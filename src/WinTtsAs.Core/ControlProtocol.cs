using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTtsAs.Core;

public sealed class ControlRequest
{
    public string Cmd { get; set; } = "";
}

public sealed class ControlResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Stop { get; set; }
    public StatusInfo? Status { get; set; }
}

public sealed record StatusInfo
{
    public bool Ok { get; init; } = true;
    public int Pid { get; init; }
    public int Port { get; init; }
    public string BaseUrl { get; init; } = "";
    public string Voice { get; init; } = "";
    public string Format { get; init; } = "";
    public double GainDb { get; init; }
    public double PitchSemitones { get; init; }
    public bool Normalize { get; init; }
    public double Speed { get; init; }
    public int Rate { get; init; }
    public int Voices { get; init; }
    public int NaturalVoices { get; init; }
    public int Queue { get; init; }
    public int QueueLimit { get; init; }
    public int Workers { get; init; }
    public double UptimeSeconds { get; init; }
    public string Version { get; init; } = "";
    public string Ffmpeg { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public bool Sanitize { get; init; } = true;
    public string Markdown { get; init; } = "auto";
    public string Adapter { get; init; } = "";
    public string LogDir { get; init; } = "";
    public string ConfigFile { get; init; } = "";
    public string SelectedTextHotkey { get; init; } = "";
    public string ClipboardHotkey { get; init; } = "";
    public string HotkeyError { get; init; } = "";
    public bool Speaking { get; init; }
}

public static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public static class ControlPipeNames
{
    public static string ForCurrentUser()
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var dataDir = Environment.GetEnvironmentVariable("WINTTSAS_DATA_DIR");
        var scope = string.IsNullOrWhiteSpace(dataDir)
            ? user
            : user + "|" + Path.GetFullPath(dataDir);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))
            .ToLowerInvariant()[..12];
        return $"winTTSas-{hash}";
    }
}
