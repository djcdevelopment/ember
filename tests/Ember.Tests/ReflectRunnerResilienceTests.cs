using System.Diagnostics;
using Ember.Config;
using Ember.Loop;
using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Exercises judge resilience (RF2 / ADR 18): transient-error retry, cross-endpoint failover,
/// and the loud degrade banner — never a silent single-bullet recap. Drives the runner against
/// a real throwaway git repo (so evidence is non-empty) with scripted chat clients.
/// </summary>
public sealed class ReflectRunnerResilienceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"ember-resilience-tests-{Guid.NewGuid():N}");

    private const string ValidRecap =
        "<recap><repo name=\"alpha\"><claim><statement>work landed</statement>"
        + "<from>feature.cs</from></claim></repo></recap>";

    [SkippableFact]
    public async Task Transient_failure_is_retried_then_succeeds_on_the_same_endpoint()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var (runner, a, _) = BuildRunner(
            failover: true, maxAttempts: 2,
            chatA: ScriptedChat.Of(Transient, () => ValidRecap),
            chatB: ScriptedChat.Of(() => ValidRecap));

        var outcome = await runner.PrepareAsync(Baseline(), runJudges: true, CancellationToken.None);

        Assert.Equal(RecapStatus.Ran, outcome.Status);
        Assert.NotNull(outcome.RecapA);
        Assert.Equal("model-A", outcome.JudgeAModelUsed); // succeeded on its own endpoint
        Assert.Null(outcome.Degrade);
        Assert.Equal(2, a.Calls); // one failure + one success
    }

    [SkippableFact]
    public async Task Down_endpoint_fails_over_to_the_sibling_and_says_so_loudly()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var (runner, _, _) = BuildRunner(
            failover: true, maxAttempts: 2,
            chatA: ScriptedChat.Of(Transient),       // A is hard-down
            chatB: ScriptedChat.Of(() => ValidRecap)); // B answers, incl. A's failover

        var outcome = await runner.PrepareAsync(Baseline(), runJudges: true, CancellationToken.None);

        Assert.Equal(RecapStatus.Ran, outcome.Status);
        Assert.NotNull(outcome.RecapA);
        Assert.Equal("model-B", outcome.JudgeAModelUsed); // produced by the sibling endpoint
        Assert.NotNull(outcome.Degrade);
        Assert.Contains("failover", outcome.Degrade!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task With_failover_off_a_down_judge_degrades_loudly_to_single_perspective()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var (runner, _, _) = BuildRunner(
            failover: false, maxAttempts: 2,
            chatA: ScriptedChat.Of(Transient),
            chatB: ScriptedChat.Of(() => ValidRecap));

        var outcome = await runner.PrepareAsync(Baseline(), runJudges: true, CancellationToken.None);

        Assert.Equal(RecapStatus.Ran, outcome.Status);
        Assert.Null(outcome.RecapA);
        Assert.NotNull(outcome.RecapB);
        Assert.NotNull(outcome.Degrade);
        Assert.Contains("single judge", outcome.Degrade!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Both_judges_down_fails_the_run_rather_than_inventing_a_recap()
    {
        Skip.IfNot(GitWorks(), "git is not available on this host.");
        var (runner, _, _) = BuildRunner(
            failover: false, maxAttempts: 2,
            chatA: ScriptedChat.Of(Transient),
            chatB: ScriptedChat.Of(Transient));

        var outcome = await runner.PrepareAsync(Baseline(), runJudges: true, CancellationToken.None);

        Assert.Equal(RecapStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Degrade); // both-down is a failure, not a partial recap
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private static string Transient() => throw new TimeoutException("simulated 503 — slot loading");

    private BaselineMode Baseline() => new BaselineMode.LastRecorded(_firstShas);

    private Dictionary<string, string> _firstShas = new();

    private (ReflectRunner Runner, ScriptedChat A, ScriptedChat B) BuildRunner(
        bool failover, int maxAttempts, ScriptedChat chatA, ScriptedChat chatB)
    {
        var repo = CreateRepo("alpha", out var first, out _);
        _firstShas = new Dictionary<string, string> { ["alpha"] = first };

        var ember = new EmberOptions { Graph = new GraphOptions { Enabled = false } };
        ember.Repos["alpha"] = new RepoEntry { Path = repo };
        ember.Reflect.JudgeMaxAttempts = maxAttempts;
        ember.Reflect.JudgeRetryBaseSeconds = 0; // no real backoff delay in tests
        ember.Reflect.JudgeFailover = failover;
        var emberOpt = Options.Create(ember);

        var models = Options.Create(new ModelsOptions
        {
            ReflectA = new ModelOptions { Model = "model-A" },
            ReflectB = new ModelOptions { Model = "model-B" },
        });

        var assembler = new EvidenceAssembler(
            new GraphContext(emberOpt, NullLogger<GraphContext>.Instance),
            new GlanceReader(emberOpt, NullLogger<GlanceReader>.Instance),
            emberOpt, NullLogger<EvidenceAssembler>.Instance);

        // The comparer is incidental here; give it a chat that never parses → Comparison stays null.
        var comparer = new DivergenceComparer(ScriptedChat.Of(() => ""), NullLogger<DivergenceComparer>.Instance);

        var runner = new ReflectRunner(
            assembler, chatA, chatB, comparer, models, emberOpt, NullLogger<ReflectRunner>.Instance);
        return (runner, chatA, chatB);
    }

    /// <summary>An IChatClient that plays a script of responses; each step returns text or throws.</summary>
    private sealed class ScriptedChat : IChatClient
    {
        private readonly Func<string>[] _steps;
        public int Calls { get; private set; }

        private ScriptedChat(Func<string>[] steps) => _steps = steps;

        public static ScriptedChat Of(params Func<string>[] steps) => new(steps);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            // After the script is exhausted, repeat the final step (steady state).
            var step = _steps[Math.Min(Calls, _steps.Length - 1)];
            Calls++;
            var text = step(); // may throw to simulate an endpoint failure
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
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
