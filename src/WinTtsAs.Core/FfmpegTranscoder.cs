using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed class FfmpegUnavailableException : Exception
{
    public FfmpegUnavailableException(string message) : base(message)
    {
    }
}

public sealed class FfmpegTranscoder
{
    private readonly string _path;
    private readonly string _mp3Bitrate;
    private readonly ILogger? _log;
    private readonly object _filterLock = new();
    private HashSet<string>? _filters;

    public FfmpegTranscoder(string path, string mp3Bitrate, ILogger? log = null)
    {
        _path = AppPaths.ResolveFfmpegPath(path);
        _mp3Bitrate = string.IsNullOrWhiteSpace(mp3Bitrate) ? "64k" : mp3Bitrate.Trim();
        _log = log;
        IsAvailable = Probe();
    }

    public bool IsAvailable { get; }
    public string ResolvedPath => _path;
    public string? Version { get; private set; }

    public async Task<byte[]> ToFormatAsync(
        byte[] wav,
        string format,
        double gainDb,
        double pitchSemitones,
        bool normalize,
        CancellationToken cancellationToken)
    {
        format = AudioFormats.Normalize(format);
        var hasGain = Math.Abs(gainDb) >= 0.05;
        var hasPitch = Math.Abs(pitchSemitones) >= 0.05;
        var hasFilter = hasGain || hasPitch || normalize;

        if (format == "wav" && !hasFilter)
        {
            return wav;
        }

        if (!IsAvailable)
        {
            var needs = new List<string>();
            if (format != "wav")
            {
                needs.Add(format);
            }

            if (hasPitch)
            {
                needs.Add("pitch");
            }

            if (hasGain)
            {
                needs.Add("gain");
            }

            if (normalize)
            {
                needs.Add("loudness normalization");
            }

            throw new FfmpegUnavailableException(
                $"ffmpeg is required for {string.Join(", ", needs)}; the bundled copy is " +
                "missing and no ffmpeg was found on PATH. Reinstall winTTSas or set FfmpegPath.");
        }

        var filter = BuildFilter(gainDb, pitchSemitones, normalize);

        // A WAV written to a pipe has placeholder RIFF/data sizes because ffmpeg
        // cannot seek back to patch them. Write WAV to a temp file so every
        // consumer gets a valid header.
        var isWav = format == "wav";
        var tempPath = isWav
            ? Path.Combine(Path.GetTempPath(), $"winTTSas-{Guid.NewGuid():N}.wav")
            : null;
        var outputTarget = tempPath ?? "pipe:1";

        var startInfo = new ProcessStartInfo
        {
            FileName = _path,
            RedirectStandardInput = true,
            RedirectStandardOutput = !isWav,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in BuildArguments(format, filter, outputTarget))
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // best effort
                }
            });

            var stdoutTask = isWav
                ? Task.FromResult(Array.Empty<byte>())
                : ReadAllAsync(process.StandardOutput.BaseStream, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.StandardInput.BaseStream.WriteAsync(wav, cancellationToken);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // ffmpeg may exit early; the exit code/stderr below is the useful part.
            }

            var output = await stdoutTask;
            var error = await stderrTask;
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "ffmpeg failed: " + (string.IsNullOrWhiteSpace(error) ? $"exit {process.ExitCode}" : error.Trim()));
            }

            if (isWav)
            {
                return await File.ReadAllBytesAsync(tempPath!, cancellationToken);
            }

            return output;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private string? BuildFilter(double gainDb, double pitchSemitones, bool normalize)
    {
        var parts = new List<string>(4);

        if (Math.Abs(pitchSemitones) >= 0.05)
        {
            RequireFilter("rubberband", "pitch");
            var ratio = Math.Pow(2, pitchSemitones / 12.0)
                .ToString("0.######", CultureInfo.InvariantCulture);
            parts.Add($"rubberband=pitch={ratio}:pitchq=quality:formant=preserved");
        }

        if (normalize)
        {
            RequireFilter("loudnorm", "loudness normalization");
            parts.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        if (Math.Abs(gainDb) >= 0.05)
        {
            var db = gainDb.ToString("0.###", CultureInfo.InvariantCulture);
            parts.Add($"volume={db}dB");
            parts.Add("alimiter=limit=0.84:level=false:latency=true");
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    /// <summary>
    /// The bundled ffmpeg is the GPL "full" build, which ships rubberband and
    /// loudnorm. A hand-installed ffmpeg from PATH may not, so name the missing
    /// filter instead of surfacing ffmpeg's own error.
    /// </summary>
    private void RequireFilter(string filter, string feature)
    {
        if (HasFilter(filter))
        {
            return;
        }

        throw new FfmpegUnavailableException(
            $"ffmpeg has no {filter} filter, which {feature} needs. " +
            "Use the bundled full ffmpeg build or point FfmpegPath at one.");
    }

    private bool HasFilter(string name)
    {
        HashSet<string> filters;
        lock (_filterLock)
        {
            _filters ??= ProbeFilters();
            filters = _filters;
        }

        return filters.Contains(name);
    }

    private HashSet<string> ProbeFilters()
    {
        var filters = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-filters");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return filters;
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return filters;
            }

            _ = stderrTask;

            foreach (var line in output.Split('\n'))
            {
                // " T.. rubberband  A->A  Apply time-stretching and pitch-shifting."
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 3 && tokens[0].Contains('.'))
                {
                    filters.Add(tokens[1]);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not probe ffmpeg filters");
        }

        return filters;
    }

    private IEnumerable<string> BuildArguments(
        string format,
        string? filter,
        string outputTarget)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-i";
        yield return "pipe:0";
        yield return "-ac";
        yield return "1";

        if (filter is not null)
        {
            yield return "-af";
            yield return filter;
        }

        switch (format)
        {
            case "wav":
                yield return "-ar";
                yield return "24000";
                yield return "-c:a";
                yield return "pcm_s16le";
                yield return "-f";
                yield return "wav";
                break;

            case "mp3":
                yield return "-ar";
                yield return "24000";
                yield return "-c:a";
                yield return "libmp3lame";
                yield return "-b:a";
                yield return _mp3Bitrate;
                yield return "-f";
                yield return "mp3";
                break;

            case "opus":
                yield return "-ar";
                yield return "48000";
                yield return "-c:a";
                yield return "libopus";
                yield return "-b:a";
                yield return "48k";
                yield return "-f";
                yield return "ogg";
                break;

            case "aac":
                yield return "-ar";
                yield return "24000";
                yield return "-c:a";
                yield return "aac";
                yield return "-b:a";
                yield return _mp3Bitrate;
                yield return "-f";
                yield return "adts";
                break;

            case "flac":
                yield return "-ar";
                yield return "24000";
                yield return "-c:a";
                yield return "flac";
                yield return "-f";
                yield return "flac";
                break;

            case "pcm":
                yield return "-ar";
                yield return "24000";
                yield return "-f";
                yield return "s16le";
                break;

            default:
                throw new ArgumentException($"Unsupported format: {format}", nameof(format));
        }

        yield return "-y";
        yield return outputTarget;
    }

    private bool Probe()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Version = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ffmpeg not found at {Path}", _path);
            return false;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
