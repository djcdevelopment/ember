namespace Ember.Loop;

/// <summary>Gathers lightweight context about a target repo for the planner.</summary>
public static class RepoContext
{
    private const int MaxReadmeChars = 4000;

    /// <summary>Returns a README excerpt plus the top-level directory listing.</summary>
    public static string Gather(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"(repo path not found on host: {repoPath})";

        var parts = new List<string>();

        var readme = Directory
            .EnumerateFiles(repoPath, "README*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (readme is not null)
        {
            var text = SafeRead(readme);
            if (text.Length > MaxReadmeChars)
                text = text[..MaxReadmeChars] + "\n...(truncated)";
            parts.Add($"{Path.GetFileName(readme)}:\n{text}");
        }

        var entries = Directory
            .EnumerateFileSystemEntries(repoPath, "*", SearchOption.TopDirectoryOnly)
            .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p))
            .Where(name => name is not (".git/" or "bin/" or "obj/"))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        parts.Add($"Top-level entries:\n{string.Join("\n", entries)}");

        return string.Join("\n\n", parts);
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "(unreadable)";
        }
    }
}
