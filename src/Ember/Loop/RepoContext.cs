using System.Diagnostics;

namespace Ember.Loop;

/// <summary>Gathers lightweight context about a target repo for the planner and critic.</summary>
public static class RepoContext
{
    private const int MaxReadmeChars = 2000;
    private const int MaxTrackedFiles = 400;

    /// <summary>
    /// Returns a README excerpt, the top-level listing, and the tracked-file tree.
    /// The tree is ground truth: it stops the planner inventing file paths and lets
    /// the critic verify the ones it is handed.
    /// </summary>
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

        var tracked = GitTrackedFiles(repoPath);
        if (tracked.Count > 0)
        {
            var shown = tracked.Take(MaxTrackedFiles).ToList();
            var listing = string.Join("\n", shown);
            if (tracked.Count > shown.Count)
                listing += $"\n...({tracked.Count - shown.Count} more not shown)";
            parts.Add(
                $"Tracked files ({tracked.Count}) — these are the only paths that exist; "
                + $"a plan must reference paths from this list:\n{listing}");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The repo's tracked files via <c>git ls-files</c>. Returns empty when the path is
    /// not a git checkout or git is unavailable — the caller degrades to README + top level.
    /// </summary>
    private static List<string> GitTrackedFiles(string repoPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "-C", repoPath, "ls-files" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
                return [];

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { /* best effort */ }
                return [];
            }
            if (proc.ExitCode != 0)
                return [];

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
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
