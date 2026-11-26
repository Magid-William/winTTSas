using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTtsAs.Core;

public sealed record VoiceDescriptor(
    string Name,
    string Slug,
    string SapiId,
    string Description,
    string Language,
    string Gender,
    bool IsNatural,
    int SampleRate);

public enum PreferredGender
{
    Any,
    Male,
    Female
}

public sealed class VoiceCatalog
{
    public IReadOnlyList<VoiceDescriptor> Voices { get; }
    public int NaturalCount => Voices.Count(v => v.IsNatural);

    private VoiceCatalog(IReadOnlyList<VoiceDescriptor> voices)
    {
        Voices = voices;
    }

    public static VoiceCatalog Load()
    {
        return StaRunner.Run(() =>
        {
            using var synth = new SpeechSynthesizer();
            var voices = new List<VoiceDescriptor>();
            var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var installed in synth.GetInstalledVoices())
            {
                if (!installed.Enabled)
                {
                    continue;
                }

                var info = installed.VoiceInfo;
                var description = info.Description ?? "";
                var natural = description.Contains("(Natural", StringComparison.OrdinalIgnoreCase) ||
                              info.Id.StartsWith("MicrosoftWindows.Voice.", StringComparison.OrdinalIgnoreCase);

                var sampleRate = 0;
                try
                {
                    sampleRate = info.SupportedAudioFormats.FirstOrDefault()?.SamplesPerSecond ?? 0;
                }
                catch
                {
                    // Some third-party engines do not expose audio formats.
                }

                var slug = Slugify(info.Name);
                if (!usedSlugs.Add(slug))
                {
                    var n = 2;
                    while (!usedSlugs.Add($"{slug}-{n}"))
                    {
                        n++;
                    }

                    slug = $"{slug}-{n}";
                }

                voices.Add(new VoiceDescriptor(
                    info.Name,
                    slug,
                    info.Id,
                    description,
                    info.Culture?.Name ?? "",
                    info.Gender.ToString(),
                    natural,
                    sampleRate));
            }

            return new VoiceCatalog(voices
                .OrderByDescending(v => v.IsNatural)
                .ThenBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        });
    }

    public VoiceDescriptor? Resolve(string? requested, AppConfig config)
    {
        var fallback = PickDefault(config.DefaultVoice);

        if (string.IsNullOrWhiteSpace(requested))
        {
            return fallback;
        }

        var query = requested.Trim();
        if (query.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            query.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        if (config.VoiceMap is not null &&
            config.VoiceMap.TryGetValue(query, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            var mappedVoice = FindExact(mapped);
            if (mappedVoice is not null)
            {
                return mappedVoice;
            }
        }

        var exact = FindExact(query);
        if (exact is not null)
        {
            return exact;
        }

        var azureName = ExtractAzureName(query);
        if (azureName is not null)
        {
            var azureMatch = Voices.FirstOrDefault(v =>
                v.Name.Contains(azureName, StringComparison.OrdinalIgnoreCase) ||
                v.Slug.Contains(Slugify(azureName), StringComparison.OrdinalIgnoreCase));
            if (azureMatch is not null)
            {
                return azureMatch;
            }
        }

        if (OpenAiVoiceAliases.TryGet(query, out var preferredGender))
        {
            var aliased = FirstByGender(preferredGender);
            if (aliased is not null)
            {
                return aliased;
            }
        }

        var slugQuery = Slugify(query);
        var contains = Voices.FirstOrDefault(v =>
            v.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            v.Slug.Contains(slugQuery, StringComparison.OrdinalIgnoreCase));
        if (contains is not null)
        {
            return contains;
        }

        return fallback;
    }

    public VoiceDescriptor? FindExact(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return Voices.FirstOrDefault(v =>
                   string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)) ??
               Voices.FirstOrDefault(v =>
                   string.Equals(v.Slug, trimmed, StringComparison.OrdinalIgnoreCase)) ??
               Voices.FirstOrDefault(v =>
                   string.Equals(v.Slug, Slugify(trimmed), StringComparison.OrdinalIgnoreCase));
    }

    private VoiceDescriptor? PickDefault(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var exact = FindExact(preferred);
            if (exact is not null)
            {
                return exact;
            }
        }

        return Voices.FirstOrDefault(v => v.IsNatural) ?? Voices.FirstOrDefault();
    }

    private VoiceDescriptor? FirstByGender(PreferredGender gender)
    {
        if (gender == PreferredGender.Any)
        {
            return null;
        }

        var target = gender == PreferredGender.Male ? "Male" : "Female";
        return Voices
            .Where(v => v.IsNatural)
            .FirstOrDefault(v => v.Gender.Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractAzureName(string query)
    {
        if (!query.EndsWith("Neural", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lastDash = query.LastIndexOf('-');
        if (lastDash < 0 || lastDash == query.Length - 1)
        {
            return null;
        }

        var name = query[(lastDash + 1)..];
        return name[..^"Neural".Length];
    }

    public static string Slugify(string value)
    {
        var name = value.Trim();
        if (name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
        {
            name = name["Microsoft ".Length..];
        }

        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "voice" : slug;
    }
}

public static class OpenAiVoiceAliases
{
    private static readonly Dictionary<string, PreferredGender> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["alloy"] = PreferredGender.Any,
            ["fable"] = PreferredGender.Any,
            ["verse"] = PreferredGender.Any,
            ["ballad"] = PreferredGender.Any,
            ["echo"] = PreferredGender.Male,
            ["onyx"] = PreferredGender.Male,
            ["ash"] = PreferredGender.Male,
            ["nova"] = PreferredGender.Female,
            ["shimmer"] = PreferredGender.Female,
            ["coral"] = PreferredGender.Female,
            ["sage"] = PreferredGender.Female
        };

    public static bool TryGet(string name, out PreferredGender gender)
    {
        if (Aliases.TryGetValue(name, out var value))
        {
            gender = value;
            return true;
        }

        gender = PreferredGender.Any;
        return false;
    }
}
