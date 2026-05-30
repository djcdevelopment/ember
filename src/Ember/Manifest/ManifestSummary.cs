using System.Text;
using System.Text.Json;

namespace Ember.Manifest;

/// <summary>
/// Pure parse/format: takes the framework's JSON output and returns the short prose summary
/// folded into the round-1 planner prompt. No subprocesses, no IO — so unit tests can drive
/// it directly off captured JSON fixtures without spawning the framework.
/// </summary>
public static class ManifestSummary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reason a summary could not be produced — surfaced to the caller's logger so an operator
    /// can tell "framework newer than I know how to read" apart from "JSON was malformed."
    /// </summary>
    public enum Failure
    {
        None,
        InvalidJson,
        SchemaVersionTooNew,
        MissingConstellation,
    }

    /// <summary>
    /// Parses <paramref name="json"/> (the framework's stdout) and renders the round-1 summary
    /// from the perspective of <paramref name="repoKey"/>. Returns <c>null</c> with a
    /// <paramref name="failure"/> reason when the document is unusable; the caller decides
    /// whether to log it.
    /// </summary>
    public static string? TryFormat(
        string json, string repoKey, int maxSchemaVersion, out Failure failure)
    {
        failure = Failure.None;
        ManifestDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            failure = Failure.InvalidJson;
            return null;
        }

        if (doc is null || doc.Constellation is null)
        {
            failure = Failure.MissingConstellation;
            return null;
        }
        if (doc.SchemaVersion > maxSchemaVersion)
        {
            failure = Failure.SchemaVersionTooNew;
            return null;
        }

        return Render(doc, repoKey);
    }

    private static string Render(ManifestDocument doc, string repoKey)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Constellation context (from {doc.Constellation.Name})");
        sb.AppendLine();

        var header = $"- Constellation: {doc.Constellation.Name} ({doc.Archetype}, {doc.Topology})";
        if (!string.IsNullOrWhiteSpace(doc.Constellation.Description))
            header += $" — {doc.Constellation.Description}";
        sb.AppendLine(header);

        if (!string.IsNullOrWhiteSpace(doc.Intent))
            sb.AppendLine($"- Intent: {doc.Intent}");

        var matched = FindRepo(doc.Repos, repoKey);
        if (matched is not null)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(matched.ProducerType))
                meta.Add($"producer_type: {matched.ProducerType}");
            if (!string.IsNullOrWhiteSpace(matched.Lifecycle))
                meta.Add($"lifecycle: {matched.Lifecycle}");
            var metaSuffix = meta.Count == 0 ? "" : $" ({string.Join(", ", meta)})";
            var role = string.IsNullOrWhiteSpace(matched.Role) ? "(no role recorded)" : matched.Role;
            sb.AppendLine($"- This repo ({matched.Name}) — {role}{metaSuffix}");

            if (matched.Surfaces is { Count: > 0 } surfaces)
            {
                var rendered = surfaces.Select(s =>
                    string.IsNullOrWhiteSpace(s.Kind) ? s.Name : $"{s.Name} [{s.Kind}]");
                sb.AppendLine($"- Surfaces: {string.Join("; ", rendered)}");
            }
        }

        var neighbors = doc.Repos
            .Where(r => matched is null || !ReferenceEquals(r, matched))
            .ToList();
        if (neighbors.Count > 0)
        {
            sb.AppendLine("- Neighbor repos in this constellation:");
            foreach (var n in neighbors)
            {
                var role = string.IsNullOrWhiteSpace(n.Role) ? "(no role recorded)" : n.Role;
                sb.AppendLine($"  - {n.Name} — {role}");
            }
        }

        if (doc.Saga?.ActiveEpicIds is { Count: > 0 } epics)
            sb.AppendLine($"- Active saga epics: {string.Join(", ", epics)}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Locates the consumed repo's record by <paramref name="repoKey"/> — case-insensitive
    /// match against <c>name</c>, then against any <c>aliases</c>. <c>null</c> when the key
    /// is not present in the manifest (which is fine: the constellation/neighbor lines still
    /// land in the prompt).
    /// </summary>
    private static ManifestRepo? FindRepo(List<ManifestRepo> repos, string repoKey)
    {
        var byName = repos.FirstOrDefault(r =>
            string.Equals(r.Name, repoKey, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        return repos.FirstOrDefault(r =>
            r.Aliases is not null
            && r.Aliases.Any(a => string.Equals(a, repoKey, StringComparison.OrdinalIgnoreCase)));
    }
}
