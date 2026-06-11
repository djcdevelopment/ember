using Ember.Config;
using Ember.Loop;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Drives <see cref="GraphContext"/> without spawning the CLI — the protected
/// <c>RunToolAsync</c> is overridden with canned JSON per tool, mirroring how
/// <see cref="ManifestLoaderTests"/> pins the manifest seam's failure paths.
/// </summary>
public class GraphContextTests
{
    private const string ArchJson =
        """
        {"total_nodes":100,"total_edges":200,
         "languages":[{"language":"C#","file_count":30},{"language":"JavaScript","file_count":5}],
         "packages":[{"name":"Ember","node_count":50}],
         "entry_points":[{"name":"Program","file":"src/Program.cs"}]}
        """;

    private const string SearchJson =
        """
        {"total":3,"results":[
          {"name":"GateService","label":"Class","file_path":"src/Gate/GateService.cs"},
          {"name":"FireGateAsync","label":"Method","file_path":"src/Gate/GateService.cs"},
          {"name":"notes.md","label":"File","file_path":"notes.md"}]}
        """;

    [Fact]
    public async Task GatherAsync_composes_architecture_and_symbols()
    {
        var graph = new FakeGraph(Opts(), new()
        {
            ["get_architecture"] = ArchJson,
            ["search_graph"] = SearchJson,
        });

        var section = await graph.GatherAsync(@"D:\work\ember", "harden the gate countdown", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Contains("Knowledge graph", section);
        Assert.Contains("C# (30 files)", section);
        Assert.Contains("Entry points: Program (src/Program.cs)", section);
        Assert.Contains("GateService — Class", section);
        Assert.Contains("FireGateAsync — Method", section);
        // File/Folder/Section nodes are noise for the planner and are filtered out.
        Assert.DoesNotContain("notes.md", section);
        // The ground-truth handoff line is always present.
        Assert.Contains("tracked-file list below remains the authority", section);
    }

    [Fact]
    public async Task GatherAsync_returns_architecture_even_when_search_matches_nothing()
    {
        var graph = new FakeGraph(Opts(), new()
        {
            ["get_architecture"] = ArchJson,
            ["search_graph"] = """{"total":0,"results":[]}""",
        });

        var section = await graph.GatherAsync(@"D:\work\ember", "anything", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Contains("Knowledge graph", section);
        Assert.DoesNotContain("Symbols matching the brief", section);
    }

    [Fact]
    public async Task GatherAsync_returns_null_when_disabled()
    {
        var options = Opts();
        options.Value.Graph.Enabled = false;
        var graph = new FakeGraph(options, new() { ["get_architecture"] = ArchJson });

        Assert.Null(await graph.GatherAsync(@"D:\work\ember", "brief", CancellationToken.None));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public async Task GatherAsync_returns_null_when_cli_fails()
    {
        var graph = new FakeGraph(Opts(), new()); // every tool returns null

        Assert.Null(await graph.GatherAsync(@"D:\work\ember", "brief", CancellationToken.None));
    }

    [Fact]
    public async Task GatherAsync_returns_null_on_malformed_architecture()
    {
        var graph = new FakeGraph(Opts(), new() { ["get_architecture"] = "not json at all" });

        Assert.Null(await graph.GatherAsync(@"D:\work\ember", "brief", CancellationToken.None));
    }

    [Fact]
    public async Task GatherAsync_truncates_to_max_chars()
    {
        var options = Opts();
        options.Value.Graph.MaxChars = 80;
        var graph = new FakeGraph(options, new()
        {
            ["get_architecture"] = ArchJson,
            ["search_graph"] = SearchJson,
        });

        var section = await graph.GatherAsync(@"D:\work\ember", "gate", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Contains("(graph context truncated)", section);
        Assert.True(section!.Length < 200);
    }

    [Fact]
    public async Task SymbolsForFilesAsync_returns_null_for_empty_file_list()
    {
        var graph = new FakeGraph(Opts(), new() { ["search_graph"] = SearchJson });

        Assert.Null(await graph.SymbolsForFilesAsync(@"D:\work\ember", [], CancellationToken.None));
    }

    [Fact]
    public async Task SymbolsForFilesAsync_searches_on_file_stems()
    {
        var graph = new FakeGraph(Opts(), new() { ["search_graph"] = SearchJson });

        var symbols = await graph.SymbolsForFilesAsync(
            @"D:\work\ember", ["src/Gate/GateService.cs", "README.md"], CancellationToken.None);

        Assert.NotNull(symbols);
        var (tool, json) = Assert.Single(graph.Calls);
        Assert.Equal("search_graph", tool);
        Assert.Contains("GateService", json);
        Assert.Contains("README", json);
    }

    [Theory]
    [InlineData(@"D:\work\leopard", "D-work-leopard")]
    [InlineData(@"D:\World of Warcraft\Tempo", "D-World of Warcraft-Tempo")]
    [InlineData("D:/work/ember/", "D-work-ember")]
    public void DeriveProjectName_matches_the_cli_convention(string path, string expected) =>
        Assert.Equal(expected, GraphContext.DeriveProjectName(path));

    private static IOptions<EmberOptions> Opts() =>
        Options.Create(new EmberOptions { Graph = new GraphOptions { Enabled = true, MaxChars = 4000 } });

    private sealed class FakeGraph : GraphContext
    {
        private readonly Dictionary<string, string?> _canned;

        public List<(string Tool, string Json)> Calls { get; } = new();

        public FakeGraph(IOptions<EmberOptions> options, Dictionary<string, string?> canned)
            : base(options, NullLogger<GraphContext>.Instance) => _canned = canned;

        protected override Task<string?> RunToolAsync(string tool, string argsJson, CancellationToken ct)
        {
            Calls.Add((tool, argsJson));
            return Task.FromResult(_canned.TryGetValue(tool, out var v) ? v : null);
        }
    }
}
