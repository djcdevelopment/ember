using Discord;
using Ember.Reflect;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// The loopback trigger's gate: a run needs Reflect enabled and a live Discord link, and the
/// request line parses to a method + query-stripped path.
/// </summary>
public class ReflectTriggerTests
{
    [Fact]
    public void Not_ready_when_reflect_disabled()
    {
        var (ready, reason) = ReflectTriggerService.Decide(enabled: false, ConnectionState.Connected);
        Assert.False(ready);
        Assert.Contains("disabled", reason);
    }

    [Fact]
    public void Not_ready_while_discord_is_still_connecting()
    {
        var (ready, reason) = ReflectTriggerService.Decide(enabled: true, ConnectionState.Connecting);
        Assert.False(ready);
        Assert.Contains("not ready", reason);
    }

    [Fact]
    public void Ready_when_enabled_and_connected()
    {
        var (ready, reason) = ReflectTriggerService.Decide(enabled: true, ConnectionState.Connected);
        Assert.True(ready);
        Assert.Equal("ready", reason);
    }

    [Theory]
    [InlineData("POST /reflect HTTP/1.1", "POST", "/reflect")]
    [InlineData("get /ready HTTP/1.1", "GET", "/ready")]
    [InlineData("GET /ready?nonce=42 HTTP/1.1", "GET", "/ready")]
    [InlineData("", "", "/")]
    public void Parses_method_and_query_stripped_path(string line, string method, string path)
    {
        var (m, p) = ReflectTriggerService.ParseRequestLine(line);
        Assert.Equal(method, m);
        Assert.Equal(path, p);
    }

    [Theory]
    [InlineData("A reflect run is already in progress.", true)]
    [InlineData("Recap posted — [\"ember\"] (589 evidence chars).", false)]
    [InlineData("No committed changes since the last recap; baselines recorded.", false)]
    [InlineData("Reflect failed: judge endpoint unreachable.", false)]
    public void Detects_an_already_running_run(string summary, bool inProgress)
    {
        // A 409 (don't tear down the GPUs — another run owns them) vs a 200 (run finished).
        Assert.Equal(inProgress, ReflectTriggerService.RunInProgress(summary));
    }
}
