using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace WinTtsAs.Core;

internal sealed class SpeechUrlValues
{
    public string? Input { get; set; }
    public string? Voice { get; set; }
    public string? Format { get; set; }
    public double? Speed { get; set; }
    public double? GainDb { get; set; }
}

internal sealed record SpeechUrlError(
    string Message,
    string? Param = null,
    string? Code = null);

internal static class SpeechUrl
{
    public const string AcceptedKeys =
        "voice, speed, gain_db (gain, gaindb, volume), response_format (format), input (text), model";

    private enum Field
    {
        Voice,
        Speed,
        Gain,
        Format,
        Input,
        Model
    }

    /// <summary>
    /// Parses the alternating key/value segments of a URL such as
    /// <c>voice/1/speed/1.5/gain_db/-3/format/wav/text/Hello</c>.
    /// Unknown keys and empty values are errors.
    /// </summary>
    public static bool TryParsePath(
        string? segments,
        out SpeechUrlValues values,
        out SpeechUrlError? error)
    {
        values = new SpeechUrlValues();
        error = null;

        if (string.IsNullOrWhiteSpace(segments))
        {
            error = MissingText();
            return false;
        }

        var parts = segments.Split('/');
        if (parts.Length % 2 != 0)
        {
            error = new SpeechUrlError(
                $"URL must contain key/value pairs; got {parts.Length} segments. Accepted keys: {AcceptedKeys}",
                null,
                "invalid_url");
            return false;
        }

        for (var i = 0; i < parts.Length; i += 2)
        {
            var key = Decode(parts[i]).Trim();
            var value = Decode(parts[i + 1]);

            if (key.Length == 0)
            {
                error = new SpeechUrlError(
                    $"missing URL key at segment {i + 1}. Accepted keys: {AcceptedKeys}",
                    null,
                    "invalid_url");
                return false;
            }

            if (!TryMapKey(key, out var field))
            {
                error = new SpeechUrlError(
                    $"unknown URL key \"{key}\". Accepted keys: {AcceptedKeys}",
                    key,
                    "invalid_url");
                return false;
            }

            if (value.Length == 0)
            {
                error = new SpeechUrlError(
                    $"missing value for \"{key}\".",
                    key,
                    "invalid_url");
                return false;
            }

            if (!Apply(values, field, value, out error))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(values.Input))
        {
            error = MissingText();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses query parameters such as <c>?input=Hello&amp;voice=1&amp;speed=1.2</c>.
    /// Unrecognized keys are ignored so unrelated parameters (token, cache busters)
    /// do not break the request. Later values win.
    /// </summary>
    public static bool TryParseQuery(
        IEnumerable<KeyValuePair<string, StringValues>> query,
        out SpeechUrlValues values,
        out SpeechUrlError? error)
    {
        values = new SpeechUrlValues();
        error = null;

        foreach (var pair in query)
        {
            if (!TryMapKey(pair.Key, out var field))
            {
                continue;
            }

            var value = pair.Value.Count > 0 ? pair.Value[^1] ?? "" : "";
            if (!Apply(values, field, value, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Apply(
        SpeechUrlValues values,
        Field field,
        string value,
        out SpeechUrlError? error)
    {
        error = null;

        switch (field)
        {
            case Field.Voice:
                values.Voice = value;
                return true;

            case Field.Format:
                values.Format = value;
                return true;

            case Field.Input:
                values.Input = value;
                return true;

            case Field.Speed:
                if (!TryParseNumber(value, out var speed))
                {
                    error = new SpeechUrlError(
                        "speed must be a number between 0.25 and 4.0.",
                        "speed");
                    return false;
                }

                values.Speed = speed;
                return true;

            case Field.Gain:
                if (!TryParseNumber(value, out var gain))
                {
                    error = new SpeechUrlError(
                        "gain_db must be a number between -24 and +24.",
                        "gain_db");
                    return false;
                }

                values.GainDb = gain;
                return true;

            case Field.Model:
            default:
                return true;
        }
    }

    private static bool TryMapKey(string key, out Field field)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case "voice":
                field = Field.Voice;
                return true;
            case "speed":
                field = Field.Speed;
                return true;
            case "gain_db" or "gaindb" or "gain" or "volume":
                field = Field.Gain;
                return true;
            case "response_format" or "format":
                field = Field.Format;
                return true;
            case "input" or "text":
                field = Field.Input;
                return true;
            case "model":
                field = Field.Model;
                return true;
            default:
                field = default;
                return false;
        }
    }

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        double.IsFinite(number);

    // The host decodes the path except for %2F, so unescape only that, and only
    // after the remainder has been split on '/'; otherwise an encoded slash would
    // create phantom segments.
    private static string Decode(string segment) =>
        segment.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            ? segment.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
            : segment;

    private static SpeechUrlError MissingText() => new(
        $"text is required; add /text/<text> (or ?input=...). Accepted keys: {AcceptedKeys}",
        "input",
        "input_required_for_url");
}
