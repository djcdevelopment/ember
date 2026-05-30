using System.IO;
using Ember.Config;
using Ember.Manifest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Drives the orchestration in <see cref="ManifestLoader.LoadSummaryAsync"/> without
/// spawning the framework — the protected <c>LoadJsonAsync</c> is overridden with canned
/// JSON, a canned null, or a canned exception so we can pin every documented failure path.
/// The live subprocess is exercised separately in
/// <see cref="ManifestLoaderIntegrationTests"/>.
/// </summary>
public class ManifestLoaderTests
{
    private static string GadJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gad.json"));

    [Fact]
    public async Task LoadSummaryAsync_returns_formatted_summary_when_subprocess_succeeds()
    {
        var loader = new FakeLoader(Opts(), GadJson());
        // The constellationPath must exist on disk — LoadSummaryAsync short-circuits otherwise.
        using var fixture = TempFixture();

        var summary = await loader.LoadSummaryAsync(fixture.Path, "tempo", CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Contains("This repo (tempo)", summary);
        Assert.Contains("Neighbor repos", summary);
    }

    [Fact]
    public async Task LoadSummaryAsync_returns_null_when_subprocess_returns_null()
    {
        var loader = new FakeLoader(Opts(), cannedJson: null);
        using var fixture = TempFixture();

        var summary = await loader.LoadSummaryAsync(fixture.Path, "tempo", CancellationToken.None);

        Assert.Null(summary);
    }

    [Fact]
    public async Task LoadSummaryAsync_returns_null_when_json_is_malformed()
    {
        var loader = new FakeLoader(Opts(), cannedJson: "this is not json");
        using var fixture = TempFixture();

        var summary = await loader.LoadSummaryAsync(fixture.Path, "tempo", CancellationToken.None);

        Assert.Null(summary);
    }

    [Fact]
    public async Task LoadSummaryAsync_returns_null_when_constellation_path_missing()
    {
        // No fixture file; the subprocess never runs because the path doesn't exist.
        var loader = new FakeLoader(Opts(), GadJson());

        var summary = await loader.LoadSummaryAsync(
            "D:\\definitely\\does\\not\\exist\\constellation.yaml", "tempo", CancellationToken.None);

        Assert.Null(summary);
    }

    [Fact]
    public async Task LoadSummaryAsync_returns_null_when_constellation_path_blank()
    {
        var loader = new FakeLoader(Opts(), GadJson());

        var summary = await loader.LoadSummaryAsync("", "tempo", CancellationToken.None);

        Assert.Null(summary);
    }

    [Fact]
    public async Task LoadSummaryAsync_returns_null_when_schema_version_too_new()
    {
        var loader = new FakeLoader(Opts(maxSchemaVersion: 0), GadJson());
        using var fixture = TempFixture();

        var summary = await loader.LoadSummaryAsync(fixture.Path, "tempo", CancellationToken.None);

        Assert.Null(summary);
    }

    private static IOptions<EmberOptions> Opts(int maxSchemaVersion = 1) =>
        Options.Create(new EmberOptions { Manifest = new ManifestOptions { MaxSchemaVersion = maxSchemaVersion } });

    private static TempFile TempFixture() =>
        new(Path.GetTempFileName());

    private sealed class FakeLoader : ManifestLoader
    {
        private readonly string? _cannedJson;

        public FakeLoader(IOptions<EmberOptions> options, string? cannedJson)
            : base(options, NullLogger<ManifestLoader>.Instance)
        {
            _cannedJson = cannedJson;
        }

        protected override Task<string?> LoadJsonAsync(string constellationPath, CancellationToken ct)
            => Task.FromResult(_cannedJson);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string path) { Path = path; }
        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort */ }
        }
    }
}
