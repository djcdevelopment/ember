using Discord;
using Discord.WebSocket;

namespace Ember.Discord;

/// <summary>Posts into session threads by id and reads operator messages back out.</summary>
public sealed class ThreadGateway
{
    private const int MaxMessageLength = 1900;

    private readonly DiscordSocketClient _client;
    private readonly ILogger<ThreadGateway> _logger;

    public ThreadGateway(DiscordSocketClient client, ILogger<ThreadGateway> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Sends text to a thread, splitting into multiple messages past Discord's length limit.</summary>
    public async Task PostAsync(string threadId, string text)
    {
        if (_client.GetChannel(ulong.Parse(threadId)) is not IMessageChannel channel)
        {
            _logger.LogWarning("Thread {ThreadId} not found — cannot post.", threadId);
            return;
        }

        foreach (var chunk in Chunk(text))
            await channel.SendMessageAsync(chunk);
    }

    /// <summary>Returns operator (non-bot) messages posted in a thread since the given time.</summary>
    public async Task<string> CollectOperatorMessagesAsync(string threadId, DateTimeOffset since)
    {
        if (_client.GetChannel(ulong.Parse(threadId)) is not IMessageChannel channel)
            return "";

        var messages = await channel.GetMessagesAsync(25).FlattenAsync();
        var operatorText = messages
            .Where(m => !m.Author.IsBot && m.Timestamp > since)
            .OrderBy(m => m.Timestamp)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c));

        return string.Join("\n", operatorText);
    }

    private static IEnumerable<string> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return "(empty)";
            yield break;
        }

        for (var i = 0; i < text.Length; i += MaxMessageLength)
            yield return text.Substring(i, Math.Min(MaxMessageLength, text.Length - i));
    }
}
