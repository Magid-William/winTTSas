using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WinTtsAs.Core;

public static class TextSanitizer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    private static readonly Regex MarkdownSignal = new(
        @"(?m)^\s{0,3}(?:#{1,6}\s|>\s|```|~~~|\|.*\||[-*+]\s+|\d+\.\s+)" +
        @"|\[[^\]]+\]\([^)]+\)|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|`[^`\r\n]+`" +
        @"|</?[a-zA-Z][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareUrl = new(
        @"\b(?:https?://|www\.)[^\s<>()\[\]{}""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> DefaultReplacements =
        new(StringComparer.Ordinal)
        {
            ["\u2014"] = ", ",   // em dash
            ["\u2013"] = ", ",   // en dash
            ["\u2012"] = ", ",   // figure dash
            ["\u2015"] = ", ",   // horizontal bar
            ["\u2192"] = " right arrow ",
            ["\u2190"] = " left arrow ",
            ["\u2191"] = " up arrow ",
            ["\u2193"] = " down arrow ",
            ["\u2194"] = " left right arrow ",
            ["\u21d2"] = " implies ",
            ["\u21d0"] = " implied by ",
            ["\u2022"] = ", ",   // bullet
            ["\u00b7"] = ", ",   // middle dot
            ["\u2026"] = ", ",   // ellipsis
            ["\u00d7"] = " times ",
            ["\u00f7"] = " divided by ",
            ["\u00b1"] = " plus or minus ",
            ["\u2264"] = " less than or equal to ",
            ["\u2265"] = " greater than or equal to ",
            ["\u2260"] = " not equal to ",
            ["\u2248"] = " approximately ",
            ["\u00a0"] = " ",
            ["\u200b"] = "",
            ["\u200c"] = "",
            ["\u200d"] = "",
            ["\ufeff"] = "",
            ["\u201c"] = "\"",
            ["\u201d"] = "\"",
            ["\u2018"] = "'",
            ["\u2019"] = "'"
        };

    public static string Apply(string text, AppConfig config, bool? sanitize = null)
    {
        if (!(sanitize ?? config.Sanitize) || string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result;
        if (ShouldParseMarkdown(text, config.Markdown))
        {
            try
            {
                result = RenderMarkdown(text, config);
            }
            catch
            {
                // Malformed input should never fail synthesis; fall back to
                // plain-text URL and symbol handling.
                result = text;
            }
        }
        else
        {
            result = text;
        }

        result = ReplaceBareUrls(result, config.UrlMode);
        result = ReplaceSymbols(result, config.TextReplacements);
        result = StripHtmlTags(WebUtility.HtmlDecode(result));
        return Cleanup(result);
    }

    private static bool ShouldParseMarkdown(string text, string mode) => mode switch
    {
        "on" => true,
        "off" => false,
        _ => MarkdownSignal.IsMatch(text)
    };

    private static string RenderMarkdown(string text, AppConfig config)
    {
        var document = Markdown.Parse(text, MarkdownPipeline);
        var builder = new StringBuilder();
        RenderBlocks(document, builder, config);
        return builder.ToString();
    }

    private static void RenderBlocks(ContainerBlock container, StringBuilder builder, AppConfig config)
    {
        foreach (var block in container)
        {
            RenderBlock(block, builder, config);
        }
    }

    private static void RenderBlock(Block block, StringBuilder builder, AppConfig config)
    {
        switch (block)
        {
            case HeadingBlock heading:
                AppendSentence(builder, RenderInlines(heading.Inline, config));
                break;

            case ParagraphBlock paragraph:
                AppendSentence(builder, RenderInlines(paragraph.Inline, config));
                break;

            case ListBlock list:
                foreach (var item in list)
                {
                    if (item is ListItemBlock listItem)
                    {
                        RenderBlocks(listItem, builder, config);
                    }
                }
                break;

            case QuoteBlock quote:
                RenderBlocks(quote, builder, config);
                break;

            case FencedCodeBlock fenced:
                RenderCodeBlock(fenced.Lines.ToString(), builder, config);
                break;

            case CodeBlock code:
                RenderCodeBlock(code.Lines.ToString(), builder, config);
                break;

            case ThematicBreakBlock:
                builder.Append('\n');
                break;

            case HtmlBlock html:
                AppendSentence(builder, StripHtml(html.Lines.ToString()));
                break;

            case Table table:
                RenderTable(table, builder, config);
                break;

            case ContainerBlock container:
                RenderBlocks(container, builder, config);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
                AppendSentence(builder, RenderInlines(leaf.Inline, config));
                break;
        }
    }

    private static void RenderTable(Table table, StringBuilder builder, AppConfig config)
    {
        if (config.TableMode == "skip")
        {
            return;
        }

        foreach (var row in table)
        {
            if (row is not TableRow tableRow)
            {
                continue;
            }

            var cells = tableRow
                .OfType<TableCell>()
                .Select(cell => RenderCell(cell, config))
                .Where(cell => cell.Length > 0)
                .ToArray();

            if (cells.Length > 0)
            {
                AppendSentence(builder, string.Join(", ", cells));
            }
        }
    }

    private static string RenderCell(TableCell cell, AppConfig config)
    {
        var builder = new StringBuilder();
        foreach (var block in cell)
        {
            if (block is ParagraphBlock paragraph)
            {
                builder.Append(RenderInlines(paragraph.Inline, config));
            }
            else
            {
                RenderBlock(block, builder, config);
            }
        }

        return CleanupInline(builder.ToString());
    }

    private static void RenderCodeBlock(string? code, StringBuilder builder, AppConfig config)
    {
        switch (config.CodeMode)
        {
            case "skip":
                return;
            case "speak":
                AppendSentence(builder, code ?? "");
                break;
            default:
                AppendSentence(builder, "code block");
                break;
        }
    }

    private static string RenderInlines(ContainerInline? container, AppConfig config)
    {
        if (container is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        RenderInlines(container, builder, config);
        return builder.ToString();
    }

    private static void RenderInlines(
        ContainerInline container,
        StringBuilder builder,
        AppConfig config)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    RenderInlines(emphasis, builder, config);
                    break;

                case CodeInline code:
                    switch (config.InlineCodeMode)
                    {
                        case "skip":
                            break;
                        case "label":
                            builder.Append(" code ");
                            break;
                        default:
                            builder.Append(' ').Append(code.Content).Append(' ');
                            break;
                    }
                    break;

                case LinkInline link:
                    RenderLink(link, builder, config);
                    break;

                case AutolinkInline autoLink:
                    builder.Append(' ').Append(ApplyUrl(autoLink.Url, config.UrlMode)).Append(' ');
                    break;

                case LineBreakInline:
                    builder.Append(' ');
                    break;

                case HtmlInline html:
                    builder.Append(' ').Append(StripHtml(html.Tag)).Append(' ');
                    break;

                case HtmlEntityInline entity:
                    builder.Append(WebUtility.HtmlDecode(entity.Transcoded.ToString()));
                    break;

                case TaskList taskList:
                    builder.Append(taskList.Checked ? " checked " : " unchecked ");
                    break;

                case ContainerInline nested:
                    RenderInlines(nested, builder, config);
                    break;
            }
        }
    }

    private static void RenderLink(LinkInline link, StringBuilder builder, AppConfig config)
    {
        var text = RenderInlines(link, config).Trim();

        if (link.IsImage)
        {
            builder.Append(' ').Append(text.Length > 0 ? text : "image").Append(' ');
            return;
        }

        if (text.Length == 0 ||
            string.Equals(text, link.Url, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(' ').Append(ApplyUrl(link.Url, config.UrlMode)).Append(' ');
            return;
        }

        builder.Append(' ').Append(text).Append(' ');
    }

    private static string ApplyUrl(string? url, string mode)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        return mode switch
        {
            "skip" => "",
            "link" => "link",
            "domain" => SpeakDomain(url),
            "speak" => SpeakUrl(url),
            _ => "link"
        };
    }

    private static string SpeakDomain(string url)
    {
        var candidate = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url
            : url;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                host = host[4..];
            }

            return host.Replace(".", " dot ", StringComparison.Ordinal);
        }

        return "link";
    }

    private static string SpeakUrl(string url)
    {
        var text = Regex.Replace(url, @"^[a-z]+://", "", RegexOptions.IgnoreCase);
        return text
            .Replace("/", " slash ", StringComparison.Ordinal)
            .Replace(".", " dot ", StringComparison.Ordinal)
            .Replace("?", " question ", StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("=", " equals ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal);
    }

    private static string ReplaceBareUrls(string text, string mode)
    {
        if (mode == "speak")
        {
            return text;
        }

        return BareUrl.Replace(text, match =>
        {
            var value = match.Value;
            var trimmed = value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            var trailing = value[trimmed.Length..];
            return ApplyUrl(trimmed, mode) + trailing;
        });
    }

    private static string ReplaceSymbols(
        string text,
        IReadOnlyDictionary<string, string> userReplacements)
    {
        var replacements = new Dictionary<string, string>(
            DefaultReplacements,
            StringComparer.Ordinal);

        foreach (var pair in userReplacements)
        {
            replacements[pair.Key] = pair.Value;
        }

        if (replacements.Count == 0)
        {
            return text;
        }

        var keys = replacements.Keys
            .OrderByDescending(key => key.Length)
            .ToArray();
        var pattern = string.Join("|", keys.Select(Regex.Escape));

        return Regex.Replace(
            text,
            pattern,
            match => replacements[match.Value],
            RegexOptions.CultureInvariant);
    }

    private static void AppendSentence(StringBuilder builder, string text)
    {
        text = CleanupInline(text);
        if (text.Length == 0)
        {
            return;
        }

        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }

        builder.Append(text);

        if (!EndsWithSentencePunctuation(text))
        {
            builder.Append('.');
        }

        builder.Append(' ');
    }

    private static bool EndsWithSentencePunctuation(string text) =>
        text.Length > 0 && text[^1] is '.' or '!' or '?' or ':' or ';';

    private static string CleanupInline(string text) =>
        Regex.Replace(
                text.Replace('\r', ' ').Replace('\n', ' '),
                @"[ \t]{2,}",
                " ")
            .Trim();

    private static string Cleanup(string text)
    {
        var result = text.Replace("\r\n", "\n").Replace('\r', '\n');
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = Regex.Replace(result, @"[ \t]+([,.;:!?])", "$1");
        result = Regex.Replace(result, @"[ \t]*\n[ \t]*", "\n");
        result = Regex.Replace(result, @"\n{2,}", "\n");
        return result.Trim();
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var withoutTags = StripHtmlTags(html);
        return CleanupInline(WebUtility.HtmlDecode(withoutTags));
    }

    private static string StripHtmlTags(string text)
    {
        var withoutBlocks = Regex.Replace(
            text,
            @"<(script|style)[^>]*>.*?</\1>",
            " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return Regex.Replace(
            withoutBlocks,
            "</?[a-zA-Z][^>]*>",
            " ",
            RegexOptions.Singleline);
    }
}
