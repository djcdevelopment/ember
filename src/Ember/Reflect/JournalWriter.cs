using System.Diagnostics;
using System.Text;
using Ember.Config;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>
/// Writes each real recap to a dated markdown file and, when configured, commits it — so a
/// scheduled (cron) or on-demand <c>/reflect</c> run leaves a durable git trail and the
/// constellation-awareness corpus accrues automatically (ADR 15).
///
/// Additive and by-name only: never <c>git add .</c>, never a history-rewriting op (the repo
/// rule). Entirely soft — a journal-write or commit failure is logged and never fails the run,
/// which has already posted to Discord. The console <c>reflect</c> taste does not use this;
/// only the real executor path journals.
/// </summary>
public sealed class JournalWriter
{
    private readonly ReflectOptions _options;
    private readonly ILogger<JournalWriter> _logger;

    public JournalWriter(IOptions<EmberOptions> options, ILogger<JournalWriter> logger)
    {
        _options = options.Value.Reflect;
        _logger = logger;
    }

    /// <summary>
    /// Writes the recap markdown for a date and, if <c>CommitArtifacts</c> is set, commits it
    /// in its repo. Returns the path written, or <c>null</c> when journaling is off or failed.
    /// </summary>
    public Task<string?> WriteAsync(string date, string markdown, CancellationToken ct) =>
        WriteAsync(_options.JournalDir, _options.CommitArtifacts, date, markdown, ct);

    /// <summary>
    /// Writes a dated markdown artifact under <paramref name="dir"/> and optionally commits it —
    /// the config-agnostic form so both Reflect and the overnight brief can journal (ADR 15/19).
    /// <paramref name="kind"/> labels the commit subject (e.g. "recap", "brief").
    /// </summary>
    public async Task<string?> WriteAsync(
        string dir, bool commit, string date, string markdown, CancellationToken ct, string kind = "reflect: recap")
    {
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        string path;
        try
        {
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{date}.md");
            await File.WriteAllTextAsync(path, markdown, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Journal: could not write artifact under {Dir}.", dir);
            return null;
        }

        if (commit)
            await TryCommitAsync(path, date, ct, kind);
        return path;
    }

    private async Task TryCommitAsync(string filePath, string date, CancellationToken ct, string kind = "reflect: recap")
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath)!;
            var root = (await GitAsync(dir, ["rev-parse", "--show-toplevel"], ct))?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                _logger.LogWarning("Reflect: journal dir {Dir} is not inside a git repo; not committing.", dir);
                return;
            }

            // Stage only this file, by name — never `git add .`.
            await GitAsync(root, ["add", "--", filePath], ct);
            var committed = await GitAsync(root, ["commit", "-m", $"{kind} {date} (automated)"], ct);
            if (committed is null)
                _logger.LogInformation("Reflect: nothing to commit for {Date} (recap unchanged).", date);
            else
                _logger.LogInformation("Reflect: committed journal entry for {Date}.", date);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reflect: journal commit failed (non-fatal).");
        }
    }

    /// <summary>Runs git in a directory and returns stdout, or null on any non-zero / failure.</summary>
    private async Task<string?> GitAsync(string cwd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("git did not start");

        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }

        return process.ExitCode == 0 ? stdout.ToString() : null;
    }
}
