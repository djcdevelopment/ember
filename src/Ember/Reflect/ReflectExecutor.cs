using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Ember.Config;
using Ember.Discord;
using Ember.Observability;
using Microsoft.Extensions.Options;

namespace Ember.Reflect;

/// <summary>
/// Owns one full reflect run end to end: runner → Discord thread → persistence → baseline
/// advance. Serialized — a scheduled run and a manual <c>/reflect</c> cannot overlap.
/// Baselines advance only on non-failed runs, so a failed night re-reports the same delta
/// rather than losing it.
/// </summary>
public sealed class ReflectExecutor
{
    private const int MaxThreadNameLength = 90;

    private readonly ReflectRunner _runner;
    private readonly RecapStore _store;
    private readonly DiscordSocketClient _client;
    private readonly ThreadGateway _threads;
    private readonly EmberOptions _options;
    private readonly ILogger<ReflectExecutor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReflectExecutor(
        ReflectRunner runner,
        RecapStore store,
        DiscordSocketClient client,
        ThreadGateway threads,
        IOptions<EmberOptions> options,
        ILogger<ReflectExecutor> logger)
    {
        _runner = runner;
        _store = store;
        _client = client;
        _threads = threads;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs reflect now. Returns a one-line summary for the caller.</summary>
    public async Task<string> ExecuteAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            return "A reflect run is already in progress.";

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
        var recap = new Recap { Date = DateTime.Now.ToString("yyyy-MM-dd") };
        ReflectOutcome? outcome = null;

        try
        {
            outcome = await _runner.PrepareAsync(
                new BaselineMode.LastRecorded(_store.GetShas()), runJudges: true, ct);

            recap.ReposJson = outcome.ReposJson;
            recap.EvidenceChars = outcome.Evidence.TotalChars;
            recap.JudgeAModel = outcome.JudgeAModel;
            recap.JudgeBModel = outcome.JudgeBModel;
            recap.RecapA = outcome.RecapA;
            recap.RecapB = outcome.RecapB;
            recap.DivergencesJson = outcome.Comparison is null
                ? null
                : JsonSerializer.Serialize(outcome.Comparison);
            recap.Status = outcome.Status;
            recap.Error = outcome.Error;

            if (outcome.Status == RecapStatus.Ran)
                await PostAsync(recap, outcome.PostText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            recap.Status = RecapStatus.Failed;
            recap.Error = ex.Message;
            _logger.LogError(ex, "Reflect run failed.");
        }

        TryPersist(recap, outcome);

        Telemetry.RecapsCompleted.Add(1, new KeyValuePair<string, object?>("outcome", recap.Status.ToString()));
        return recap.Status switch
        {
            RecapStatus.Ran => $"Recap posted — {recap.ReposJson} ({recap.EvidenceChars} evidence chars).",
            RecapStatus.Skipped => "No committed changes since the last recap; baselines recorded.",
            _ => $"Reflect failed: {recap.Error}",
        };
    }

    private async Task PostAsync(Recap recap, string postText)
    {
        var channelId = _options.Reflect.ChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || !ulong.TryParse(channelId, out var id))
            throw new InvalidOperationException("Ember:Reflect:ChannelId is not a valid channel id.");
        if (_client.GetChannel(id) is not ITextChannel channel)
            throw new InvalidOperationException($"Reflect channel {channelId} not found or not a text channel.");

        var name = $"reflect: {recap.Date}";
        if (name.Length > MaxThreadNameLength)
            name = name[..MaxThreadNameLength];
        var thread = await channel.CreateThreadAsync(name, ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
        recap.ThreadId = thread.Id.ToString();

        await _threads.PostAsync(recap.ThreadId, postText);

        var labelMessage = await _threads.CreateMessageAsync(recap.ThreadId,
            "**How did the recap do?** React ✅ accurate · ✏️ partially · ❌ wrong. "
            + "Anything you reply in this thread is kept as correction context.");
        recap.MessageId = labelMessage?.Id.ToString();
    }

    private void TryPersist(Recap recap, ReflectOutcome? outcome)
    {
        try
        {
            _store.Create(recap);

            if (recap.Status != RecapStatus.Failed && outcome is not null)
            {
                foreach (var repo in outcome.Evidence.Repos)
                {
                    if (repo.ToSha is not null)
                        _store.SetSha(repo.Repo, repo.ToSha);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reflect: could not persist recap row / baselines.");
        }
    }
}
