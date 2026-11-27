using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

/// <summary>Plays one clip at a time on a background thread and can stop it.</summary>
public sealed class SpeechPlayer : IDisposable
{
    private readonly ILogger<SpeechPlayer>? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private volatile bool _playing;

    public SpeechPlayer(ILogger<SpeechPlayer>? log = null)
    {
        _log = log;
    }

    /// <summary>Raised on the playback thread when the current clip finishes.</summary>
    public event Action? PlaybackCompleted;

    public bool IsPlaying => _playing;

    public void Play(byte[] wav, int? deviceIndex = null)
    {
        Stop();

        var cts = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            _cts = cts;
            generation = ++_generation;
            _playing = true;
        }

        var thread = new Thread(() => Run(wav, deviceIndex, cts, generation))
        {
            IsBackground = true,
            Name = "winTTSas-speech"
        };

        thread.Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            var cts = _cts;
            _cts = null;
            _playing = false;
            cts?.Cancel();
        }
    }

    private void Run(byte[] wav, int? deviceIndex, CancellationTokenSource cts, int generation)
    {
        var current = false;
        try
        {
            WaveOutPlayer.Play(wav, deviceIndex, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // stopped by the user
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "audio playback failed");
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _playing = false;
                    _cts = null;
                    current = true;
                    cts.Dispose();
                }
            }

            if (!current)
            {
                cts.Dispose();
            }
        }

        if (current)
        {
            PlaybackCompleted?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
