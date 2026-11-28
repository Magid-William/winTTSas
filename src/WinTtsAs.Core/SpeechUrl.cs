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
    public double? Pitch { get; set; }
    public bool? Normalize { get; set; }
    public bool? Sanitize { get; set; }
}

internal sealed record SpeechUrlError(
    string Message,
    string? Param = null,
    string? Code = null);

internal static class SpeechUrl
{
    public const string AcceptedKeys =
        "voice, speed, pitch, gain_db (gain, gaindb, volume), response_format (format), input (text), sanitize, normalize, model";

    private enum Field
    {
        Voice,
        Speed,
        Gain,
        Pitch,
        Normalize,
        Format,
        Input,
        Sanitize,
        Model
    }

    /// <summary>
    /// Parses the alternating key/value segments of a URL such as
    /// <c>voice/1/speed/1.5/gain_db/-3/format/wav/text/Hello</c>.
    /// Unknown keys and empty values are errors; text may come from the query
    /// string instead (see <see cref="TryApplyQueryFallback"/>).
    /// </summary>
    public static bool TryParsePath(
        string? segments,
        out SpeechUrlValues values,
        out SpeechUrlError? error)
    {
        values = new SpeechUrlValues();
        error = null;

        var trimmed = (segments ?? "").Trim('/');
        if (trimmed.Length == 0)
        {
            return true;
        }

        var parts = trimmed.Split('/');
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

        return true;
    }

    /// <summary>
    /// Fills fields that the path did not set from the query string, so
    /// <c>/v1/audio/speech/voice/1/speed/1.2/?input=Hello</c> works. Path values
    /// always win over query values.
    /// </summary>
    public static bool TryApplyQueryFallback(
        SpeechUrlValues values,
        IEnumerable<KeyValuePair<string, StringValues>> query,
        out SpeechUrlError? error)
    {
        error = null;
        if (query is null)
        {
            return true;
        }

        foreach (var pair in query)
        {
            if (!TryMapKey(pair.Key, out var field) || !IsMissing(values, field))
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

    public static SpeechUrlError MissingTextError() => new(
        $"text is required; add /text/<text>, ?input=..., or input in the JSON body. Accepted keys: {AcceptedKeys}",
        "input",
        "input_required_for_url");

    private static bool IsMissing(SpeechUrlValues values, Field field) => field switch
    {
        Field.Input => values.Input is null,
        Field.Voice => values.Voice is null,
        Field.Format => values.Format is null,
        Field.Speed => values.Speed is null,
        Field.Gain => values.GainDb is null,
        Field.Pitch => values.Pitch is null,
        Field.Normalize => values.Normalize is null,
        Field.Sanitize => values.Sanitize is null,
        _ => false
    };

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

        if (query is null)
        {
            return true;
        }

        // Query-only requests keep "last value wins" semantics.
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

            case Field.Sanitize:
                if (!TryParseBool(value, out var sanitize))
                {
                    error = new SpeechUrlError(
                        "sanitize must be true or false.",
                        "sanitize");
                    return false;
                }

                values.Sanitize = sanitize;
                return true;

            case Field.Normalize:
                if (!TryParseBool(value, out var normalize))
                {
                    error = new SpeechUrlError(
                        "normalize must be true or false.",
                        "normalize");
                    return false;
                }

                values.Normalize = normalize;
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

            case Field.Pitch:
                if (!TryParseNumber(value, out var pitch))
                {
                    error = new SpeechUrlError(
                        "pitch must be a number between -12 and +12 semitones.",
                        "pitch");
                    return false;
                }

                values.Pitch = pitch;
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
            case "pitch":
                field = Field.Pitch;
                return true;
            case "normalize" or "loudnorm":
                field = Field.Normalize;
                return true;
            case "response_format" or "format":
                field = Field.Format;
                return true;
            case "input" or "text":
                field = Field.Input;
                return true;
            case "sanitize":
                field = Field.Sanitize;
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

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "1" or "yes":
                result = true;
                return true;
            case "false" or "off" or "0" or "no":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    // The host decodes the path except for %2F, so unescape only that, and only
    // after the remainder has been split on '/'; otherwise an encoded slash would
    // create phantom segments.
    private static string Decode(string segment) =>
        segment.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            ? segment.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
            : segment;
}
