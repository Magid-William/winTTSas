using System.Runtime.InteropServices;

namespace WinTtsAs.Core;

public readonly record struct HotkeyCaptureResult(HotkeyChord? Chord, string? Error, bool Cancelled);

/// <summary>
/// Waits for the user to press a shortcut and reports it. Runs a low-level
/// keyboard hook so the chord does not reach the focused window or other
/// hotkeys.
/// </summary>
public static class HotkeyCapture
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlkhfInjected = 0x10;
    private const int VkEscape = 0x1B;

    public static Task<HotkeyCaptureResult> CaptureAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.Run(() => Capture(timeout, cancellationToken), CancellationToken.None);

    public static HotkeyCaptureResult Capture(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<HotkeyCaptureResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new ManualResetEventSlim(false);
        var threadId = 0u;

        var thread = new Thread(() => HookLoop(completion, ready, id => threadId = id))
        {
            IsBackground = true,
            Name = "winTTSas-capture"
        };

        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            return new HotkeyCaptureResult(null, "could not start the keyboard hook", false);
        }

        using var timer = new Timer(
            _ =>
            {
                PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                completion.TrySetResult(new HotkeyCaptureResult(null, null, true));
            },
            null,
            timeout,
            Timeout.InfiniteTimeSpan);

        using var registration = cancellationToken.Register(() =>
        {
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            completion.TrySetResult(new HotkeyCaptureResult(null, null, true));
        });

        completion.Task.GetAwaiter().GetResult();
        thread.Join(TimeSpan.FromSeconds(2));

        return completion.Task.Result;
    }

    private static void HookLoop(
        TaskCompletionSource<HotkeyCaptureResult> completion,
        ManualResetEventSlim ready,
        Action<uint> setThreadId)
    {
        LowLevelKeyboardProc callback = (nCode, wParam, lParam) =>
        {
            if (nCode < 0)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var message = (int)wParam;
            if (message is not (WmKeyDown or WmSysKeyDown))
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if ((data.Flags & LlkhfInjected) != 0 || IsModifier(data.VirtualKey))
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            if (data.VirtualKey == VkEscape)
            {
                completion.TrySetResult(new HotkeyCaptureResult(null, null, true));
                PostQuitMessage(0);
                return (IntPtr)1;
            }

            var modifiers = CurrentModifiers();
            if (modifiers == HotkeyModifiers.None)
            {
                completion.TrySetResult(new HotkeyCaptureResult(
                    null,
                    "a shortcut needs Ctrl, Alt, Shift, or Win",
                    false));
                PostQuitMessage(0);
                return (IntPtr)1;
            }

            if (!HotkeyChord.TryFromVirtualKey(modifiers, data.VirtualKey, out var chord))
            {
                completion.TrySetResult(new HotkeyCaptureResult(
                    null,
                    $"that key cannot be used: {HotkeyChord.NameForKey(data.VirtualKey) ?? "unknown"}",
                    false));
                PostQuitMessage(0);
                return (IntPtr)1;
            }

            completion.TrySetResult(new HotkeyCaptureResult(chord, null, false));
            PostQuitMessage(0);
            return (IntPtr)1;
        };

        var hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            completion.TrySetResult(new HotkeyCaptureResult(
                null,
                "could not install the keyboard hook",
                false));
            ready.Set();
            return;
        }

        setThreadId(GetCurrentThreadId());
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        ready.Set();

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(0x11))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(0x12))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(0x10))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(0x5B) || IsDown(0x5C))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsModifier(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or     // Shift, Ctrl, Alt
        0x14 or                     // CapsLock
        0x5B or 0x5C or             // left/right Win
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
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

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelKeyboardProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out Message message,
        IntPtr window,
        uint filterMin,
        uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

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

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
