namespace Ember.Build;

/// <summary>
/// Materialises the frozen plan snapshot into the build worktree as <c>.ember/PLAN.md</c> —
/// the single source of truth the builder consumes (it never reads Discord history). The
/// <c>.ember/</c> directory is kept out of the target repo via <c>.git/info/exclude</c>, so
/// no tracked <c>.gitignore</c> churn lands in the operator's repo.
/// </summary>
public static class PlanArtifact
{
    private const string ExcludeEntry = ".ember/";

    /// <summary>Writes the plan to <c>.ember/PLAN.md</c> in the worktree and excludes <c>.ember/</c>.</summary>
    public static async Task WriteAsync(
        string repoPath, string worktreePath, string planSnapshot, CancellationToken ct)
    {
        var emberDir = Path.Combine(worktreePath, ".ember");
        Directory.CreateDirectory(emberDir);
        await File.WriteAllTextAsync(Path.Combine(emberDir, "PLAN.md"), planSnapshot, ct);

        await EnsureExcludedAsync(repoPath, ct);
    }

    /// <summary>
    /// Appends <c>.ember/</c> to the repo's shared <c>info/exclude</c> (idempotent). Worktrees
    /// share the main repo's common git dir, so a single entry covers every build worktree.
    /// </summary>
    private static async Task EnsureExcludedAsync(string repoPath, CancellationToken ct)
    {
        var commonDir = await ProcessRunner.RunAsync(
            "git", new[] { "-C", repoPath, "rev-parse", "--git-common-dir" }, null, ct);
        if (!commonDir.Ok)
            return; // Not fatal — the worktree still builds; .ember/ just is not auto-excluded.

        var dir = commonDir.StdOut.Trim();
        if (!Path.IsPathRooted(dir))
            dir = Path.GetFullPath(Path.Combine(repoPath, dir));

        var infoDir = Path.Combine(dir, "info");
        Directory.CreateDirectory(infoDir);
        var excludePath = Path.Combine(infoDir, "exclude");

        if (File.Exists(excludePath))
        {
            var lines = await File.ReadAllLinesAsync(excludePath, ct);
            if (lines.Any(l => l.Trim() == ExcludeEntry))
                return;
        }

        var existing = File.Exists(excludePath) ? await File.ReadAllTextAsync(excludePath, ct) : "";
        if (existing.Length > 0 && !existing.EndsWith('\n'))
            existing += "\n";
        await File.WriteAllTextAsync(excludePath, existing + ExcludeEntry + "\n", ct);
    }
}
