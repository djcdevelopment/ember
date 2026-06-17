using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ember.Reflect;

/// <summary>
/// Renders a recap author's <c>&lt;recap&gt;</c> XML (recap-prompt/v2-xml-cite) into readable
/// markdown for Discord, and scores grounding by checking each <c>&lt;from&gt;</c> citation
/// against the evidence. Both operations soft-fall to safe defaults: malformed XML is posted
/// raw rather than lost, and an uncitable recap scores 0 rather than throwing.
/// </summary>
internal static class RecapXml
{
    /// <summary>Renders the recap XML to markdown; returns the trimmed raw text if it is not parseable XML.</summary>
    public static string Render(string raw)
    {
        var start = raw.IndexOf("<recap", StringComparison.Ordinal);
        var closeAt = raw.IndexOf("</recap>", StringComparison.Ordinal);
        if (start < 0 || closeAt < 0)
            return raw.Trim();

        try
        {
            var xml = SanitizeForParse(raw.Substring(start, closeAt - start + "</recap>".Length));
            var root = XElement.Parse(xml);
            var sb = new StringBuilder();

            foreach (var repo in root.Elements("repo"))
            {
                var name = (string?)repo.Attribute("name") ?? "(repo)";
                sb.AppendLine($"**{name}**");
                foreach (var claim in repo.Elements("claim"))
                {
                    var statement = ((string?)claim.Element("statement") ?? "").Trim();
                    var from = ((string?)claim.Element("from") ?? "").Trim();
                    if (statement.Length == 0)
                        continue;
                    sb.AppendLine(from.Length > 0 ? $"- {statement} (`{from}`)" : $"- {statement}");
                }
                sb.AppendLine();
            }

            AppendList(sb, "Threads & risks", root.Element("threads")?.Elements("thread"));
            AppendList(sb, "Open questions", root.Element("open-questions")?.Elements("question"));

            var md = sb.ToString().Trim();
            return md.Length > 0 ? md : raw.Trim();
        }
        catch
        {
            // Malformed XML from a local model: post the raw recap rather than drop it.
            return raw.Trim();
        }
    }

    /// <summary>
    /// Escapes bare ampersands so a local model's not-quite-well-formed XML still parses. Local
    /// models reliably emit tags but not entity-escaping, and the domain text contains "&amp;"
    /// (e.g. "Threads &amp; risks") which otherwise breaks the strict XElement parser. Naked
    /// angle brackets in content are rarer and left to the caller's soft-fall.
    /// </summary>
    internal static string SanitizeForParse(string xml) =>
        Regex.Replace(xml, "&(?!(?:amp|lt|gt|quot|apos|#[0-9]+|#x[0-9A-Fa-f]+);)", "&amp;");

    /// <summary>(valid, total) — how many <c>&lt;from&gt;</c> citations appear verbatim in the evidence.</summary>
    public static (int Valid, int Total) CountCitations(string raw, string evidence)
    {
        var matches = Regex.Matches(raw, "<from>(.*?)</from>", RegexOptions.Singleline);
        if (matches.Count == 0)
            return (0, 0);

        var valid = 0;
        foreach (Match m in matches)
        {
            var token = m.Groups[1].Value.Trim();
            var head = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
            if (head.Length > 0 && evidence.Contains(head, StringComparison.Ordinal))
                valid++;
        }
        return (valid, matches.Count);
    }

    private static void AppendList(StringBuilder sb, string heading, IEnumerable<XElement>? items)
    {
        var values = items?.Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList();
        if (values is null || values.Count == 0)
            return;
        sb.AppendLine($"**{heading}**");
        foreach (var v in values)
            sb.AppendLine($"- {v}");
        sb.AppendLine();
    }
}
