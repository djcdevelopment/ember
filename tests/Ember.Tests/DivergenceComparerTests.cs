using Ember.Reflect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Pins the comparer's structured-output contract (now XML — comparer-schema/v2-xml): valid
/// XML parses, anything else gets exactly one retry, and persistent garbage soft-fails to null.
/// </summary>
public class DivergenceComparerTests
{
    private const string ValidXml =
        """
        <comparison>
          <agreements><item>leopard shipped the Explorer</item></agreements>
          <divergences>
            <divergence>
              <topic>risk</topic>
              <kind>contradiction</kind>
              <a>tests are thin</a>
              <b>coverage is fine</b>
            </divergence>
          </divergences>
        </comparison>
        """;

    [Fact]
    public async Task Valid_xml_parses_into_comparison()
    {
        var chat = new StubChatClient(ValidXml);
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Agreements);
        var divergence = Assert.Single(result.Divergences);
        Assert.Equal("risk", divergence.Topic);
        Assert.Equal("contradiction", divergence.Kind);
        Assert.Equal("tests are thin", divergence.ASays);
        Assert.Equal("coverage is fine", divergence.BSays);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Garbage_then_valid_xml_succeeds_on_retry()
    {
        var chat = new StubChatClient("not xml at all", ValidXml);
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task Bare_ampersand_in_content_still_parses_first_try()
    {
        // Local models emit tags but not entity-escaping, and the domain text has "&"
        // ("Threads & risks"). The pre-parse sanitizer must rescue this without a retry.
        const string xmlWithAmp =
            """
            <comparison>
              <agreements><item>both note Threads & risks coverage</item></agreements>
              <divergences></divergences>
            </comparison>
            """;
        var chat = new StubChatClient(xmlWithAmp);
        var comparer = new DivergenceComparer(chat, NullLogger<DivergenceComparer>.Instance);

        var result = await comparer.CompareAsync("recap a", "recap b", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Agreements);
        Assert.Equal(1, chat.Calls); // parsed on the first attempt — no retry needed
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
