using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Pins the comparer's structured-output contract with a scripted chat client: valid JSON
/// parses, anything else gets exactly one retry, and persistent garbage soft-fails to null.
/// </summary>
public class DivergenceComparerTests
{
    private const string ValidJson =
        """
        {"agreements":["leopard shipped the Explorer"],
         "divergences":[{"topic":"risk","aSays":"tests are thin","bSays":"coverage is fine"}]}
        """;

    [Fact]
    public async Task Valid_json_parses_into_comparison()
    {
        var chat = new StubChatClient(ValidJson);
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Agreements);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal("risk", divergence.Topic);
        Assert.Equal("tests are thin", divergence.ASays);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Garbage_then_valid_json_succeeds_on_retry()
    {
        var chat = new StubChatClient("not json", ValidJson);
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task Persistent_garbage_soft_fails_to_null()
    {
        var chat = new StubChatClient("nope", "still nope");
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, chat.Calls);
    }

    /// <summary>Returns scripted responses in order; repeats "" when exhausted.</summary>
    internal sealed class StubChatClient : IChatClient
    {
        private readonly Queue<string> _responses;

        public int Calls { get; private set; }

        public StubChatClient(params string[] responses) => _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = _responses.Count > 0 ? _responses.Dequeue() : "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
