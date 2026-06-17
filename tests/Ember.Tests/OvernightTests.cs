using Ember.Config;
using Ember.Loop;
using Ember.Overnight;
using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// The overnight backlog planner (ADR 19): objective-state assembly (E1), board-sync tiering (E2),
/// the gated auto-safe apply (E3), and the runner's loud degrade. Pure over the glance / board
/// readers — no git or models needed (scripted fakes), so these run on any host.
/// </summary>
public sealed class OvernightTests
{
    private const string GlanceJson = """
        { "repos": [
          { "name": "ember", "kind": "git", "lifecycle": "active", "wip": 10, "recent": ["a x","b y"],
            "branch": "master...origin/master", "ahead": false, "behind": false, "days_since_commit": 0, "hot": true, "drift_flag": false },
          { "name": "raidui", "kind": "git", "lifecycle": "deprecating", "wip": 27, "recent": [],
            "branch": "master", "ahead": false, "behind": false, "days_since_commit": 31, "hot": true, "drift_flag": false },
          { "name": "lantern", "kind": "git", "lifecycle": "active", "wip": 0, "recent": [],
            "branch": "main", "ahead": true, "behind": false, "days_since_commit": 26, "hot": false, "drift_flag": true }
        ] }
        """;

    [Fact]
    public async Task Objective_state_categorizes_changed_drifting_needscall_and_nextslice()
    {
        var bundle = await Assemble(GlanceJson, board: null);
        var text = bundle.Text;

        // Changed: in-flight + committed both surface.
        Assert.Contains("ember — active, 2 recent commit(s), 10 uncommitted", text);
        // Drifting: a deprecating repo with churn, and a drift-flagged repo.
        Assert.Contains("raidui — 27 uncommitted in a DEPRECATING repo", text);
        Assert.Contains("lantern — declared active, quiet 26d ago, no WIP (drift flag)", text);
        // Needs-a-call: the lifecycle tensions.
        Assert.Contains("raidui: 27 uncommitted but lifecycle is `deprecating`", text);
        Assert.Contains("lantern: declared active but quiet", text);
        // Next-slice candidates: active + in-flight only — never a deprecating repo.
        Assert.Contains("## Next-slice candidates", text);
        var nextSlice = text[text.IndexOf("## Next-slice", StringComparison.Ordinal)..];
        Assert.Contains("ember", nextSlice);
        Assert.DoesNotContain("raidui", nextSlice); // deprecating must not be recommended
    }

    [Fact]
    public async Task Board_delta_folds_into_needs_a_call_and_the_board_section()
    {
        var board = """
            { "in_sync": false, "board_available": true, "manifest_repos": 3,
              "auto_safe": ["foo: missing pm/repos/foo.md  ->  draft from manifest entry"],
              "decisions": ["bar: no epic in GAD\\bar  ->  own epic"],
              "live_truth": ["baz: unresolved CHECK marker(s): github  ->  verify live"] }
            """;
        var bundle = await Assemble(GlanceJson, board);

        Assert.NotNull(bundle.Board);
        Assert.Contains("board: bar: no epic", bundle.Text);          // decision → needs-a-call
        Assert.Contains("[auto-safe] foo: missing pm/repos/foo.md", bundle.Text);
        Assert.Contains("[decision] bar: no epic", bundle.Text);
        Assert.Contains("[live-truth] baz: unresolved CHECK", bundle.Text);
    }

    [Fact]
    public async Task Glance_unavailable_marks_the_brief_provisional()
    {
        var bundle = await Assemble(glanceJson: null, board: null);
        Assert.False(bundle.GlanceAvailable);
        Assert.Contains("glance unavailable", bundle.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoardSyncDelta_isolates_the_in_repo_auto_safe_subset()
    {
        var delta = new BoardSyncDelta(
            InSync: false, BoardAvailable: true, ManifestRepos: 2,
            AutoSafe: new[]
            {
                "foo: missing pm/repos/foo.md  ->  draft from manifest entry",
                "bar: no area path  ->  az boards area project create --name bar",
            },
            Decisions: Array.Empty<string>(), LiveTruth: Array.Empty<string>());

        var inRepo = delta.InRepoAutoSafe;
        Assert.Single(inRepo);
        Assert.Contains("missing pm/repos/foo.md", inRepo[0]); // area-path (needs az) is excluded
    }

    [Fact]
    public static void Critic_issue_parsing_is_tolerant_of_prose_around_the_json()
    {
        Assert.Empty(BriefCritic.ParseIssues("[]"));
        Assert.Empty(BriefCritic.ParseIssues("looks good"));
        var issues = BriefCritic.ParseIssues("Here you go: [\"raidui WIP missing\", \"mis-tiered\"] done");
        Assert.Equal(2, issues.Count);
        Assert.Contains("raidui WIP missing", issues);
    }

    [Fact]
    public async Task AutoSafe_apply_is_gated_off_by_default_and_surfaces_everything()
    {
        using var tmp = new TempGad();
        var writer = new SummaryDocWriter(tmp.Options(autoApply: false), NullLogger<SummaryDocWriter>.Instance);
        var board = MissingDocBoard();

        var result = await writer.ApplyAsync(board, Empty(), CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Contains(result.Surfaced, s => s.Contains("missing pm/repos/foo.md"));
        Assert.False(File.Exists(tmp.DocPath("foo")));
    }

    [Fact]
    public async Task AutoSafe_apply_when_enabled_drafts_the_missing_summary_stub()
    {
        using var tmp = new TempGad();
        var writer = new SummaryDocWriter(tmp.Options(autoApply: true), NullLogger<SummaryDocWriter>.Instance);
        var board = MissingDocBoard();
        var glance = new Dictionary<string, GlanceRepo>(StringComparer.OrdinalIgnoreCase)
        {
            ["foo"] = new GlanceRepo("foo", "git", "active", 4, Array.Empty<string>(), "main", false, false, 2, true, false),
        };

        var result = await writer.ApplyAsync(board, glance, CancellationToken.None);

        Assert.Single(result.Applied);
        var path = tmp.DocPath("foo");
        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("# foo", content);
        Assert.Contains("active", content);             // glance-seeded lifecycle
        Assert.Contains("4 uncommitted", content);
        // The credentialed auto-safe item (area path) is never applied — it stays surfaced.
        Assert.Contains(result.Surfaced, s => s.Contains("no area path"));
    }

    [Fact]
    public async Task Runner_degrades_loudly_to_raw_state_when_the_author_is_down()
    {
        var runner = NewRunner(
            author: ScriptedChat.Throwing(),
            critic: ScriptedChat.Returning("[]"),
            maxAttempts: 1);

        var outcome = await runner.PrepareAsync(runJudges: true, CancellationToken.None);

        Assert.Equal(BriefStatus.Ran, outcome.Status);
        Assert.NotNull(outcome.Degrade);
        Assert.Contains("author unavailable", outcome.Degrade!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("objective state", outcome.PostText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runner_posts_the_draft_but_flags_when_the_critic_is_down()
    {
        var runner = NewRunner(
            author: ScriptedChat.Returning("## What changed\n- ember moved"),
            critic: ScriptedChat.Throwing(),
            maxAttempts: 1);

        var outcome = await runner.PrepareAsync(runJudges: true, CancellationToken.None);

        Assert.Equal(BriefStatus.Ran, outcome.Status);
        Assert.Contains("ember moved", outcome.Brief);
        Assert.NotNull(outcome.Degrade);
        Assert.Contains("unreviewed", outcome.Degrade!, StringComparison.OrdinalIgnoreCase);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private static BoardSyncDelta MissingDocBoard() => new(
        InSync: false, BoardAvailable: true, ManifestRepos: 1,
        AutoSafe: new[]
        {
            "foo: missing pm/repos/foo.md  ->  draft from manifest entry",
            "foo: no area path  ->  az boards area project create --name foo",
        },
        Decisions: Array.Empty<string>(), LiveTruth: Array.Empty<string>());

    private static IReadOnlyDictionary<string, GlanceRepo> Empty() =>
        new Dictionary<string, GlanceRepo>();

    private static async Task<BriefInputs> Assemble(string? glanceJson, string? board)
    {
        var opt = Options.Create(new EmberOptions { Graph = new GraphOptions { Enabled = false } });
        var assembler = new BriefAssembler(
            new FakeGlance(opt, glanceJson),
            new FakeBoard(opt, board),
            opt, NullLogger<BriefAssembler>.Instance);
        return await assembler.AssembleAsync(CancellationToken.None);
    }

    private static OvernightRunner NewRunner(IChatClient author, IChatClient critic, int maxAttempts)
    {
        var ember = new EmberOptions { Graph = new GraphOptions { Enabled = false } };
        ember.Overnight.JudgeMaxAttempts = maxAttempts;
        ember.Overnight.JudgeRetryBaseSeconds = 0;
        var opt = Options.Create(ember);
        var assembler = new BriefAssembler(
            new FakeGlance(opt, GlanceJson), new FakeBoard(opt, null), opt, NullLogger<BriefAssembler>.Instance);
        var models = Options.Create(new ModelsOptions
        {
            ReflectA = new ModelOptions { Model = "author-m" },
            ReflectB = new ModelOptions { Model = "critic-m" },
        });
        return new OvernightRunner(assembler, author, critic, models, opt, NullLogger<OvernightRunner>.Instance);
    }

    private sealed class FakeGlance : GlanceReader
    {
        private readonly string? _json;
        public FakeGlance(IOptions<EmberOptions> o, string? json) : base(o, NullLogger<GlanceReader>.Instance)
        {
            // Enable the seam so ReadAsync calls RunGlanceAsync (overridden below).
            o.Value.Reflect.Glance.Enabled = true;
            o.Value.Reflect.Glance.ScriptPath = "fake.py";
            _json = json;
        }
        protected override Task<string?> RunGlanceAsync(CancellationToken ct) => Task.FromResult(_json);
    }

    private sealed class FakeBoard : BoardSyncReader
    {
        private readonly string? _json;
        public FakeBoard(IOptions<EmberOptions> o, string? json) : base(o, NullLogger<BoardSyncReader>.Instance)
        {
            // Enable the seam so ReadAsync calls RunCheckerAsync (overridden below).
            o.Value.Overnight.BoardSync.Enabled = true;
            o.Value.Overnight.BoardSync.ScriptPath = "fake.py";
            _json = json;
        }
        protected override Task<string?> RunCheckerAsync(CancellationToken ct) => Task.FromResult(_json);
    }

    private sealed class ScriptedChat : IChatClient
    {
        private readonly Func<string> _step;
        private ScriptedChat(Func<string> step) => _step = step;
        public static ScriptedChat Returning(string text) => new(() => text);
        public static ScriptedChat Throwing() => new(() => throw new TimeoutException("simulated 503"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _step())));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A throwaway gad tree (<c>pm/scripts/board-sync-check.py</c>) so GadRoot resolves.</summary>
    private sealed class TempGad : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"ember-gad-{Guid.NewGuid():N}");

        public TempGad()
        {
            Directory.CreateDirectory(Path.Combine(_root, "pm", "scripts"));
            File.WriteAllText(Path.Combine(_root, "pm", "scripts", "board-sync-check.py"), "# stub");
        }

        public IOptions<EmberOptions> Options(bool autoApply)
        {
            var e = new EmberOptions();
            e.Overnight.AutoApplyAutoSafe = autoApply;
            e.Overnight.CommitArtifacts = false; // no git in the temp tree
            e.Overnight.BoardSync.ScriptPath = Path.Combine(_root, "pm", "scripts", "board-sync-check.py");
            return Microsoft.Extensions.Options.Options.Create(e);
        }

        public string DocPath(string name) => Path.Combine(_root, "pm", "repos", $"{name}.md");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
