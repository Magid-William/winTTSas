using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

/// <summary>
/// Reads the selection or the clipboard and speaks it. A hotkey pressed while
/// speaking stops playback instead of starting another read.
/// </summary>
public sealed class SpeechActions : IDisposable
{
    private readonly SpeechService _speech;
    private readonly SpeechPlayer _player;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _busy;

    public SpeechActions(SpeechService speech, SpeechPlayer player, ILogger log)
    {
        _speech = speech;
        _player = player;
        _log = log;
        _player.PlaybackCompleted += OnPlaybackCompleted;
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _busy || _player.IsPlaying;
            }
        }
    }

    public async Task HandleAsync(SpeechHotkeyAction action)
    {
        CancellationTokenSource cts;
        int generation;

        lock (_gate)
        {
            if (_busy)
            {
                StopCore();
                _log.LogInformation("hotkey: stopped ({Action})", action);
                return;
            }

            _busy = true;
            generation = ++_generation;
            cts = _cts = new CancellationTokenSource();
        }

        var started = false;
        try
        {
            string? text;
            if (action == SpeechHotkeyAction.SelectedText)
            {
                text = await SelectionReader.TryReadAsync(cts.Token).ConfigureAwait(false);
            }
            else
            {
                text = ClipboardText.TryGetText(out var clipboard) ? clipboard : null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _log.LogInformation("hotkey: nothing to read ({Action})", action);
                Beep();
                return;
            }

            text = Truncate(text, _speech.Config.MaxTextLength);
            _log.LogInformation("hotkey: reading {Action} ({Chars} chars)", action, text.Length);

            var result = await _speech.SynthesizeAsync(
                    text,
                    voiceRequest: null,
                    formatRequest: "wav",
                    speedRequest: null,
                    gainDbRequest: null,
                    sanitizeRequest: null,
                    cts.Token)
                .ConfigureAwait(false);

            cts.Token.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _cts = null;
            }

            _player.Play(result.Audio);
            started = true;

            _log.LogInformation(
                "hotkey: speaking {Action} ({Ms} ms, {Bytes} bytes)",
                action,
                result.ElapsedMs,
                result.Audio.Length);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("hotkey: stopped ({Action})", action);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "hotkey: could not read {Action}", action);
            Beep();
        }
        finally
        {
            if (!started)
            {
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        _busy = false;
                    }
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_busy && !_player.IsPlaying)
            {
                return;
            }

            StopCore();
        }
    }

    private void StopCore()
    {
        _generation++;
        _busy = false;
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _player.Stop();
    }

    private void OnPlaybackCompleted()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0 || text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOfAny(
            new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?' },
            maxLength - 1);

        return cut > maxLength / 2 ? text[..(cut + 1)].TrimEnd() : text[..maxLength];
    }

    private static void Beep()
    {
        try
        {
            BeepNative(880, 80);
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _player.PlaybackCompleted -= OnPlaybackCompleted;
        Stop();
    }

    [DllImport("kernel32.dll")]
    private static extern bool BeepNative(uint frequency, uint duration);
}
