using System.Diagnostics;
using System.IO;
using Ember.Config;
using Ember.Manifest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// End-to-end check that the production <see cref="ManifestLoader"/> can actually shell out to
/// the manifest framework and consume <c>D:\\work\\gad\\constellation.yaml</c> — the
/// reference-consumer integration target. Skips cleanly when the live manifest or the
/// framework CLI is not on this host so CI without the GAD checkout still passes.
/// </summary>
public class ManifestLoaderIntegrationTests
{
    private const string GadConstellation = @"D:\work\gad\constellation.yaml";

    [SkippableFact]
    public async Task Real_subprocess_against_live_GAD_yaml_yields_summary_with_expected_tokens()
    {
        Skip.IfNot(File.Exists(GadConstellation), $"Live constellation not present at {GadConstellation}");
        Skip.IfNot(PythonCliWorks(), "python -m constellation_manifest.cli is not available on this host.");

        var options = Options.Create(new EmberOptions
        {
            Manifest = new ManifestOptions
            {
                Command = "python",
                ExtraArgs = new() { "-m", "constellation_manifest.cli" },
                TimeoutSeconds = 30,
                MaxSchemaVersion = 1,
            },
        });
        var loader = new ManifestLoader(options, NullLogger<ManifestLoader>.Instance);

        var summary = await loader.LoadSummaryAsync(GadConstellation, "tempo", CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Contains("GAD", summary);
        Assert.Contains("This repo (tempo)", summary);
        Assert.Contains("raidui", summary);
    }

    /// <summary>Quick "is the framework on this host" probe — avoids a hard test failure on a stock CI box.</summary>
    private static bool PythonCliWorks()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                ArgumentList = { "-m", "constellation_manifest.cli", "--help" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
