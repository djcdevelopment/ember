using System.Text;

namespace Ember.Build;

/// <summary>An isolated build worktree: its on-disk path, its branch, and the ref it was cut from.</summary>
public sealed record WorktreeInfo(string Path, string Branch, string BaseRef);

/// <summary>Creates and removes the per-build git worktrees the builder runs in.</summary>
public static class Worktree
{
    /// <summary>
    /// Adds a fresh worktree for <paramref name="repoPath"/> on a new branch
    /// <c>ember/&lt;slug&gt;</c>, cut from the repo's default branch. Any stale worktree or
    /// directory left by a prior crashed run at the same path is cleaned first.
    /// </summary>
    public static async Task<WorktreeInfo> CreateAsync(
        string repoPath, string worktreeRoot, string slug, CancellationToken ct)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"repo path not found on host: {repoPath}");

        var gitMarker = System.IO.Path.Combine(repoPath, ".git");
        if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker))
            throw new InvalidOperationException($"not a git repository: {repoPath}");

        var baseRef = await ResolveBaseRefAsync(repoPath, ct);
        var branch = $"ember/{slug}";
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(worktreeRoot, $"ember-{slug}"));

        Directory.CreateDirectory(worktreeRoot);

        // Drop any registration/directory a previous run left behind so `worktree add` is clean.
        await ProcessRunner.RunAsync("git", new[] { "-C", repoPath, "worktree", "prune" }, null, ct);
        if (Directory.Exists(path))
        {
            await ProcessRunner.RunAsync(
                "git", new[] { "-C", repoPath, "worktree", "remove", "--force", path }, null, ct);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        // -B creates or resets the branch, so a re-run of the same session does not collide.
        var add = await ProcessRunner.RunAsync(
            "git", new[] { "-C", repoPath, "worktree", "add", "-B", branch, path, baseRef }, null, ct);
        if (!add.Ok)
            throw new InvalidOperationException(
                $"`git worktree add` failed: {Trim(add.StdErr, add.StdOut)}");

        return new WorktreeInfo(path, branch, baseRef);
    }

    /// <summary>Removes a worktree and prunes its registration. Best-effort — used by Phase 3 cleanup.</summary>
    public static async Task RemoveAsync(string repoPath, string worktreePath, CancellationToken ct)
    {
        await ProcessRunner.RunAsync(
            "git", new[] { "-C", repoPath, "worktree", "remove", "--force", worktreePath }, null, ct);
        await ProcessRunner.RunAsync("git", new[] { "-C", repoPath, "worktree", "prune" }, null, ct);
    }

    /// <summary>A one-line summary of the worktree's changes against the ref it was cut from.</summary>
    public static async Task<string> DiffStatAsync(string worktreePath, string baseRef, CancellationToken ct)
    {
        var diff = await ProcessRunner.RunAsync(
            "git", new[] { "-C", worktreePath, "diff", "--shortstat", baseRef }, null, ct);
        var summary = diff.StdOut.Trim();
        if (summary.Length == 0)
            summary = "no tracked changes vs base";

        var status = await ProcessRunner.RunAsync(
            "git", new[] { "-C", worktreePath, "status", "--porcelain" }, null, ct);
        var untracked = status.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => l.StartsWith("??", StringComparison.Ordinal));
        if (untracked > 0)
            summary += $" (+{untracked} untracked)";

        return summary;
    }

    /// <summary>Derives a filesystem- and branch-safe slug from a brief, uniquified by thread id.</summary>
    public static string MakeSlug(string brief, string threadId)
    {
        var sb = new StringBuilder();
        var lastDash = true; // suppress a leading dash
        foreach (var c in brief.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= 32)
                break;
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
            slug = "session";

        // A short thread-id tail keeps concurrent / repeated briefs from colliding.
        var tail = threadId.Length >= 6 ? threadId[^6..] : threadId;
        return $"{slug}-{tail}";
    }

    /// <summary>
    /// Resolves the ref a build branch should be cut from: the remote's default branch if
    /// known, then a local <c>main</c> / <c>master</c>, then <c>HEAD</c>.
    /// </summary>
    private static async Task<string> ResolveBaseRefAsync(string repoPath, CancellationToken ct)
    {
        var originHead = await ProcessRunner.RunAsync(
            "git", new[] { "-C", repoPath, "symbolic-ref", "--short", "refs/remotes/origin/HEAD" }, null, ct);
        if (originHead.Ok && originHead.StdOut.Trim() is { Length: > 0 } remote)
            return remote;

        foreach (var name in new[] { "main", "master" })
        {
            var verify = await ProcessRunner.RunAsync(
                "git", new[] { "-C", repoPath, "rev-parse", "--verify", "--quiet", $"refs/heads/{name}" }, null, ct);
            if (verify.Ok)
                return name;
        }

        return "HEAD";
    }

    private static string Trim(string primary, string fallback)
    {
        var text = primary.Trim();
        if (text.Length == 0)
            text = fallback.Trim();
        return text.Length == 0 ? "(no output)" : text;
    }
}
