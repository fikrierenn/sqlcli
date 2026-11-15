using SqlCli.Core;

namespace SqlCli.Tests;

// Perf komutlarının dialect katmanı: sunucu GEREKTİRMEYEN kısım (SQL üretimi + girdi muhafızları).
// Gerçek PostgreSQL ile uçtan uca doğrulama README/HANDOFF'ta anlatılan elle testle yapıldı.
public class DialectPerfTests
{
    private static readonly PostgresDialect Pg = new();
    private static readonly SqlServerDialect Ms = new();

    [Fact]
    public void Both_dialects_support_perf()
    {
        Assert.True(Pg.SupportsPerf);
        Assert.True(Ms.SupportsPerf);
    }

    [Fact]
    public void Kill_pg_cancel_and_terminate()
    {
        Assert.Equal("SELECT pg_cancel_backend(42)", Pg.KillSql(42, hard: false));
        Assert.Equal("SELECT pg_terminate_backend(42)", Pg.KillSql(42, hard: true));
    }

    [Fact]
    public void Kill_sqlserver_always_KILL()
    {
        Assert.Equal("KILL 42", Ms.KillSql(42, hard: false));
        Assert.Equal("KILL 42", Ms.KillSql(42, hard: true));
    }

    [Theory]
    [InlineData("avg", "s.mean_exec_time")]
    [InlineData("total", "s.total_exec_time")]
    [InlineData("reads", "s.shared_blks_hit + s.shared_blks_read")]
    [InlineData("executions", "s.calls")]
    public void SlowQueries_pg_ordering(string by, string expected)
    {
        var sql = Pg.SlowQueriesSql(7, by);
        Assert.Contains($"ORDER BY ", sql);
        Assert.Contains(expected, sql);
        Assert.Contains("LIMIT 7", sql);
        Assert.Contains("pg_stat_statements", sql);
    }

    [Fact]
    public void SlowQueries_precheck_only_on_pg()
    {
        Assert.Contains("pg_extension", Pg.SlowQueriesPrecheckSql);
        Assert.Contains("pg_stat_statements", Pg.SlowQueriesPrecheckMessage);
        Assert.Null(Ms.SlowQueriesPrecheckSql);
    }

    [Fact]
    public void ActiveSessions_pg_uses_pg_stat_activity()
    {
        Assert.Contains("pg_stat_activity", Pg.ActiveSessionsSql);
        Assert.Contains("pg_backend_pid()", Pg.ActiveSessionsSql);   // kendi oturumunu listelemez
        Assert.Contains("sys.dm_exec_sessions", Ms.ActiveSessionsSql);
    }

    // Bağlantıya gitmeden reddedilen girdiler (conn = null: muhafız önce çalışmalı).
    [Theory]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1; SELECT 2")]
    public async Task Explain_pg_multiple_statements_rejected(string sql)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Pg.ExplainAsync(null!, sql, actual: true, format: null));
    }

    [Fact]
    public async Task Explain_pg_unknown_format_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Pg.ExplainAsync(null!, "SELECT 1", actual: false, format: "yaml"));
    }
}
