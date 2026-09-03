using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Performance / debugging: explain, active-sessions, slow-queries
public class PerfCommands
{
    [Command("explain", Description = "Query'nin estimated execution plan'ını XML olarak gösterir")]
    public async Task Explain(
        [Argument(Description = "SQL sorgusu")] string sql,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Plan tipi: estimated|actual")] string mode = "estimated")
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return;

        var setStmt = mode.ToLowerInvariant() == "actual"
            ? "SET STATISTICS XML ON;"
            : "SET SHOWPLAN_XML ON;";
        var unsetStmt = mode.ToLowerInvariant() == "actual"
            ? "SET STATISTICS XML OFF;"
            : "SET SHOWPLAN_XML OFF;";

        await using var c = await ConnectionResolver.OpenAsync(resolved.ConnectionString);
        try
        {
            using var setCmd = c.CreateCommand();
            setCmd.CommandText = setStmt;
            await setCmd.ExecuteNonQueryAsync();

            using var qCmd = c.CreateCommand();
            qCmd.CommandText = sql;
            await using var reader = await qCmd.ExecuteReaderAsync();

            // Plan XML çoğunlukla son result set'te
            do
            {
                while (await reader.ReadAsync())
                {
                    var val = reader[0]?.ToString();
                    if (!string.IsNullOrEmpty(val) && val.Contains("ShowPlanXML", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine("\n[cyan bold]Execution Plan (XML)[/]");
                        Console.WriteLine(val);
                    }
                }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            using var unsetCmd = c.CreateCommand();
            unsetCmd.CommandText = unsetStmt;
            await unsetCmd.ExecuteNonQueryAsync();
        }
    }

    [Command("active-sessions", Description = "Aktif oturumlar + blocking detection (sp_who2 alternatifi) — SQL Server")]
    public Task ActiveSessions(
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option] string format = "table")
    {
        if (!EnsurePerf(Resolve(conn, profile))) return Task.CompletedTask;
        return RunQuery(conn, profile, format, @"
            SELECT
                s.session_id AS SessionId,
                s.login_name AS Kullanici,
                s.host_name AS Host,
                s.program_name AS Program,
                s.status AS Durum,
                r.command AS Komut,
                r.wait_type AS WaitType,
                r.blocking_session_id AS BlokeEden,
                r.cpu_time AS CpuMs,
                r.total_elapsed_time AS GecenMs,
                DB_NAME(r.database_id) AS Veritabani,
                SUBSTRING(t.text, COALESCE(r.statement_start_offset, 0)/2 + 1,
                    (CASE WHEN r.statement_end_offset = -1 THEN LEN(CONVERT(NVARCHAR(MAX), t.text)) * 2
                          ELSE r.statement_end_offset END - COALESCE(r.statement_start_offset, 0))/2 + 1) AS Sorgu
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
            ORDER BY r.blocking_session_id DESC, r.cpu_time DESC");
    }

    [Command("slow-queries", Description = "Plan cache'deki en yavaş query'ler (DMV) — SQL Server")]
    public Task SlowQueries(
        [Option(Description = "Kaç sorgu (default 20)")] int top = 20,
        [Option(Description = "Sıralama: avg|total|cpu|reads|executions")] string by = "avg",
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option] string format = "table")
    {
        if (!EnsurePerf(Resolve(conn, profile))) return Task.CompletedTask;
        var orderBy = by.ToLowerInvariant() switch
        {
            "total" => "qs.total_elapsed_time",
            "cpu" => "qs.total_worker_time",
            "reads" => "qs.total_logical_reads",
            "executions" => "qs.execution_count",
            _ => "qs.total_elapsed_time / qs.execution_count"
        };

        var sql = $@"
            SELECT TOP {top}
                qs.execution_count AS Calistirma,
                qs.total_elapsed_time / 1000 AS ToplamMs,
                (qs.total_elapsed_time / qs.execution_count) / 1000 AS OrtalamaMs,
                qs.total_worker_time / 1000 AS CpuMs,
                qs.total_logical_reads AS MantiksalOkuma,
                qs.last_execution_time AS SonCalistirma,
                SUBSTRING(qt.text, qs.statement_start_offset/2 + 1,
                    (CASE WHEN qs.statement_end_offset = -1
                          THEN LEN(CONVERT(NVARCHAR(MAX), qt.text)) * 2
                          ELSE qs.statement_end_offset END - qs.statement_start_offset)/2 + 1) AS Sorgu
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
            WHERE qt.dbid = DB_ID()
            ORDER BY {orderBy} DESC";

        return RunQuery(conn, profile, format, sql);
    }

    [Command("kill", Description = "Bir oturumu sonlandırır (KILL <session_id>) — onay isteyebilir")]
    public async Task Kill(
        [Argument(Description = "Session ID (active-sessions ile bul)")] int sessionId,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Onay sormadan çalıştır")] bool yes = false)
    {
        var resolved = Resolve(conn, profile);
        if (!EnsurePerf(resolved)) return;
        if (!yes)
        {
            var ok = AnsiConsole.Confirm($"Session [yellow]{sessionId}[/] sonlandırılsın mı?", false);
            if (!ok) { AnsiConsole.MarkupLine("[grey]İptal.[/]"); return; }
        }
        await using var c = await ConnectionResolver.OpenAsync(resolved.ConnectionString);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"KILL {sessionId}";
        await cmd.ExecuteNonQueryAsync();
        AnsiConsole.MarkupLine($"[green]✓[/] Session {sessionId} sonlandırıldı.");
    }

    private static async Task RunQuery(string? conn, string? profile, string format, string sql)
    {
        var resolved = Resolve(conn, profile);
        var dt = await SqlExecutor.RunQueryAsync(resolved.ConnectionString, sql);
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

    // Perf/DMV komutları şimdilik yalnız SQL Server. Postgres'te net mesajla çık.
    private static bool EnsurePerf(ConnectionResolver.ResolvedConnection r)
    {
        if (r.Dialect.SupportsPerf) return true;
        AnsiConsole.MarkupLine($"[yellow]Bu komut {r.Dialect.Name} için desteklenmiyor (şimdilik yalnız SQL Server DMV).[/]");
        return false;
    }
}
