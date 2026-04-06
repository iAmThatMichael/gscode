using System.Text;
using System.Text.RegularExpressions;

namespace GSCode.Data.Helpers;

/// <summary>
/// Formats script doc comments into Markdown for display in hovers and completions.
/// </summary>
public static class ScriptDocCommentFormatter
{
    private static readonly Regex s_kv = new(@"^(?<k>\w+)\s*:\s*(?<v>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Accept <arg> desc, <arg>: desc, [arg] desc, [arg]: desc, or bareword desc
    private static readonly Regex s_argPattern = new(@"^(?<n><[^>]+>|\[[^\]]+\]|[^:\s]+)\s*:?\s*(?<d>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Formats a sanitized doc comment into structured Markdown, including the Name: prototype block.
    /// Use for standalone hover display (e.g. ScrMember, macro definitions).
    /// </summary>
    public static string FormatToMarkdown(string sanitizedDocComment, string? ns)
        => FormatCore(sanitizedDocComment, ns, includePrototype: true);

    /// <summary>
    /// Formats a sanitized doc comment into structured Markdown, omitting the Name: prototype block.
    /// Use when the caller already prepends a live-data prototype (e.g. ScrFunction.Documentation).
    /// </summary>
    public static string FormatBodyOnly(string sanitizedDocComment, string? ns)
        => FormatCore(sanitizedDocComment, ns, includePrototype: false);

    private static string FormatCore(string sanitizedDocComment, string? ns, bool includePrototype)
    {
        if (string.IsNullOrWhiteSpace(sanitizedDocComment))
            return string.Empty;

        string[] lines = sanitizedDocComment.Split('\n');

        // Parse into fields
        string? name = null, summary = null, module = null, callOn = null, spmp = null;
        var mandatory = new List<(string Arg, string Desc)>();
        var optional = new List<(string Arg, string Desc)>();
        var examples = new List<string>();

        foreach (var l in lines)
        {
            var m = s_kv.Match(l);
            if (!m.Success) continue;
            string key = m.Groups["k"].Value.Trim().ToLowerInvariant();
            string val = m.Groups["v"].Value.Trim();

            switch (key)
            {
                case "name":
                    name = val;
                    break;
                case "summary":
                    summary = val;
                    break;
                case "module":
                    module = val;
                    break;
                case "callon":
                    callOn = string.IsNullOrWhiteSpace(val) ? "UNKNOWN" : val;
                    break;
                case "spmp":
                    spmp = val;
                    break;
                case "mandatoryarg":
                    {
                        var am = s_argPattern.Match(val);
                        if (am.Success)
                        {
                            string a = am.Groups["n"].Value.Trim();
                            string d = am.Groups["d"].Value.Trim();

                            a = a.Replace("<", "").Replace(">", "");
                            a = a.Replace("[", "").Replace("]", "");

                            mandatory.Add((a, d));
                        }
                        break;
                    }
                case "optionalarg":
                    {
                        var am = s_argPattern.Match(val);
                        if (am.Success)
                        {
                            string a = am.Groups["n"].Value.Trim();
                            string d = am.Groups["d"].Value.Trim();

                            a = a.Replace("<", "").Replace(">", "");
                            a = a.Replace("[", "").Replace("]", "");

                            optional.Add((a, d));
                        }
                        break;
                    }
                case "example":
                    examples.Add(val);
                    break;
            }
        }

        // If parsing found nothing significant, fall back to cleaned plain text
        if (name is null && summary is null && module is null && callOn is null && spmp is null && mandatory.Count == 0 && optional.Count == 0 && examples.Count == 0)
        {
            return sanitizedDocComment.Trim();
        }

        // Render Markdown
        StringBuilder sb = new();

        if (includePrototype && !string.IsNullOrWhiteSpace(name))
        {
            sb.AppendLine("```gsc");
            // Only add namespace prefix if name doesn't already contain it
            if (!string.IsNullOrWhiteSpace(ns) && !name.StartsWith(ns + "::"))
            {
                sb.AppendLine(ns + "::" + name);
            }
            else
            {
                sb.AppendLine(name);
            }
            sb.AppendLine("```");
            sb.AppendLine("---");
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary);
            sb.AppendLine();
            sb.AppendLine("---");
        }

        if (!string.IsNullOrWhiteSpace(module) || !string.IsNullOrWhiteSpace(callOn) || !string.IsNullOrWhiteSpace(spmp))
        {
            sb.AppendLine("Region:");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(callOn)) sb.AppendLine($"* Called on: `<{callOn}>`");
            if (!string.IsNullOrWhiteSpace(spmp)) sb.AppendLine($"* SPMP: `{spmp}`");
            if (!string.IsNullOrWhiteSpace(module)) sb.AppendLine($"* Module: `{module}`");
            sb.AppendLine();
            sb.AppendLine("---");
        }

        if (mandatory.Count > 0 || optional.Count > 0)
        {
            sb.AppendLine("Parameters:");
            foreach (var (a, d) in mandatory)
            {
                sb.AppendLine($"* `{a}` {d}");
            }
            foreach (var (a, d) in optional)
            {
                sb.AppendLine($"* `{a}` {d}");
            }
            sb.AppendLine();
            sb.AppendLine("---");
        }

        foreach (var ex in examples)
        {
            sb.AppendLine("Example:");
            sb.AppendLine("```gsc");
            sb.AppendLine(ex);
            sb.AppendLine("```");
        }

        return sb.ToString().Trim();
    }
}
