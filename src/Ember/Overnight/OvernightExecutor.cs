using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Discord;
using Ember.Reflect;
using Microsoft.Extensions.Options;

namespace Ember.Overnight;

/// <summary>
/// Owns one full overnight run end to end: runner → auto-safe apply (E3) → Discord thread →
/// journal → persistence. Serialized — a scheduled run and a manual <c>/brief</c> cannot overlap.
/// The brief itself is read-only synthesis; the only writes are the gated, in-repo auto-safe
/// reconciliations (drafting missing summary docs), and they are reported in the post so nothing
/// the machine did is silent. Editorial / Discord / credentialed board ops are never auto-run.
/// </summary>
public sealed class OvernightExecutor
{
    private const int MaxThreadNameLength = 90;

    private readonly OvernightRunner _runner;
    private readonly SummaryDocWriter _autoSafe;
    private readonly BriefStore _store;
    private readonly JournalWriter _journal;
    private readonly DiscordSocketClient _client;
    private readonly ThreadGateway _threads;
    private readonly OvernightOptions _options;
    private readonly ILogger<OvernightExecutor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OvernightExecutor(
        OvernightRunner runner,
        SummaryDocWriter autoSafe,
        BriefStore store,
        JournalWriter journal,
        DiscordSocketClient client,
        ThreadGateway threads,
        IOptions<EmberOptions> options,
        ILogger<OvernightExecutor> logger)
    {
        _runner = runner;
        _autoSafe = autoSafe;
        _store = store;
        _journal = journal;
        _client = client;
        _threads = threads;
        _options = options.Value.Overnight;
        _logger = logger;
    }

    /// <summary>Runs the overnight planner now. Returns a one-line summary for the caller.</summary>
    public async Task<string> ExecuteAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            return "An overnight run is already in progress.";
        try
        {
            return await RunOnceAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RunOnceAsync(CancellationToken ct)
    {
        var brief = new Brief { Date = DateTime.Now.ToString("yyyy-MM-dd") };
        OvernightOutcome? outcome = null;
        AutoSafeResult applied = AutoSafeResult.None;

        try
        {
            outcome = await _runner.PrepareAsync(runJudges: true, ct);

            // E3 — apply only the in-repo auto-safe tier (gated); surface the rest.
            applied = await _autoSafe.ApplyAsync(outcome.Inputs.Board, outcome.Inputs.Glance, ct);

            var postText = ComposePost(outcome, applied);

            brief.GlanceRepos = outcome.Inputs.GlanceRepoCount;
            brief.EvidenceChars = outcome.Inputs.TotalChars;
            brief.AuthorModel = outcome.AuthorModel;
            brief.CriticModel = outcome.CriticModel;
            brief.BriefText = outcome.Brief;
            brief.CriticIssuesJson = JsonSerializer.Serialize(outcome.CriticIssues);
            brief.AppliedJson = JsonSerializer.Serialize(applied.Applied);
            brief.Status = outcome.Status;
            brief.Error = outcome.Error;

            if (outcome.Status == BriefStatus.Ran)
            {
                await PostAsync(brief, postText);
                await _journal.WriteAsync(
                    _options.JournalDir, _options.CommitArtifacts, brief.Date, postText, ct, kind: "overnight: brief");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            brief.Status = BriefStatus.Failed;
            brief.Error = ex.Message;
            _logger.LogError(ex, "Overnight run failed.");
        }

        TryPersist(brief);

        var degraded = outcome?.Degrade is not null ? " [degraded — see banner]" : "";
        var appliedNote = applied.DidAnything ? $", {applied.Applied.Count} auto-safe applied" : "";
        return brief.Status == BriefStatus.Ran
            ? $"Brief posted — {brief.GlanceRepos} repos ({brief.EvidenceChars} chars){appliedNote}{degraded}."
            : $"Overnight failed: {brief.Error}";
    }

    /// <summary>The post = the composed brief + a factual footer of what E3 actually applied / surfaced.</summary>
    private static string ComposePost(OvernightOutcome outcome, AutoSafeResult applied)
    {
        var sb = new StringBuilder();
        sb.Append(outcome.PostText.TrimEnd());

        if (applied.Applied.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("**Auto-applied (auto-safe, reversible):**");
            foreach (var a in applied.Applied)
                sb.AppendLine($"- drafted `{a}`");
        }
        if (applied.Surfaced.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Surfaced for you (needs creds or a decision — not auto-run):**");
            foreach (var s in applied.Surfaced.Take(20))
                sb.AppendLine($"- {FirstLine(s)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }

    private async Task PostAsync(Brief brief, string postText)
    {
        var channelId = _options.ChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || !ulong.TryParse(channelId, out var id))
            throw new InvalidOperationException("Ember:Overnight:ChannelId is not a valid channel id.");
        if (_client.GetChannel(id) is not ITextChannel channel)
            throw new InvalidOperationException($"Overnight channel {channelId} not found or not a text channel.");

        var name = $"brief: {brief.Date}";
        if (name.Length > MaxThreadNameLength)
            name = name[..MaxThreadNameLength];
        var thread = await channel.CreateThreadAsync(name, ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
        brief.ThreadId = thread.Id.ToString();

        await _threads.PostAsync(brief.ThreadId, postText);

        var labelMessage = await _threads.CreateMessageAsync(brief.ThreadId,
            "**How did the brief do?** React ✅ accurate · ✏️ partially · ❌ wrong. "
            + "Anything you reply in this thread is kept as correction context.");
        brief.MessageId = labelMessage?.Id.ToString();
    }

    private void TryPersist(Brief brief)
    {
        try
        {
            _store.Create(brief);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overnight: could not persist brief row.");
        }
    }
}
