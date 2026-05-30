using System.IO;
using Ember.Manifest;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// <see cref="ManifestSummary.TryFormat"/> is the pure parse-and-format step. These tests
/// drive it directly from the captured GAD JSON fixture so the asserted tokens are the
/// observable evidence of "manifest facts landed in the prompt" — the Slice B acceptance
/// gate. Re-render the fixture from <c>D:\\work\\gad\\constellation.yaml</c> when the
/// upstream manifest moves.
/// </summary>
public class ManifestSummaryTests
{
    private static string GadJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gad.json"));

    [Fact]
    public void GAD_summary_for_tempo_contains_constellation_facts()
    {
        var summary = ManifestSummary.TryFormat(GadJson(), "tempo", maxSchemaVersion: 1, out var failure);

        Assert.Equal(ManifestSummary.Failure.None, failure);
        Assert.NotNull(summary);

        // The acceptance tokens — these are what the planner needs to see in round 1.
        Assert.Contains("GAD", summary);
        Assert.Contains("This repo (tempo)", summary);
        Assert.Contains("centerpiece", summary);   // tempo's role
        Assert.Contains("producer_type: engine", summary);
        Assert.Contains("lifecycle: active", summary);
        Assert.Contains("Surfaces: MainWindow", summary);
        Assert.Contains("Neighbor repos in this constellation:", summary);
        Assert.Contains("raidui", summary);
        Assert.Contains("lantern", summary);
        Assert.Contains("ember", summary);
        Assert.Contains("hearth", summary);
        Assert.Contains("campfire", summary);
        Assert.Contains("battlemage", summary);
        Assert.Contains("Active saga epics:", summary);
        Assert.Contains("79", summary);
    }

    [Fact]
    public void GAD_summary_for_ember_finds_the_self_record_and_excludes_it_from_neighbors()
    {
        var summary = ManifestSummary.TryFormat(GadJson(), "ember", maxSchemaVersion: 1, out _);

        Assert.NotNull(summary);
        Assert.Contains("This repo (ember)", summary);
        // ember should NOT appear in the neighbors list; tempo should.
        var neighborSection = summary[summary.IndexOf("Neighbor repos", StringComparison.Ordinal)..];
        Assert.DoesNotContain("- ember", neighborSection);
        Assert.Contains("- tempo", neighborSection);
    }

    [Fact]
    public void Alias_matches_the_repo_record_via_aliases_list()
    {
        // raidui has alias "newui" in the manifest — invoking `/plan newui` should still
        // resolve to the raidui record (case-insensitively).
        var summary = ManifestSummary.TryFormat(GadJson(), "newui", maxSchemaVersion: 1, out _);

        Assert.NotNull(summary);
        Assert.Contains("This repo (raidui)", summary);
    }

    [Fact]
    public void Unknown_repo_key_keeps_neighbors_but_omits_the_self_line()
    {
        var summary = ManifestSummary.TryFormat(GadJson(), "no-such-repo", maxSchemaVersion: 1, out var failure);

        Assert.Equal(ManifestSummary.Failure.None, failure);
        Assert.NotNull(summary);
        Assert.DoesNotContain("This repo", summary);
        Assert.Contains("Neighbor repos in this constellation:", summary);
        // All seven repos should appear when none is the matched self-record.
        Assert.Contains("- tempo", summary);
        Assert.Contains("- ember", summary);
    }

    [Fact]
    public void Schema_version_too_new_returns_null_with_failure()
    {
        // GAD JSON is schema_version: 1. Cap at 0 to simulate "framework newer than I know."
        var summary = ManifestSummary.TryFormat(GadJson(), "tempo", maxSchemaVersion: 0, out var failure);

        Assert.Null(summary);
        Assert.Equal(ManifestSummary.Failure.SchemaVersionTooNew, failure);
    }

    [Fact]
    public void Invalid_json_returns_null_with_failure()
    {
        var summary = ManifestSummary.TryFormat("not json at all", "tempo", 1, out var failure);

        Assert.Null(summary);
        Assert.Equal(ManifestSummary.Failure.InvalidJson, failure);
    }

    [Fact]
    public void Missing_constellation_block_returns_null_with_failure()
    {
        // schema_version is present but the required constellation block is absent.
        var summary = ManifestSummary.TryFormat(
            """{"schema_version":1,"archetype":"presence","topology":"centered","intent":null,"repos":[]}""",
            "tempo", 1, out var failure);

        Assert.Null(summary);
        Assert.Equal(ManifestSummary.Failure.MissingConstellation, failure);
    }

    [Fact]
    public void Intent_line_is_omitted_when_null_and_present_when_set()
    {
        var withIntent = """
        {
          "schema_version": 1,
          "archetype": "presence",
          "topology": "centered",
          "intent": "guild-software substrate; raiding is the load-bearing audience",
          "constellation": { "name": "GAD", "description": null },
          "saga": null,
          "repos": []
        }
        """;
        var withoutIntent = """
        {
          "schema_version": 1,
          "archetype": "presence",
          "topology": "centered",
          "intent": null,
          "constellation": { "name": "GAD", "description": null },
          "saga": null,
          "repos": []
        }
        """;

        var s1 = ManifestSummary.TryFormat(withIntent, "x", 1, out _);
        var s2 = ManifestSummary.TryFormat(withoutIntent, "x", 1, out _);

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.Contains("Intent: guild-software substrate", s1);
        Assert.DoesNotContain("Intent:", s2);
    }
}
