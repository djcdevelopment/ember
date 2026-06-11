using Ember.Config;
using Ember.Reflect;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>Round-trips the reflect tables against a throwaway SQLite file.</summary>
public sealed class RecapStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"ember-recap-tests-{Guid.NewGuid():N}.db");

    private RecapStore NewStore()
    {
        var store = new RecapStore(
            Options.Create(new EmberOptions { DatabasePath = _dbPath }),
            NullLogger<RecapStore>.Instance);
        store.Initialize();
        return store;
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        var store = NewStore();
        store.Initialize();
        Assert.Null(store.LatestRunDate());
    }

    [Fact]
    public void Create_assigns_id_and_latest_run_date_reflects_it()
    {
        var store = NewStore();

        var id = store.Create(new Recap { Date = "2026-06-11", Status = RecapStatus.Ran });

        Assert.True(id > 0);
        Assert.Equal("2026-06-11", store.LatestRunDate());
    }

    [Fact]
    public void SetLabel_updates_by_message_id()
    {
        var store = NewStore();
        store.Create(new Recap { Date = "2026-06-11", MessageId = "12345" });

        Assert.True(store.SetLabel("12345", RecapLabels.Accurate));
        Assert.False(store.SetLabel("99999", RecapLabels.Wrong));
    }

    [Fact]
    public void Shas_roundtrip_and_upsert()
    {
        var store = NewStore();

        store.SetSha("leopard", "aaa111");
        store.SetSha("ember", "bbb222");
        store.SetSha("leopard", "ccc333"); // upsert

        var shas = store.GetShas();
        Assert.Equal("ccc333", shas["leopard"]);
        Assert.Equal("bbb222", shas["ember"]);
        Assert.Equal(2, shas.Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
