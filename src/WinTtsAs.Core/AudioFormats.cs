namespace WinTtsAs.Core;

public static class AudioFormats
{
    public static readonly string[] All = ["mp3", "wav", "opus", "aac", "flac", "pcm"];

    public static bool IsSupported(string? format) =>
        (format ?? "").Trim().ToLowerInvariant() switch
        {
            "mp3" or "mpeg" or
            "wav" or "wave" or
            "opus" or "ogg" or "oga" or
            "aac" or "m4a" or
            "flac" or
            "pcm" or "s16le" => true,
            _ => false
        };

    public static string Normalize(string? format, string fallback = "mp3")
    {
        var value = (format ?? string.Empty).Trim().ToLowerInvariant();

        value = value switch
        {
            "mpeg" or "mp3" => "mp3",
            "wave" or "wav" => "wav",
            "ogg" or "oga" or "opus" => "opus",
            "m4a" or "aac" => "aac",
            "flac" => "flac",
            "pcm" or "s16le" => "pcm",
            _ => string.Empty
        };

        if (value.Length > 0)
        {
            return value;
        }

        var fallbackValue = (fallback ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(fallbackValue, StringComparer.OrdinalIgnoreCase) ? fallbackValue : "mp3";
    }

    public static string ContentType(string format) => Normalize(format) switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "opus" => "audio/ogg",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        _ => "application/octet-stream"
    };

    public static string Extension(string format) => Normalize(format) switch
    {
        "mp3" => ".mp3",
        "wav" => ".wav",
        "opus" => ".opus",
        "aac" => ".aac",
        "flac" => ".flac",
        "pcm" => ".pcm",
        _ => ".bin"
    };
}
