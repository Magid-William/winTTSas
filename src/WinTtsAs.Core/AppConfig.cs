using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTtsAs.Core;

public sealed class AppConfig
{
    public int Port { get; set; } = 5178;
    public string Bind { get; set; } = "127.0.0.1";
    public string DefaultVoice { get; set; } = "";
    public double DefaultSpeed { get; set; } = 1.0;
    public string DefaultFormat { get; set; } = "mp3";
    public double GainDb { get; set; } = 0;
    public int MaxTextLength { get; set; } = 4096;
    public int ChunkChars { get; set; } = 1200;
    public int Workers { get; set; } = 2;
    public int QueueLimit { get; set; } = 20;
    public int Volume { get; set; } = 100;
    public bool CacheEnabled { get; set; } = true;
    public int CacheMaxMb { get; set; } = 200;
    public string Token { get; set; } = "";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string Mp3Bitrate { get; set; } = "64k";
    public Dictionary<string, string> VoiceMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Sanitize { get; set; } = true;
    public string Markdown { get; set; } = "auto";
    public string UrlMode { get; set; } = "link";
    public string CodeMode { get; set; } = "label";
    public string InlineCodeMode { get; set; } = "speak";
    public string TableMode { get; set; } = "flatten";
    public Dictionary<string, string> TextReplacements { get; set; } = new(StringComparer.Ordinal);
    public HotkeySettings Hotkeys { get; set; } = new();

    public void Normalize()
    {
        if (Port is < 1024 or > 65535)
        {
            Port = 5178;
        }

        Bind = string.IsNullOrWhiteSpace(Bind) ? "127.0.0.1" : Bind.Trim();
        DefaultFormat = AudioFormats.Normalize(DefaultFormat, "mp3");
        if (!double.IsFinite(GainDb))
        {
            GainDb = 0;
        }

        if (!double.IsFinite(DefaultSpeed))
        {
            DefaultSpeed = 1.0;
        }

        GainDb = Math.Round(Math.Clamp(GainDb, -24.0, 24.0), 1);
        DefaultSpeed = Math.Clamp(DefaultSpeed, 0.25, 4.0);
        MaxTextLength = Math.Clamp(MaxTextLength, 1, 16_000);
        ChunkChars = Math.Clamp(ChunkChars, 200, Math.Max(200, MaxTextLength));
        Workers = Math.Clamp(Workers, 1, 4);
        QueueLimit = Math.Clamp(QueueLimit, 1, 100);
        Volume = Math.Clamp(Volume, 0, 100);
        CacheMaxMb = Math.Clamp(CacheMaxMb, 0, 10_000);
        Token = Token?.Trim() ?? "";
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();
        Mp3Bitrate = string.IsNullOrWhiteSpace(Mp3Bitrate) ? "64k" : Mp3Bitrate.Trim();
        DefaultVoice = DefaultVoice?.Trim() ?? "";

        Markdown = NormalizeChoice(Markdown, "auto", "auto", "on", "off");
        UrlMode = NormalizeChoice(UrlMode, "link", "skip", "link", "domain", "speak");
        CodeMode = NormalizeChoice(CodeMode, "label", "label", "skip", "speak");
        InlineCodeMode = NormalizeChoice(InlineCodeMode, "speak", "speak", "label", "skip");
        TableMode = NormalizeChoice(TableMode, "flatten", "flatten", "skip");

        Hotkeys ??= new HotkeySettings();
        Hotkeys.SelectedText = NormalizeHotkey(Hotkeys.SelectedText, "Ctrl+Alt+R");
        Hotkeys.Clipboard = NormalizeHotkey(Hotkeys.Clipboard, "Ctrl+Alt+C");

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TextReplacements is not null)
        {
            foreach (var pair in TextReplacements)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    replacements[pair.Key] = pair.Value ?? "";
                }
            }
        }

        TextReplacements = replacements;

        var normalizedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (VoiceMap is not null)
        {
            foreach (var pair in VoiceMap)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    normalizedMap[pair.Key.Trim()] = pair.Value.Trim();
                }
            }
        }

        VoiceMap = normalizedMap;
    }

    private static string NormalizeChoice(
        string? value,
        string fallback,
        params string[] allowed)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : fallback;
    }

    private static HotkeyBindingSettings NormalizeHotkey(
        HotkeyBindingSettings? binding,
        string fallback)
    {
        binding ??= new HotkeyBindingSettings();

        var shortcut = binding.Shortcut?.Trim() ?? "";
        if (shortcut.Length == 0)
        {
            shortcut = fallback;
        }
        else if (HotkeyChord.TryParse(shortcut, out var chord, out _))
        {
            shortcut = chord!.Display;
        }

        binding.Shortcut = shortcut;
        return binding;
    }
}

public sealed class HotkeySettings
{
    public HotkeyBindingSettings SelectedText { get; set; } = new()
    {
        Shortcut = "Ctrl+Alt+R"
    };

    public HotkeyBindingSettings Clipboard { get; set; } = new()
    {
        Shortcut = "Ctrl+Alt+C"
    };
}

public sealed class HotkeyBindingSettings
{
    public bool Enabled { get; set; } = true;
    public string Shortcut { get; set; } = "";

    /// <summary>The normalized chord when enabled and parseable, otherwise null.</summary>
    [JsonIgnore]
    public string? ActiveChord =>
        Enabled && HotkeyChord.TryParse(Shortcut, out var chord, out _) ? chord!.Display : null;
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            var fresh = new AppConfig();
            fresh.Normalize();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("config.json is empty");
            config.Normalize();
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid config file {AppPaths.ConfigFile}: {ex.Message}", ex);
        }
    }

    public static void Save(AppConfig config)
    {
        config.Normalize();
        Directory.CreateDirectory(AppPaths.ConfigDir);

        var temp = AppPaths.ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, AppPaths.ConfigFile, overwrite: true);
    }
}
