using System.Diagnostics;
using System.Text;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Manifest;

/// <summary>
/// Reads a <c>constellation.yaml</c> via the manifest framework's
/// <c>framework load &lt;path&gt; --json</c> CLI and renders the short summary folded
/// into the round-1 planner prompt. Failure modes are uniformly soft: every IO / parse /
/// schema problem returns <c>null</c> with a logged warning so a manifest issue cannot
/// block planning.
/// </summary>
public class ManifestLoader
{
    private readonly EmberOptions _options;
    private readonly ILogger<ManifestLoader> _logger;

    public ManifestLoader(IOptions<EmberOptions> options, ILogger<ManifestLoader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Reads the manifest at <paramref name="constellationPath"/> via the framework CLI and
    /// returns the round-1 summary from the perspective of <paramref name="repoKey"/>. Returns
    /// <c>null</c> on any failure (the warning is logged, the planner runs without manifest
    /// context).
    /// </summary>
    public async Task<string?> LoadSummaryAsync(
        string constellationPath, string repoKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(constellationPath))
            return null;
        if (!File.Exists(constellationPath))
        {
            _logger.LogWarning(
                "Constellation manifest path does not exist: {Path}. Proceeding without manifest context.",
                constellationPath);
            return null;
        }

        var json = await LoadJsonAsync(constellationPath, ct);
        if (json is null)
            return null;

        var summary = ManifestSummary.TryFormat(
            json, repoKey, _options.Manifest.MaxSchemaVersion, out var failure);
        if (summary is null)
        {
            _logger.LogWarning(
                "Could not render manifest summary for {Path}: {Failure}. Proceeding without manifest context.",
                constellationPath, failure);
        }
        return summary;
    }

    /// <summary>
    /// Spawns <c>framework load &lt;path&gt; --json</c> (or whatever <see cref="ManifestOptions"/>
    /// resolves to) and returns the JSON document on stdout. <c>null</c> on non-zero exit,
    /// missing executable, timeout, or any other failure — with the framework's stderr or the
    /// exception logged for the operator.
    /// </summary>
    /// <remarks>
    /// <c>protected virtual</c> so test fixtures can override it with canned JSON instead of
    /// spawning the framework. Production code uses this implementation.
    /// </remarks>
    protected virtual async Task<string?> LoadJsonAsync(string constellationPath, CancellationToken ct)
    {
        var manifest = _options.Manifest;
        var psi = new ProcessStartInfo
        {
            FileName = manifest.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var extra in manifest.ExtraArgs)
            psi.ArgumentList.Add(extra);
        psi.ArgumentList.Add("load");
        psi.ArgumentList.Add(constellationPath);
        psi.ArgumentList.Add("--json");

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not start the manifest framework ({Command}). Proceeding without manifest context.",
                manifest.Command);
            return null;
        }

        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(Math.Max(1, manifest.TimeoutSeconds));
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (ct.IsCancellationRequested)
                    return null;
                _logger.LogWarning(
                    "Manifest framework exceeded its {Timeout}s timeout reading {Path}; proceeding without manifest context.",
                    timeout.TotalSeconds, constellationPath);
                return null;
            }

            // process.ExitCode is read here, while still inside the `using` — disposing the
            // Process object before reading it throws "No process is associated with this object."
            if (process.ExitCode != 0)
            {
                var detail = stderr.ToString().Trim();
                _logger.LogWarning(
                    "Manifest framework exited {Code} reading {Path}: {Detail}",
                    process.ExitCode, constellationPath,
                    string.IsNullOrEmpty(detail) ? "(no stderr)" : detail);
                return null;
            }

            return stdout.ToString();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited between the check and the kill.
        }
    }
}
