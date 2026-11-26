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

    public FfmpegTranscoder(string path, string mp3Bitrate, ILogger? log = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path.Trim();
        _mp3Bitrate = string.IsNullOrWhiteSpace(mp3Bitrate) ? "64k" : mp3Bitrate.Trim();
        _log = log;
        IsAvailable = Probe();
    }

    public bool IsAvailable { get; }
    public string? Version { get; private set; }

    public async Task<byte[]> ToFormatAsync(
        byte[] wav,
        string format,
        double gainDb,
        CancellationToken cancellationToken)
    {
        format = AudioFormats.Normalize(format);
        var filter = BuildGainFilter(gainDb);

        if (format == "wav" && filter is null)
        {
            return wav;
        }

        if (!IsAvailable)
        {
            throw new FfmpegUnavailableException(
                $"ffmpeg is required for {format} or gain; install ffmpeg or set FfmpegPath.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in BuildArguments(format, filter))
        {
            startInfo.ArgumentList.Add(arg);
        }

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

        var stdoutTask = ReadAllAsync(process.StandardOutput.BaseStream, cancellationToken);
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

        return output;
    }

    private static string? BuildGainFilter(double gainDb)
    {
        if (Math.Abs(gainDb) < 0.05)
        {
            return null;
        }

        var db = gainDb.ToString("0.###", CultureInfo.InvariantCulture);
        return $"volume={db}dB,alimiter=limit=0.84:level=false:latency=true";
    }

    private IEnumerable<string> BuildArguments(string format, string? filter)
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

        yield return "pipe:1";
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
