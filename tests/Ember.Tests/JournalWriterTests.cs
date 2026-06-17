using Ember.Config;
using Ember.Reflect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// The journal artifact write (the git-trail enabler). The commit path shells git and is
/// left to integration use; these pin the write behaviour and the off-switch.
/// </summary>
public sealed class JournalWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ember-journal-tests-{Guid.NewGuid():N}");

    private JournalWriter New(string journalDir) =>
        new(Options.Create(new EmberOptions
        {
            Reflect = new ReflectOptions { JournalDir = journalDir, CommitArtifacts = false },
        }), NullLogger<JournalWriter>.Instance);

    [Fact]
    public async Task WriteAsync_writes_the_dated_markdown_file()
    {
        var writer = New(_dir);

        var path = await writer.WriteAsync("2026-06-17", "# recap\nhello", CancellationToken.None);

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_dir, "2026-06-17.md"), path);
        Assert.Equal("# recap\nhello", await File.ReadAllTextAsync(path!));
    }

    [Fact]
    public async Task WriteAsync_returns_null_when_journaling_is_off()
    {
        var writer = New(journalDir: "");
        Assert.Null(await writer.WriteAsync("2026-06-17", "x", CancellationToken.None));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
