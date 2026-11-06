using SqlCli.Core;

namespace SqlCli.Tests;

// Perf komutlarının dialect katmanı: sunucu GEREKTİRMEYEN kısım (SQL üretimi + girdi muhafızları).
// Gerçek PostgreSQL ile uçtan uca doğrulama README/HANDOFF'ta anlatılan elle testle yapıldı.
public class DialectPerfTests
{
    private static readonly PostgresDialect Pg = new();
    private static readonly SqlServerDialect Ms = new();

    [Fact]
    public void Her_iki_dialect_perf_destekler()
    {
        Assert.True(Pg.SupportsPerf);
        Assert.True(Ms.SupportsPerf);
    }

    [Fact]
    public void Kill_pg_iptal_ve_sonlandir()
    {
        Assert.Equal("SELECT pg_cancel_backend(42)", Pg.KillSql(42, hard: false));
        Assert.Equal("SELECT pg_terminate_backend(42)", Pg.KillSql(42, hard: true));
    }

    [Fact]
    public void Kill_sqlserver_hep_KILL()
    {
        Assert.Equal("KILL 42", Ms.KillSql(42, hard: false));
        Assert.Equal("KILL 42", Ms.KillSql(42, hard: true));
    }

    [Theory]
    [InlineData("avg", "s.mean_exec_time")]
    [InlineData("total", "s.total_exec_time")]
    [InlineData("reads", "s.shared_blks_hit + s.shared_blks_read")]
    [InlineData("executions", "s.calls")]
    public void SlowQueries_pg_siralama(string by, string beklenen)
    {
        var sql = Pg.SlowQueriesSql(7, by);
        Assert.Contains($"ORDER BY ", sql);
        Assert.Contains(beklenen, sql);
        Assert.Contains("LIMIT 7", sql);
        Assert.Contains("pg_stat_statements", sql);
    }

    [Fact]
    public void SlowQueries_on_kosul_yalniz_pg_de_var()
    {
        Assert.Contains("pg_extension", Pg.SlowQueriesPrecheckSql);
        Assert.Contains("pg_stat_statements", Pg.SlowQueriesPrecheckMessage);
        Assert.Null(Ms.SlowQueriesPrecheckSql);
    }

    [Fact]
    public void ActiveSessions_pg_pg_stat_activity_kullanir()
    {
        Assert.Contains("pg_stat_activity", Pg.ActiveSessionsSql);
        Assert.Contains("pg_backend_pid()", Pg.ActiveSessionsSql);   // kendi oturumunu listelemez
        Assert.Contains("sys.dm_exec_sessions", Ms.ActiveSessionsSql);
    }

    // Bağlantıya gitmeden reddedilen girdiler (conn = null: muhafız önce çalışmalı).
    [Theory]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1; SELECT 2")]
    public async Task Explain_pg_coklu_ifade_reddi(string sql)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Pg.ExplainAsync(null!, sql, actual: true, format: null));
    }

    [Fact]
    public async Task Explain_pg_bilinmeyen_format_reddi()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Pg.ExplainAsync(null!, "SELECT 1", actual: false, format: "yaml"));
    }

    // ---- Yan etki denetimi (PR #4 Codex bulgusu): rollback sequence/dblink/NOTIFY'yi geri almaz ----
    [Theory]
    [InlineData("SELECT nextval('s')", "nextval")]
    [InlineData("SELECT setval('s', 10)", "setval")]
    [InlineData("SELECT * FROM dblink('x','select 1') t(a int)", "dblink")]
    [InlineData("SELECT pg_notify('kanal','m')", "pg_notify")]
    [InlineData("SELECT pg_advisory_lock(1)", "pg_advisory_lock")]
    public void YanEtki_pg_belirtecleri_yakalanir(string sql, string beklenen)
        => Assert.Contains(beklenen, YanEtkiDenetimi.Bul(sql, DbProvider.Postgres));

    [Theory]
    [InlineData("SELECT NEXT VALUE FOR dbo.Sira", "next value for")]
    [InlineData("EXEC sp_who", "exec")]
    [InlineData("SELECT * FROM OPENQUERY(LS, 'select 1')", "openquery")]
    [InlineData("SELECT 1 WHERE 1 = (SELECT 1 FROM x) ; EXEC xp_cmdshell 'dir'", "xp_cmdshell")]
    public void YanEtki_sqlserver_belirtecleri_yakalanir(string sql, string beklenen)
        => Assert.Contains(beklenen, YanEtkiDenetimi.Bul(sql, DbProvider.SqlServer));

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 'nextval' AS metin")]                 // metin içi: yanlış alarm yok
    [InlineData("SELECT 1 -- nextval('s')")]                  // yorum içi
    [InlineData("SELECT nextval_kolonu FROM t")]              // ön ek: sözcük bütün eşleşir
    [InlineData("SELECT next FROM t WHERE value = 1")]        // NEXT tek başına kolon
    [InlineData("DELETE FROM t WHERE id = 1")]                // salt tablo yazması: rollback geri alır
    public void YanEtki_zararsiz_sorgu_yakalanmaz(string sql)
    {
        Assert.Empty(YanEtkiDenetimi.Bul(sql, DbProvider.Postgres));
        Assert.Empty(YanEtkiDenetimi.Bul(sql, DbProvider.SqlServer));
    }

    // Bağlantıya gitmeden: varsayılan RED, --allow-side-effects ile geçer (parite: iki dialect aynı).
    [Fact]
    public async Task Explain_analyze_yan_etkili_sorgu_varsayilan_reddedilir()
    {
        var pg = await Assert.ThrowsAsync<ArgumentException>(() => Pg.ExplainAsync(null!, "SELECT nextval('s')", actual: true, format: null));
        var ms = await Assert.ThrowsAsync<ArgumentException>(() => Ms.ExplainAsync(null!, "SELECT NEXT VALUE FOR s", actual: true, format: null));
        Assert.Contains("--allow-side-effects", pg.Message);
        Assert.Contains("--allow-side-effects", ms.Message);
    }

    [Fact]
    public async Task Explain_tahmini_plan_yan_etki_denetimine_girmez()
    {
        // actual=false sorguyu çalıştırmaz → guard devreye girmemeli (conn null olduğundan NullReference beklenir, ArgumentException DEĞİL).
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            try { await Pg.ExplainAsync(null!, "SELECT nextval('s')", actual: false, format: null); }
            catch (ArgumentException) { throw new Xunit.Sdk.XunitException("tahmini planda guard çalıştı"); }
        });
    }

    [Fact]
    public void YanEtki_izin_bayragi_reddi_kaldirir()
    {
        Assert.NotNull(YanEtkiDenetimi.Denetle("SELECT nextval('s')", DbProvider.Postgres, izinVer: false));
        Assert.Null(YanEtkiDenetimi.Denetle("SELECT nextval('s')", DbProvider.Postgres, izinVer: true));
        Assert.Null(YanEtkiDenetimi.Denetle("SELECT 1", DbProvider.Postgres, izinVer: false));
    }
}
