using System.Data;
using SqlCli.Core;

namespace SqlCli.Tests;

/// <summary>
/// Perf komutlarının İKİ sağlayıcıda aynı şekil/davranışı: gerçek SQL Server + gerçek PostgreSQL.
///
/// SQL Server: SQLCLI_TEST_SQL (yoksa yerel `localhost`, Windows kimliği) — sqlcli_test_kaynak DB'si.
/// PostgreSQL: SQLCLI_TEST_PG (yoksa `Host=localhost;Username=postgres;Database=postgres`) — pg_stat_statements
///   KURULU olmalı (shared_preload_libraries + CREATE EXTENSION). Test tabloları kendi kurulup silinir.
/// Sunucuya ulaşılamazsa testler KIRMIZI olur, atlanmaz (CopyTests ile aynı kural).
/// </summary>
public class ProviderParityTests : IClassFixture<SqlFixture>
{
    private readonly string _ms;
    private static readonly string Pg = Environment.GetEnvironmentVariable("SQLCLI_TEST_PG")
        ?? "Host=localhost;Username=postgres;Database=postgres";

    public ProviderParityTests(SqlFixture f) => _ms = f.Kaynak;

    private static async Task<DataTable> Tablo(string cs, string sql)
    {
        await using var c = await ConnectionResolver.OpenAsync(cs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var dt = new DataTable();
        dt.Load(r);
        return dt;
    }

    private static string[] Kolonlar(DataTable dt) => dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();

    private static async Task Exec(string cs, string sql)
    {
        await using var c = await ConnectionResolver.OpenAsync(cs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Say(string cs, string sql)
        => Convert.ToInt64((await Tablo(cs, sql)).Rows[0][0]);

    [Fact]
    public async Task ActiveSessions_ayni_kolonlar()
    {
        var ms = Kolonlar(await Tablo(_ms, new SqlServerDialect().ActiveSessionsSql));
        var pg = Kolonlar(await Tablo(Pg, new PostgresDialect().ActiveSessionsSql));
        Assert.Equal(ms, pg);
    }

    [Fact]
    public async Task SlowQueries_ayni_kolonlar()
    {
        var ms = Kolonlar(await Tablo(_ms, new SqlServerDialect().SlowQueriesSql(5, "avg")));
        var pg = Kolonlar(await Tablo(Pg, new PostgresDialect().SlowQueriesSql(5, "avg")));
        Assert.Equal(ms, pg);
    }

    [Theory]
    [InlineData("avg")] [InlineData("total")] [InlineData("reads")] [InlineData("executions")]
    public async Task SlowQueries_ortak_siralamalar_iki_tarafta_kosar(string by)
    {
        await Tablo(_ms, new SqlServerDialect().SlowQueriesSql(3, by));
        await Tablo(Pg, new PostgresDialect().SlowQueriesSql(3, by));
    }

    [Fact]
    public void SlowQueries_gecersiz_by_iki_tarafta_hata_cpu_yalniz_pgde_hata()
    {
        Assert.Throws<ArgumentException>(() => new SqlServerDialect().SlowQueriesSql(3, "bogus"));
        Assert.Throws<ArgumentException>(() => new PostgresDialect().SlowQueriesSql(3, "bogus"));
        Assert.Throws<ArgumentException>(() => new PostgresDialect().SlowQueriesSql(3, "cpu"));   // PG'de CPU yok: sessiz eşleme yok
        Assert.Contains("total_worker_time", new SqlServerDialect().SlowQueriesSql(3, "cpu"));
    }

    // SqlFixture.Kaynak DB'sinde / PG test DB'sinde: explain actual DELETE → satırlar GERİ ALINIR.
    [Theory]
    [InlineData(false)]   // SQL Server
    [InlineData(true)]    // PostgreSQL
    public async Task Explain_actual_yazmayi_geri_alir(bool pg)
    {
        var cs = pg ? Pg : _ms;
        ISqlDialect d = pg ? new PostgresDialect() : new SqlServerDialect();
        var t = pg ? "public.pt_explain" : "dbo.pt_explain";
        await Exec(cs, pg ? $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t}(id int); INSERT INTO {t} VALUES (1),(2),(3);"
                          : $"DROP TABLE IF EXISTS {t}; CREATE TABLE {t}(id int); INSERT {t} VALUES (1),(2),(3);");
        try
        {
            await using var c = await ConnectionResolver.OpenAsync(cs);
            var r = await d.ExplainAsync(c, $"DELETE FROM {t}", actual: true, format: null);
            Assert.NotEmpty(r.Plans);
            Assert.All(r.Plans, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            Assert.Equal(3, await Say(cs, $"SELECT COUNT(*) FROM {t}"));   // silinmedi
        }
        finally { await Exec(cs, $"DROP TABLE IF EXISTS {t}"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explain_tahmini_plan_doner_ve_calistirmaz(bool pg)
    {
        var cs = pg ? Pg : _ms;
        ISqlDialect d = pg ? new PostgresDialect() : new SqlServerDialect();
        await using var c = await ConnectionResolver.OpenAsync(cs);
        var r = await d.ExplainAsync(c, "SELECT 1 AS x", actual: false, format: null);
        Assert.NotEmpty(r.Plans);
        Assert.Contains("tahmini", r.Label);
    }

    [Fact]
    public async Task Explain_format_destegi_sessiz_yok_sayilmaz()
    {
        await using var ms = await ConnectionResolver.OpenAsync(_ms);
        await using var pg = await ConnectionResolver.OpenAsync(Pg);
        // SQL Server yalnız XML: json hata verir, xml kabul.
        await Assert.ThrowsAsync<ArgumentException>(() => new SqlServerDialect().ExplainAsync(ms, "SELECT 1", false, "json"));
        Assert.NotEmpty((await new SqlServerDialect().ExplainAsync(ms, "SELECT 1", false, "xml")).Plans);
        // PG hepsini destekler.
        foreach (var f in new[] { "text", "json", "xml" })
            Assert.NotEmpty((await new PostgresDialect().ExplainAsync(pg, "SELECT 1", false, f)).Plans);
    }

    [Fact]
    public async Task Kill_pg_olmayan_pid_false_doner()
    {
        var sonuc = (await Tablo(Pg, new PostgresDialect().KillSql(2147483000, hard: false))).Rows[0][0];
        Assert.Equal(false, sonuc);
    }

    // ---- PR #4 Codex bulgusu: aynı DB'de sorgu hedef tabloyu okuyorsa TRUNCATE kaynak kilidinde takılıyordu ----
    [Fact]
    public async Task PgCopy_ayni_tablodan_kendine_truncate_hizli_reddedilir()
    {
        await Exec(Pg, "DROP TABLE IF EXISTS public.pt_self; CREATE TABLE public.pt_self(id int); INSERT INTO public.pt_self VALUES (1),(2);");
        try
        {
            var sure = System.Diagnostics.Stopwatch.StartNew();
            // timeout 0 = sınırsız: eski kod burada sonsuza dek beklerdi → test süre aşımıyla kırmızı olurdu.
            var gorev = PostgresCopy.RunAsync(Pg, Pg, "SELECT id FROM public.pt_self", "public.pt_self", truncate: true, timeoutSeconds: 0, saltOkuma: true);
            var bitti = await Task.WhenAny(gorev, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(gorev, bitti);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gorev);
            Assert.Contains("kendi", ex.Message);
            Assert.Equal(2, await Say(Pg, "SELECT COUNT(*) FROM public.pt_self"));   // hedef bozulmadı
        }
        finally { await Exec(Pg, "DROP TABLE IF EXISTS public.pt_self"); }
    }

    [Fact]
    public async Task PgCopy_ayni_db_farkli_tablo_truncate_calisir()
    {
        await Exec(Pg, "DROP TABLE IF EXISTS public.pt_k; DROP TABLE IF EXISTS public.pt_h; CREATE TABLE public.pt_k(id int); CREATE TABLE public.pt_h(id int); INSERT INTO public.pt_k VALUES (1),(2),(3); INSERT INTO public.pt_h VALUES (9);");
        try
        {
            var s = await PostgresCopy.RunAsync(Pg, Pg, "SELECT id FROM public.pt_k", "public.pt_h", truncate: true, timeoutSeconds: 30, saltOkuma: true);
            Assert.Equal(3, s.Satir);
            Assert.Equal(3, await Say(Pg, "SELECT COUNT(*) FROM public.pt_h"));
        }
        finally { await Exec(Pg, "DROP TABLE IF EXISTS public.pt_k; DROP TABLE IF EXISTS public.pt_h"); }
    }
}
