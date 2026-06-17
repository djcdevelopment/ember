using System.Diagnostics;
using Ember.Config;
using Ember.Loop;
using Ember.Reflect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Exercises evidence assembly against real throwaway git repos (skips when git is not on
/// the host). The graph seam stays disabled so these tests pin only the git side.
/// </summary>
public sealed class EvidenceAssemblerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"ember-evidence-tests-{Guid.NewGuid():N}");

    [SkippableFact]
    public async Task First_run_records_baseline_without_changes()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var repo = CreateRepo("alpha", out _, out var head);

        var bundle = await Assemble(repo, new BaselineMode.LastRecorded(new Dictionary<string, string>()));

        var evidence = Assert.Single(bundle.Repos);
        Assert.False(evidence.HasChanges);
        Assert.Null(evidence.FromSha);
        Assert.Equal(head, evidence.ToSha);
        Assert.Contains("baseline recorded", bundle.Text);
    }

    [SkippableFact]
    public async Task Recorded_baseline_yields_commit_and_file_delta()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var repo = CreateRepo("alpha", out var first, out var head);

        var bundle = await Assemble(repo, new BaselineMode.LastRecorded(
            new Dictionary<string, string> { ["alpha"] = first }));

        var evidence = Assert.Single(bundle.Repos);
        Assert.True(evidence.HasChanges);
        Assert.Equal(first, evidence.FromSha);
        Assert.Equal(head, evidence.ToSha);
        Assert.Contains("add feature file", evidence.Section);
        Assert.Contains("feature.cs", evidence.Section);
        Assert.Contains("1 repo(s) with changes", bundle.Text);
    }

    [SkippableFact]
    public async Task Current_head_as_baseline_means_no_changes()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var repo = CreateRepo("alpha", out _, out var head);

        var bundle = await Assemble(repo, new BaselineMode.LastRecorded(
            new Dictionary<string, string> { ["alpha"] = head }));

        var evidence = Assert.Single(bundle.Repos);
        Assert.False(evidence.HasChanges);
        Assert.Contains("no changes", bundle.Text);
    }

    [SkippableFact]
    public async Task Unusable_baseline_rebaselines_instead_of_failing()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var repo = CreateRepo("alpha", out _, out var head);

        var bundle = await Assemble(repo, new BaselineMode.LastRecorded(
            new Dictionary<string, string> { ["alpha"] = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef" }));

        var evidence = Assert.Single(bundle.Repos);
        Assert.False(evidence.HasChanges);
        Assert.Null(evidence.FromSha);
        Assert.Equal(head, evidence.ToSha);
    }

    [SkippableFact]
    public async Task Missing_repo_path_is_skipped_not_fatal()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");

        var options = OptionsFor(("ghost", Path.Combine(_root, "nope")));
        var assembler = NewAssembler(options);

        var bundle = await assembler.AssembleAsync(
            new BaselineMode.LastRecorded(new Dictionary<string, string>()), CancellationToken.None);

        var evidence = Assert.Single(bundle.Repos);
        Assert.False(evidence.HasChanges);
        Assert.Null(evidence.ToSha);
        Assert.Contains("unreadable", bundle.Text);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private async Task<EvidenceBundle> Assemble(string repoPath, BaselineMode mode)
    {
        var assembler = NewAssembler(OptionsFor(("alpha", repoPath)));
        return await assembler.AssembleAsync(mode, CancellationToken.None);
    }

    [SkippableFact]
    public async Task Changed_repo_is_reindexed_before_its_symbols_are_read()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var repo = CreateRepo("alpha", out var first, out _);

        var options = new EmberOptions
        {
            Graph = new GraphOptions { Enabled = true, ReindexBeforeRead = true },
        };
        options.Repos["alpha"] = new RepoEntry { Path = repo };
        var opt = Options.Create(options);
        var graph = new RecordingGraph(opt);
        var assembler = new EvidenceAssembler(graph, opt, NullLogger<EvidenceAssembler>.Instance);

        await assembler.AssembleAsync(
            new BaselineMode.LastRecorded(new Dictionary<string, string> { ["alpha"] = first }),
            CancellationToken.None);

        // The freshness fix: reindex must happen, and before the symbol read.
        var reindexAt = graph.Tools.IndexOf("index_repository");
        var searchAt = graph.Tools.IndexOf("search_graph");
        Assert.True(reindexAt >= 0, "changed repo should be re-indexed");
        Assert.True(searchAt < 0 || reindexAt < searchAt, "reindex should precede the symbol search");
    }

    private static EvidenceAssembler NewAssembler(IOptions<EmberOptions> options) =>
        new(new GraphContext(options, NullLogger<GraphContext>.Instance),
            options, NullLogger<EvidenceAssembler>.Instance);

    /// <summary>A GraphContext whose CLI seam records the tools called, in order, and returns
    /// canned JSON — so we can assert the reindex-before-read ordering without a real index.</summary>
    private sealed class RecordingGraph : GraphContext
    {
        public List<string> Tools { get; } = new();

        public RecordingGraph(IOptions<EmberOptions> options)
            : base(options, NullLogger<GraphContext>.Instance) { }

        protected override Task<string?> RunToolAsync(string tool, string argsJson, CancellationToken ct)
        {
            Tools.Add(tool);
            return Task.FromResult<string?>(tool == "index_repository"
                ? """{"status":"indexed"}"""
                : """{"total":0,"results":[]}""");
        }
    }

    private static IOptions<EmberOptions> OptionsFor(params (string Key, string Path)[] repos)
    {
        var options = new EmberOptions { Graph = new GraphOptions { Enabled = false } };
        foreach (var (key, path) in repos)
            options.Repos[key] = new RepoEntry { Path = path };
        return Options.Create(options);
    }

    /// <summary>A repo with two commits; returns the first sha and HEAD.</summary>
    private string CreateRepo(string name, out string firstSha, out string headSha)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);

        Git(path, "init");
        File.WriteAllText(Path.Combine(path, "readme.md"), "hello");
        Git(path, "add .");
        Git(path, "commit -m \"initial\"");
        firstSha = Git(path, "rev-parse HEAD").Trim();

        File.WriteAllText(Path.Combine(path, "feature.cs"), "// feature");
        Git(path, "add .");
        Git(path, "commit -m \"add feature file\"");
        headSha = Git(path, "rev-parse HEAD").Trim();

        return path;
    }

    private static string Git(string repoPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{repoPath}\" -c user.name=test -c user.email=test@test {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return stdout;
    }

    private static bool GitWorks()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Clear read-only attributes git sets on object files so the delete succeeds.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best effort — temp dir cleanup
        }
    }
}
