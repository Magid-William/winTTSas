using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinTtsAs.Core;

public sealed class AudioCache
{
    private readonly string _dir;
    private readonly int _maxBytes;
    private readonly bool _enabled;
    private readonly ILogger? _log;
    private readonly object _cleanupLock = new();

    public AudioCache(string dir, int maxMb, bool enabled, ILogger? log = null)
    {
        _dir = dir;
        _enabled = enabled && maxMb > 0;
        _maxBytes = Math.Max(0, maxMb) * 1024 * 1024;
        _log = log;

        if (_enabled)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                Cleanup();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "could not initialize audio cache");
                _enabled = false;
            }
        }
    }

    public bool Enabled => _enabled;

    public static string Key(string text, string voice, int rate, int volume)
    {
        var raw = $"v1|{voice}|{rate}|{volume}|{text}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
    }

    public byte[]? TryGet(string key)
    {
        if (!_enabled)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(_dir, key + ".wav");
            if (!File.Exists(path))
            {
                return null;
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "cache read failed");
            return null;
        }
    }

    public void Set(string key, byte[] wav)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, key + ".wav");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, wav);
            File.Move(temp, path, overwrite: true);
            Cleanup();
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "cache write failed");
        }
    }

    public long SizeBytes
    {
        get
        {
            if (!_enabled)
            {
                return 0;
            }

            try
            {
                return new DirectoryInfo(_dir)
                    .EnumerateFiles("*.wav")
                    .Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }
    }

    public int Count
    {
        get
        {
            if (!_enabled)
            {
                return 0;
            }

            try
            {
                return new DirectoryInfo(_dir).EnumerateFiles("*.wav").Count();
            }
            catch
            {
                return 0;
            }
        }
    }

    private void Cleanup()
    {
        if (!_enabled || _maxBytes == 0)
        {
            return;
        }

        lock (_cleanupLock)
        {
            try
            {
                var files = new DirectoryInfo(_dir)
                    .EnumerateFiles("*.wav")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                var total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                {
                    return;
                }

                var target = (long)(_maxBytes * 0.8);
                foreach (var file in files)
                {
                    if (total <= target)
                    {
                        break;
                    }

                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                    catch
                    {
                        // keep going
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "cache cleanup failed");
            }
        }
    }
}
