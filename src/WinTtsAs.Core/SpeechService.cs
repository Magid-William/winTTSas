using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed record SpeechResult(
    byte[] Audio,
    string ContentType,
    string Voice,
    double Speed,
    int Rate,
    double GainDb,
    bool CacheHit,
    long ElapsedMs);

public sealed class SpeechService : IAsyncDisposable
{
    private readonly ILogger<SpeechService> _log;
    private readonly SapiWorkerPool _pool;
    private readonly AudioCache _cache;
    private readonly FfmpegTranscoder _ffmpeg;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private readonly object _reloadLock = new();
    private AppConfig _config;
    private VoiceCatalog _catalog;
    private string _configStamp;

    public SpeechService(AppConfig config, ILogger<SpeechService> log)
    {
        _config = config;
        _log = log;
        _catalog = VoiceCatalog.Load();
        _configStamp = GetConfigStamp();
        _pool = new SapiWorkerPool(config.Workers, config.QueueLimit, log);
        _cache = new AudioCache(AppPaths.CacheDir, config.CacheMaxMb, config.CacheEnabled, log);
        _ffmpeg = new FfmpegTranscoder(config.FfmpegPath, config.Mp3Bitrate, log);
        _log.LogInformation(
            "ffmpeg: {Path} ({State})",
            _ffmpeg.ResolvedPath,
            _ffmpeg.IsAvailable ? "ok" : "unavailable");
    }

    public AppConfig Config => _config;
    public IReadOnlyList<VoiceDescriptor> Voices => _catalog.Voices;
    public int NaturalVoices => _catalog.NaturalCount;
    public VoiceDescriptor? DefaultVoice => _catalog.Resolve(_config.DefaultVoice, _config);
    public bool FfmpegAvailable => _ffmpeg.IsAvailable;
    public string FfmpegPath => _ffmpeg.ResolvedPath;
    public string FfmpegVersion => _ffmpeg.IsAvailable
        ? _ffmpeg.Version ?? "available"
        : "unavailable";
    public long CacheSizeBytes => _cache.SizeBytes;
    public int CacheCount => _cache.Count;
    public int QueueCount => _pool.QueueCount;
    public int Workers => _pool.WorkerCount;

    public void Reload(AppConfig config)
    {
        lock (_reloadLock)
        {
            var catalog = VoiceCatalog.Load();
            _config = config;
            _catalog = catalog;
            _configStamp = GetConfigStamp();
            _log.LogInformation(
                "reloaded config: {Voices} voices ({Natural} natural), default={Voice}, format={Format}, speed={Speed}, gain={Gain}",
                catalog.Voices.Count,
                catalog.NaturalCount,
                catalog.Resolve(config.DefaultVoice, config)?.Name ?? "none",
                config.DefaultFormat,
                config.DefaultSpeed,
                config.GainDb);
        }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        var voice = DefaultVoice;
        if (voice is null)
        {
            return;
        }

        var jobs = Enumerable.Range(0, _pool.WorkerCount)
            .Select(_ => _pool.SynthesizeAsync(
                new SynthesisRequest("Warm up.", voice.Name, 0, _config.Volume),
                cancellationToken));

        await Task.WhenAll(jobs);
    }

    public async Task<SpeechResult> SynthesizeAsync(
        string text,
        string? voiceRequest,
        string? formatRequest,
        double? speedRequest,
        double? gainDbRequest,
        CancellationToken cancellationToken)
    {
        ReloadIfConfigChanged();

        var config = _config;
        var catalog = _catalog;

        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("input is empty", nameof(text));
        }

        if (normalized.Length > config.MaxTextLength)
        {
            throw new ArgumentException(
                $"input must be {config.MaxTextLength} characters or fewer",
                nameof(text));
        }

        var voice = catalog.Resolve(voiceRequest, config)
            ?? throw new InvalidOperationException("No SAPI voices are installed.");

        var format = AudioFormats.Normalize(formatRequest, config.DefaultFormat);
        var speed = Math.Clamp(speedRequest ?? config.DefaultSpeed, 0.25, 4.0);
        var gainDb = Math.Clamp(gainDbRequest ?? config.GainDb, -24.0, 24.0);
        var rate = SpeechRate.FromSpeed(speed);
        var cacheKey = AudioCache.Key(normalized, voice.Name, rate, config.Volume);

        var stopwatch = Stopwatch.StartNew();
        var cacheHit = false;
        var wav = _cache.TryGet(cacheKey);

        if (wav is null)
        {
            var chunks = TextChunker.Split(normalized, config.ChunkChars);
            var parts = new List<byte[]>(chunks.Count);

            foreach (var chunk in chunks)
            {
                parts.Add(await _pool.SynthesizeAsync(
                    new SynthesisRequest(chunk, voice.Name, rate, config.Volume),
                    cancellationToken));
            }

            wav = WavConcatenator.Concat(parts);
            _cache.Set(cacheKey, wav);
        }
        else
        {
            cacheHit = true;
        }

        var audio = await _ffmpeg.ToFormatAsync(
            wav,
            format,
            gainDb,
            cancellationToken);
        stopwatch.Stop();

        return new SpeechResult(
            audio,
            AudioFormats.ContentType(format),
            voice.Name,
            speed,
            rate,
            gainDb,
            cacheHit,
            stopwatch.ElapsedMilliseconds);
    }

    public StatusInfo GetStatus()
    {
        ReloadIfConfigChanged();

        var config = _config;
        var catalog = _catalog;
        var voice = catalog.Resolve(config.DefaultVoice, config);
        var adapter = AdapterProbe.Get();

        return new StatusInfo
        {
            Ok = true,
            Pid = Environment.ProcessId,
            Port = config.Port,
            BaseUrl = AppPaths.OpenAiBaseUrl(config.Port, config.Bind),
            Voice = voice?.Name ?? "",
            Format = config.DefaultFormat,
            GainDb = config.GainDb,
            Speed = config.DefaultSpeed,
            Rate = SpeechRate.FromSpeed(config.DefaultSpeed),
            Voices = catalog.Voices.Count,
            NaturalVoices = catalog.NaturalCount,
            Queue = _pool.QueueCount,
            QueueLimit = config.QueueLimit,
            Workers = _pool.WorkerCount,
            UptimeSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            Version = BuildInfo.Version,
            Ffmpeg = FfmpegVersion,
            FfmpegPath = _ffmpeg.ResolvedPath,
            Adapter = adapter.Detail,
            LogDir = AppPaths.LogDir,
            ConfigFile = AppPaths.ConfigFile
        };
    }

    private void ReloadIfConfigChanged()
    {
        var stamp = GetConfigStamp();
        if (stamp.Length == 0 ||
            string.Equals(stamp, _configStamp, StringComparison.Ordinal))
        {
            return;
        }

        lock (_reloadLock)
        {
            if (string.Equals(stamp, _configStamp, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                var config = ConfigStore.Load();
                var catalog = VoiceCatalog.Load();
                _config = config;
                _catalog = catalog;
                _configStamp = stamp;
                _log.LogInformation(
                    "hot-reloaded config: default={Voice}, format={Format}, speed={Speed}, gain={Gain}",
                    catalog.Resolve(config.DefaultVoice, config)?.Name ?? "none",
                    config.DefaultFormat,
                    config.DefaultSpeed,
                    config.GainDb);
            }
            catch (Exception ex)
            {
                _configStamp = stamp;
                _log.LogWarning(ex, "could not hot-reload config");
            }
        }
    }

    private static string GetConfigStamp()
    {
        try
        {
            var file = new FileInfo(AppPaths.ConfigFile);
            return file.Exists
                ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}"
                : "";
        }
        catch
        {
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
    }
}
