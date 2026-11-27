using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public enum SpeechHotkeyAction
{
    SelectedText,
    Clipboard
}

public sealed record HotkeyBinding(SpeechHotkeyAction Action, HotkeyChord Chord);

/// <summary>
/// Owns the global shortcut registrations used by the server. Registration
/// must happen on the thread that pumps the message loop, so each applied set
/// gets its own thread.
/// </summary>
public sealed class HotkeyManager : IAsyncDisposable
{
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;

    private readonly Func<SpeechHotkeyAction, Task> _handler;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotkeyListener? _listener;
    private string _signature = "";

    public HotkeyManager(Func<SpeechHotkeyAction, Task> handler, ILogger log)
    {
        _handler = handler;
        _log = log;
    }

    /// <summary>Why the last registration was not fully active, if anything.</summary>
    public string? Error { get; private set; }

    public async Task<string?> ApplyAsync(AppConfig config, bool force = false)
    {
        var signature = string.Concat(
            config.Hotkeys.SelectedText.Enabled ? "1" : "0",
            config.Hotkeys.SelectedText.Shortcut,
            "|",
            config.Hotkeys.Clipboard.Enabled ? "1" : "0",
            config.Hotkeys.Clipboard.Shortcut);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && signature == _signature)
            {
                return Error;
            }

            if (_listener is not null)
            {
                await _listener.StopAsync().ConfigureAwait(false);
                _listener = null;
            }

            _signature = signature;

            var bindings = new List<HotkeyBinding>(2);
            var errors = new List<string>();

            Add(SpeechHotkeyAction.SelectedText, config.Hotkeys.SelectedText);
            Add(SpeechHotkeyAction.Clipboard, config.Hotkeys.Clipboard);

            if (bindings.Count == 0)
            {
                Error = errors.Count > 0 ? string.Join("; ", errors) : null;
                _log.LogInformation("global hotkeys are off");
                return Error;
            }

            var listener = new HotkeyListener(bindings, _handler, _log);
            var registrationError = await listener.StartAsync().ConfigureAwait(false);
            _listener = listener;

            if (registrationError is not null)
            {
                errors.Add(registrationError);
            }

            Error = errors.Count > 0 ? string.Join("; ", errors) : null;

            if (Error is null)
            {
                _log.LogInformation(
                    "global hotkeys registered: {Hotkeys}",
                    string.Join(", ", bindings.Select(binding => binding.Chord.Display)));
            }
            else
            {
                _log.LogWarning("global hotkeys: {Error}", Error);
            }

            return Error;

            void Add(SpeechHotkeyAction action, HotkeyBindingSettings settings)
            {
                if (!settings.Enabled)
                {
                    return;
                }

                if (!HotkeyChord.TryParse(settings.Shortcut, out var chord, out var error))
                {
                    errors.Add(error!);
                    return;
                }

                bindings.Add(new HotkeyBinding(action, chord!));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is not null)
            {
                await _listener.StopAsync().ConfigureAwait(false);
                _listener = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private sealed class HotkeyListener
    {
        private readonly IReadOnlyList<HotkeyBinding> _bindings;
        private readonly Func<SpeechHotkeyAction, Task> _handler;
        private readonly ILogger _log;
        private readonly TaskCompletionSource<string?> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, SpeechHotkeyAction> _actions = new();
        private Thread? _thread;
        private uint _threadId;

        public HotkeyListener(
            IReadOnlyList<HotkeyBinding> bindings,
            Func<SpeechHotkeyAction, Task> handler,
            ILogger log)
        {
            _bindings = bindings;
            _handler = handler;
            _log = log;
        }

        public async Task<string?> StartAsync()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "winTTSas-hotkeys"
            };

            _thread.Start();
            return await _ready.Task.ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            if (_thread is null)
            {
                return;
            }

            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _thread = null;
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);

            var errors = new List<string>();

            for (var index = 0; index < _bindings.Count; index++)
            {
                var binding = _bindings[index];
                var id = index + 1;
                var flags = (uint)binding.Chord.Modifiers | ModNoRepeat;

                if (RegisterHotKey(IntPtr.Zero, id, flags, binding.Chord.VirtualKey))
                {
                    _actions[id] = binding.Action;
                }
                else
                {
                    errors.Add($"{binding.Chord.Display}: {DescribeError(Marshal.GetLastWin32Error())}");
                }
            }

            _ready.TrySetResult(errors.Count == 0 ? null : string.Join("; ", errors));

            if (_actions.Count == 0)
            {
                return;
            }

            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Type != WmHotkey ||
                        !_actions.TryGetValue((int)message.WParam, out var action))
                    {
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _handler(action).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "hotkey handler failed");
                        }
                    });
                }
            }
            finally
            {
                foreach (var id in _actions.Keys)
                {
                    UnregisterHotKey(IntPtr.Zero, id);
                }

                _actions.Clear();
            }
        }

        private static string DescribeError(int code) => code switch
        {
            1409 => "already registered by another application",
            5 => "access denied",
            _ => new Win32Exception(code).Message
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Type;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out Message message,
        IntPtr window,
        uint filterMin,
        uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(
        out Message message,
        IntPtr window,
        uint filterMin,
        uint filterMax,
        uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint threadId,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
