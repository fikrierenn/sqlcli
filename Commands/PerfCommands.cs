using System.Data.Common;
using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Performance / debugging: explain, active-sessions, slow-queries, kill.
// Sağlayıcıya özgü SQL ve akış ISqlDialect'te (SQL Server DMV, PostgreSQL pg_stat_*); burada sağlayıcı dalı yok.
public class PerfCommands
{
    [Command("explain", Description = "Query'nin execution plan'ını gösterir (SQL Server: XML; PostgreSQL: EXPLAIN text|json)")]
    public async Task Explain(
        [Argument(Description = "SQL sorgusu")] string sql,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Plan tipi: estimated|actual (actual sorguyu GERÇEKTEN çalıştırır)")] string mode = "estimated",
        [Option(Description = "--mode=actual kısayolu. PostgreSQL: EXPLAIN (ANALYZE, BUFFERS); değişiklikler geri alınır")] bool analyze = false,
        [Option(Description = "Plan biçimi. PostgreSQL: text|json|xml (varsayılan text); SQL Server: yalnız xml (varsayılan xml), diğerleri hata")] string? format = null)
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return;

        var actual = analyze || mode.Equals("actual", StringComparison.OrdinalIgnoreCase);

        // Salt-okuma muhafızı (SQLCLI_READONLY=1): gerçek çalıştırma yalnız okuma ifadesine izinli.
        if (actual)
        {
            var red = SqlGuard.Denetle(sql, false);
            if (red != null)
            {
                AnsiConsole.MarkupLine($"[red]HATA:[/] {Markup.Escape(red)} (explain actual/analyze ifadeyi çalıştırır)");
                throw new CommandExitedException(2);
            }
        }

        await using var c = await ConnectionResolver.OpenAsync(resolved.Effective);
        ExplainResult result;
        try
        {
            result = await resolved.Dialect.ExplainAsync(c, sql, actual, format);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]HATA:[/] {Markup.Escape(ex.Message)}");
            throw new CommandExitedException(2);
        }

        if (result.Note != null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.Note)}[/]");
        foreach (var plan in result.Plans)
        {
            AnsiConsole.MarkupLine($"\n[cyan bold]{Markup.Escape(result.Label)}[/]");
            Console.WriteLine(plan);
        }
    }

    [Command("active-sessions", Description = "Aktif oturumlar + blocking detection (SQL Server: sp_who2 alternatifi; PostgreSQL: pg_stat_activity)")]
    public Task ActiveSessions(
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option] string format = "table")
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return Task.CompletedTask;
        return RunQuery(resolved, format, resolved.Dialect.ActiveSessionsSql);
    }

    [Command("slow-queries", Description = "En yavaş query'ler (SQL Server: plan cache DMV; PostgreSQL: pg_stat_statements)")]
    public async Task SlowQueries(
        [Option(Description = "Kaç sorgu (default 20)")] int top = 20,
        [Option(Description = "Sıralama: avg|total|cpu|reads|executions")] string by = "avg",
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option] string format = "table")
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return;
        var dialect = resolved.Dialect;
        string slowSql;
        try { slowSql = dialect.SlowQueriesSql(top, by); }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]HATA:[/] {Markup.Escape(ex.Message)}");
            throw new CommandExitedException(2);
        }

        if (dialect.SlowQueriesPrecheckSql != null)
        {
            var hazir = await SqlExecutor.ScalarAsync(resolved.Effective, dialect.SlowQueriesPrecheckSql);
            if (hazir is not true)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(dialect.SlowQueriesPrecheckMessage)}[/]");
                throw new CommandExitedException(2);
            }
        }

        try
        {
            await RunQuery(resolved, format, slowSql);
        }
        catch (DbException ex) when (dialect.SlowQueriesErrorHint(ex) is { } ipucu)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ipucu)}[/]");
            throw new CommandExitedException(2);
        }
    }

    [Command("kill", Description = "Bir oturumu sonlandırır (SQL Server: KILL; PostgreSQL: pg_cancel_backend / --terminate) — onay isteyebilir")]
    public async Task Kill(
        [Argument(Description = "Session ID / PostgreSQL pid (active-sessions ile bul)")] int sessionId,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Onay sormadan çalıştır")] bool yes = false,
        [Option(Description = "PostgreSQL: oturumu tamamen sonlandır (pg_terminate_backend); verilmezse yalnız çalışan sorgu iptal edilir (pg_cancel_backend)")] bool terminate = false)
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return;

        var pg = resolved.Dialect.Provider == DbProvider.Postgres;
        var eylem = pg && !terminate ? "sorgusu iptal edilsin" : "sonlandırılsın";
        // SQL Server'da yalnız sorgu iptali yok; KILL oturumu sonlandırır (--terminate ile aynı). Sessiz bırakma.
        if (!pg && !terminate)
            AnsiConsole.MarkupLine("[yellow]Not: SQL Server'da sorgu iptali yok; KILL oturumun kendisini sonlandırır.[/]");
        if (!yes)
        {
            var ok = AnsiConsole.Confirm($"Session [yellow]{sessionId}[/] {eylem} mi?", false);
            if (!ok) { AnsiConsole.MarkupLine("[grey]İptal.[/]"); return; }
        }
        await using var c = await ConnectionResolver.OpenAsync(resolved.Effective);
        using var cmd = c.CreateCommand();
        cmd.CommandText = resolved.Dialect.KillSql(sessionId, terminate);
        object? sonuc;
        try { sonuc = await cmd.ExecuteScalarAsync(); }   // PG: bool; SQL Server KILL: sonuç kümesi yok (null)
        catch (DbException ex)
        {
            // SQL Server'da olmayan/yetkisiz session hata fırlatır; PG'de false döner → ikisi de aynı HATA + exit 2.
            AnsiConsole.MarkupLine($"[red]HATA:[/] Session {sessionId} için işlem uygulanamadı: {Markup.Escape(ex.Message)}");
            throw new CommandExitedException(2);
        }
        if (sonuc is false)
        {
            AnsiConsole.MarkupLine($"[red]HATA:[/] Session {sessionId} için işlem uygulanamadı (pid yok ya da yetki yetersiz).");
            throw new CommandExitedException(2);
        }
        var fiil = pg && !terminate ? "sorgusu iptal edildi" : "sonlandırıldı";
        AnsiConsole.MarkupLine($"[green]✓[/] Session {sessionId} {fiil}.");
    }

    private static async Task RunQuery(ConnectionResolver.ResolvedConnection resolved, string format, string sql)
    {
        var dt = await SqlExecutor.RunQueryAsync(resolved.Effective, sql);
        OutputFormatter.Render(dt, OutputFormatter.Parse(format));
    }

    private static ConnectionResolver.ResolvedConnection Resolve(string? conn, string? profile)
    {
        var r = ConnectionResolver.Resolve(conn, profile);
        if (r == null)
        {
            AnsiConsole.MarkupLine("[red]HATA: Bağlantı bilgisi bulunamadı.[/]");
            throw new CommandExitedException(1);
        }
        return r;
    }

    // Dialect perf desteklemiyorsa (ör. ileride Oracle) net mesajla çık.
    private static bool EnsurePerf(ConnectionResolver.ResolvedConnection r)
    {
        if (r.Dialect.SupportsPerf) return true;
        AnsiConsole.MarkupLine($"[yellow]Bu komut {r.Dialect.Name} için desteklenmiyor.[/]");
        return false;
    }
}
