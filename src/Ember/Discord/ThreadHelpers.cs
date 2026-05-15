using Discord;

namespace Ember.Discord;

/// <summary>Helpers for creating session threads.</summary>
public static class ThreadHelpers
{
    private const int MaxThreadNameLength = 90;

    /// <summary>Creates a public thread for a planning session in the given channel.</summary>
    public static async Task<IThreadChannel> CreateSessionThreadAsync(ITextChannel channel, string brief)
    {
        var name = $"plan: {brief}";
        if (name.Length > MaxThreadNameLength)
            name = name[..MaxThreadNameLength];

        return await channel.CreateThreadAsync(name, ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
    }
}
