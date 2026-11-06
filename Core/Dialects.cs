using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlCli.Core;

// Çok-sağlayıcı dialect soyutlaması.
// SqlServerDialect ORİJİNAL SQL'i birebir döndürür → SQL Server davranışı değişmez (regresyonsuz).
// PostgresDialect standart information_schema/pg_catalog sorguları kullanır.
// Oracle: yeni bir ISqlDialect impl'i ekle (katalog=ALL_TABLES/USER_TAB_COLUMNS, sayfalama=FETCH FIRST) — seam hazır.
public enum DbProvider { SqlServer, Postgres }

public sealed record ExplainResult(string Label, IReadOnlyList<string> Plans, string? Note = null);

public interface ISqlDialect
{
    DbProvider Provider { get; }
    string Name { get; }
    bool SupportsPerf { get; }   // explain/active-sessions/slow-queries/kill (copy ayrı: yalnız SQL Server)

    DbConnection Create(string connStr);
    Task InitAsync(DbConnection conn);
    IEnumerable<string> SplitBatches(string script);
    bool IsIdempotentWarn(DbException ex);
    string ErrorCode(DbException ex);

    string WrapLimit(string select, int n);            // query --max-rows
    string TopSql(string table, int n);                // top komutu (table ham girdi)
    (string Schema, string Name) SplitTable(string table);

    // Introspection SQL
    string StatusSql { get; }
    string ConnInfoSql { get; }
    string TablesSql { get; }
    string RowsSql { get; }
    string EmptyTablesSql { get; }
    string FkSql { get; }
    string IndexesSql { get; }
    string SearchTablesSql(string like);
    string SearchColumnsSql(string like);
    string DescribeColumnsSql(string schema, string name);
    string DescribeIndexesSql(string schema, string name);
    string DescribeFksSql(string schema, string name);
    string RowCountSql(string schema, string name);
    string RelOutgoingSql(string schema, string name);
    string RelIncomingSql(string schema, string name);

    // Perf / tanılama
    string ActiveSessionsSql { get; }
    string SlowQueriesSql(int top, string by);
    /// <summary>slow-queries'in ön koşulunu denetleyen skaler sorgu (true = hazır); gerekmiyorsa null.</summary>
    string? SlowQueriesPrecheckSql { get; }
    string SlowQueriesPrecheckMessage { get; }
    /// <summary>slow-queries çalışırken gelen hata için kullanıcıya yardımcı mesaj; tanınmıyorsa null.</summary>
    string? SlowQueriesErrorHint(DbException ex);
    /// <param name="hard">true: oturumu sonlandır (PG: pg_terminate_backend); false: yalnız çalışan sorguyu iptal et (PG: pg_cancel_backend). SQL Server her zaman KILL.</param>
    string KillSql(int sessionId, bool hard);
    /// <summary>Plan üretir. actual=true sorguyu GERÇEKTEN çalıştırır (PG: ANALYZE; tablo değişiklikleri geri alınır).
    /// Rollback sequence/dblink/NOTIFY gibi yan etkileri geri ALMAZ: bu belirteçler varsa <paramref name="allowSideEffects"/> verilmedikçe ArgumentException.</summary>
    Task<ExplainResult> ExplainAsync(DbConnection conn, string sql, bool actual, string? format, bool allowSideEffects = false);

    static ISqlDialect Detect(string connStr)
    {
        // Npgsql anahtarı "Host=" veya postgres URI → Postgres; aksi SQL Server.
        if (Regex.IsMatch(connStr, @"(^|;)\s*Host\s*=", RegexOptions.IgnoreCase)
            || connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return new PostgresDialect();
        return new SqlServerDialect();
    }
}

// ─────────────────────────────────────────────────────────────────────────
public sealed class SqlServerDialect : ISqlDialect
{
    public DbProvider Provider => DbProvider.SqlServer;
    public string Name => "SQL Server";
    public bool SupportsPerf => true;

    private static readonly HashSet<int> WarnOnly = new() { 2714, 1913, 2627, 2601, 1505, 2705 };

    public DbConnection Create(string cs) => new SqlConnection(cs);

    public async Task InitAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;";
        await cmd.ExecuteNonQueryAsync();
    }

    public IEnumerable<string> SplitBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public bool IsIdempotentWarn(DbException ex) => ex is SqlException se && WarnOnly.Contains(se.Number);
    public string ErrorCode(DbException ex) => ex is SqlException se ? se.Number.ToString() : "?";

    public string WrapLimit(string sql, int n)
    {
        var t = sql.TrimStart();
        if (!t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return sql;
        if (t.Contains("TOP ", StringComparison.OrdinalIgnoreCase)) return sql;
        if (t.StartsWith("SELECT @@", StringComparison.OrdinalIgnoreCase)) return sql;
        if (t.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase)) return sql;
        var idx = t.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        return t[..(idx + 6)] + $" TOP {n}" + t[(idx + 6)..];
    }

    public string TopSql(string table, int n) => $"SELECT TOP {n} * FROM {Qualify(table)}";

    public (string, string) SplitTable(string table)
    {
        var clean = table.Replace("[", "").Replace("]", "");
        var parts = clean.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private static string Qualify(string table)
    {
        if (table.Contains('[')) return table;
        if (table.Contains('.'))
            return string.Join(".", table.Split('.').Select(p => $"[{p}]"));
        return $"dbo.[{table}]";
    }

    public string StatusSql =>
        "SELECT @@SERVERNAME AS Sunucu, DB_NAME() AS VeritabaniAdi, SUSER_NAME() AS Kullanici, @@VERSION AS Versiyon";

    public string ConnInfoSql =>
        "SELECT @@SERVERNAME AS Sunucu, DB_NAME() AS Veritabani, SUSER_NAME() AS Kullanici";

    public string TablesSql =>
        "SELECT TABLE_SCHEMA AS Sema, TABLE_NAME AS Tablo FROM INFORMATION_SCHEMA.TABLES " +
        "WHERE TABLE_TYPE='BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public string RowsSql => @"
        SELECT t.TABLE_SCHEMA AS Sema, t.TABLE_NAME AS Tablo, p.rows AS SatirSayisi
        FROM INFORMATION_SCHEMA.TABLES t
        JOIN sys.partitions p ON p.object_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME))
        WHERE t.TABLE_TYPE = 'BASE TABLE' AND p.index_id IN (0, 1)
        ORDER BY p.rows DESC, t.TABLE_NAME";

    public string EmptyTablesSql => @"
        SELECT s.name AS Sema, t.name AS Tablo
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        GROUP BY s.name, t.name
        HAVING SUM(p.rows) = 0
        ORDER BY s.name, t.name";

    public string FkSql => @"
        SELECT fk.name AS FK_Adi, tp.name AS AnaTablo, cp.name AS AnaKolon,
               tr.name AS RefTablo, cr.name AS RefKolon, fk.is_disabled AS DevreDisi
        FROM sys.foreign_keys fk
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
        ORDER BY tp.name, fk.name";

    public string IndexesSql => @"
        SELECT t.name AS Tablo, i.name AS IndexAdi, i.type_desc AS Tip,
               i.is_unique AS UniqueMi, i.is_primary_key AS PKmi, i.is_disabled AS DevreDisi
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        WHERE i.name IS NOT NULL
        ORDER BY t.name, i.name";

    public string SearchTablesSql(string like) => $@"
        SELECT TABLE_SCHEMA AS Sema, TABLE_NAME AS Tablo
        FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME LIKE '{Esc(like)}'
        ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public string SearchColumnsSql(string like) => $@"
        SELECT TABLE_SCHEMA AS Sema, TABLE_NAME AS Tablo, COLUMN_NAME AS Kolon, DATA_TYPE AS Tip
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE COLUMN_NAME LIKE '{Esc(like)}'
        ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

    public string DescribeColumnsSql(string schema, string name) => $@"
        SELECT
            c.COLUMN_NAME AS Kolon,
            CASE
                WHEN c.DATA_TYPE IN ('nvarchar','varchar','char','nchar','varbinary','binary') THEN
                    c.DATA_TYPE + '(' + CASE WHEN c.CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX' ELSE CAST(c.CHARACTER_MAXIMUM_LENGTH AS VARCHAR(10)) END + ')'
                WHEN c.DATA_TYPE IN ('decimal','numeric') THEN
                    c.DATA_TYPE + '(' + CAST(c.NUMERIC_PRECISION AS VARCHAR(10)) + ',' + CAST(c.NUMERIC_SCALE AS VARCHAR(10)) + ')'
                ELSE c.DATA_TYPE
            END AS Tip,
            c.IS_NULLABLE AS Nullable,
            c.COLUMN_DEFAULT AS Default_,
            CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN '✓' ELSE '' END AS PK
        FROM INFORMATION_SCHEMA.COLUMNS c
        LEFT JOIN (
            SELECT kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA = '{Esc(schema)}' AND tc.TABLE_NAME = '{Esc(name)}'
        ) pk ON pk.COLUMN_NAME = c.COLUMN_NAME
        WHERE c.TABLE_SCHEMA = '{Esc(schema)}' AND c.TABLE_NAME = '{Esc(name)}'
        ORDER BY c.ORDINAL_POSITION";

    public string DescribeIndexesSql(string schema, string name) => $@"
        SELECT i.name AS IndexAdi, i.type_desc AS Tip,
               i.is_unique AS UniqueMi, i.is_primary_key AS PKmi,
               STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Kolonlar
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID('{Esc(schema)}.{Esc(name)}') AND i.name IS NOT NULL
        GROUP BY i.name, i.type_desc, i.is_unique, i.is_primary_key
        ORDER BY i.name";

    public string DescribeFksSql(string schema, string name) => $@"
        SELECT fk.name AS FK_Adi, cp.name AS Kolon, tr.name AS RefTablo, cr.name AS RefKolon
        FROM sys.foreign_keys fk
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID('{Esc(schema)}.{Esc(name)}')
        ORDER BY fk.name";

    public string RowCountSql(string schema, string name) => $@"
        SELECT SUM(p.rows) AS SatirSayisi
        FROM sys.partitions p
        WHERE p.object_id = OBJECT_ID('{Esc(schema)}.{Esc(name)}') AND p.index_id IN (0, 1)";

    public string RelOutgoingSql(string schema, string name) => $@"
        SELECT fk.name AS FK_Adi, cp.name AS BizimKolon,
               SCHEMA_NAME(tr.schema_id) + '.' + tr.name AS RefTablo, cr.name AS RefKolon,
               fk.delete_referential_action_desc AS OnDelete
        FROM sys.foreign_keys fk
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID('{Esc(schema)}.{Esc(name)}')
        ORDER BY fk.name";

    public string RelIncomingSql(string schema, string name) => $@"
        SELECT fk.name AS FK_Adi,
               SCHEMA_NAME(tp.schema_id) + '.' + tp.name AS KaynakTablo, cp.name AS KaynakKolon,
               cr.name AS BizimKolon,
               fk.delete_referential_action_desc AS OnDelete
        FROM sys.foreign_keys fk
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
        WHERE fk.referenced_object_id = OBJECT_ID('{Esc(schema)}.{Esc(name)}')
        ORDER BY tp.name, fk.name";

    public string ActiveSessionsSql => @"
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
        ORDER BY r.blocking_session_id DESC, r.cpu_time DESC";

    public string SlowQueriesSql(int top, string by)
    {
        var orderBy = by.ToLowerInvariant() switch
        {
            "total" => "qs.total_elapsed_time",
            "cpu" => "qs.total_worker_time",
            "reads" => "qs.total_logical_reads",
            "executions" => "qs.execution_count",
            "avg" => "qs.total_elapsed_time / qs.execution_count",
            _ => throw new ArgumentException($"--by yalnız avg|total|cpu|reads|executions olabilir: '{by}'")
        };
        return $@"
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
    }

    public string? SlowQueriesPrecheckSql => null;
    public string SlowQueriesPrecheckMessage => "";
    public string? SlowQueriesErrorHint(DbException ex) => null;

    public string KillSql(int sessionId, bool hard) => $"KILL {sessionId}";

    public async Task<ExplainResult> ExplainAsync(DbConnection c, string sql, bool actual, string? format, bool allowSideEffects = false)
    {
        // Paritesi: PG'nin text/json planı SQL Server'da yok → sessizce yok sayma, net hata.
        if (!string.IsNullOrEmpty(format) && !format.Equals("xml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"SQL Server planı yalnız XML üretir; --format '{format}' desteklenmez (xml ya da hiç verme).");

        if (actual)
        {
            var red = YanEtkiDenetimi.Denetle(sql, DbProvider.SqlServer, allowSideEffects);
            if (red != null) throw new ArgumentException(red);
        }

        var setStmt = actual ? "SET STATISTICS XML ON;" : "SET SHOWPLAN_XML ON;";
        var unsetStmt = actual ? "SET STATISTICS XML OFF;" : "SET SHOWPLAN_XML OFF;";
        var plans = new List<string>();

        // actual: sorgu GERÇEKTEN çalışır → PG'deki gibi transaction içinde, sonunda ROLLBACK
        // (INSERT/UPDATE/DELETE plan alındıktan sonra geri alınır). Tahmini planda çalıştırma yok.
        DbTransaction? tx = actual ? await c.BeginTransactionAsync() : null;
        try
        {
            using var setCmd = c.CreateCommand();
            setCmd.Transaction = tx;
            setCmd.CommandText = setStmt;
            await setCmd.ExecuteNonQueryAsync();

            using var qCmd = c.CreateCommand();
            qCmd.Transaction = tx;
            qCmd.CommandText = sql;
            await using var reader = await qCmd.ExecuteReaderAsync();

            // Plan XML çoğunlukla son result set'te
            do
            {
                while (await reader.ReadAsync())
                {
                    var val = reader[0]?.ToString();
                    if (!string.IsNullOrEmpty(val) && val.Contains("ShowPlanXML", StringComparison.OrdinalIgnoreCase))
                        plans.Add(val);
                }
            } while (await reader.NextResultAsync());
        }
        finally
        {
            using var unsetCmd = c.CreateCommand();
            unsetCmd.Transaction = tx;
            unsetCmd.CommandText = unsetStmt;
            await unsetCmd.ExecuteNonQueryAsync();
            if (tx != null) { await tx.RollbackAsync(); await tx.DisposeAsync(); }
        }
        var label = actual ? "Execution Plan (XML, actual — tablo değişiklikleri geri alındı)" : "Execution Plan (XML, tahmini)";
        return new ExplainResult(label, plans);
    }

    private static string Esc(string s) => s.Replace("'", "''");
}

// ─────────────────────────────────────────────────────────────────────────
public sealed class PostgresDialect : ISqlDialect
{
    public DbProvider Provider => DbProvider.Postgres;
    public string Name => "PostgreSQL";
    public bool SupportsPerf => true;

    // İdempotent SQLSTATE'ler: duplicate_table/object/column/schema/function, unique_violation.
    private static readonly HashSet<string> WarnStates = new()
        { "42P07", "42710", "42701", "42P06", "42723", "23505", "42P16" };

    public DbConnection Create(string cs) => new NpgsqlConnection(cs);
    public Task InitAsync(DbConnection conn) => Task.CompletedTask;

    public IEnumerable<string> SplitBatches(string script) => new[] { script }; // Npgsql çoklu-statement destekler

    public bool IsIdempotentWarn(DbException ex) => ex is PostgresException pe && WarnStates.Contains(pe.SqlState);
    public string ErrorCode(DbException ex) => ex is PostgresException pe ? pe.SqlState : "?";

    public string WrapLimit(string sql, int n)
    {
        var t = sql.TrimStart().TrimEnd(';', ' ', '\r', '\n', '\t');
        if (!t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return sql;
        if (Regex.IsMatch(t, @"\bLIMIT\b", RegexOptions.IgnoreCase)) return sql;
        return t + $" LIMIT {n}";
    }

    public string TopSql(string table, int n) => $"SELECT * FROM {Qualify(table)} LIMIT {n}";

    public (string, string) SplitTable(string table)
    {
        var clean = table.Replace("\"", "");
        var parts = clean.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("public", parts[0]);
    }

    private static string Qualify(string table) => table.Contains('.') ? table : $"public.{table}";

    public string StatusSql =>
        "SELECT COALESCE(inet_server_addr()::text, 'local') AS \"Sunucu\", current_database() AS \"VeritabaniAdi\", current_user AS \"Kullanici\", version() AS \"Versiyon\"";

    public string ConnInfoSql =>
        "SELECT COALESCE(inet_server_addr()::text, 'local') AS \"Sunucu\", current_database() AS \"Veritabani\", current_user AS \"Kullanici\"";

    public string TablesSql =>
        "SELECT table_schema AS \"Sema\", table_name AS \"Tablo\" FROM information_schema.tables " +
        "WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') " +
        "ORDER BY table_schema, table_name";

    public string RowsSql =>
        "SELECT schemaname AS \"Sema\", relname AS \"Tablo\", n_live_tup AS \"SatirSayisi\" " +
        "FROM pg_stat_user_tables ORDER BY n_live_tup DESC, relname";

    public string EmptyTablesSql =>
        "SELECT schemaname AS \"Sema\", relname AS \"Tablo\" FROM pg_stat_user_tables WHERE n_live_tup = 0 ORDER BY schemaname, relname";

    public string FkSql => @"
        SELECT tc.constraint_name AS ""FK_Adi"", tc.table_name AS ""AnaTablo"", kcu.column_name AS ""AnaKolon"",
               ccu.table_name AS ""RefTablo"", ccu.column_name AS ""RefKolon"", false AS ""DevreDisi""
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema
        WHERE tc.constraint_type = 'FOREIGN KEY'
        ORDER BY tc.table_name, tc.constraint_name";

    public string IndexesSql =>
        "SELECT tablename AS \"Tablo\", indexname AS \"IndexAdi\", indexdef AS \"Tanim\" " +
        "FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog') ORDER BY tablename, indexname";

    public string SearchTablesSql(string like) => $@"
        SELECT table_schema AS ""Sema"", table_name AS ""Tablo""
        FROM information_schema.tables
        WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema')
          AND table_name ILIKE '{Esc(like)}'
        ORDER BY table_schema, table_name";

    public string SearchColumnsSql(string like) => $@"
        SELECT table_schema AS ""Sema"", table_name AS ""Tablo"", column_name AS ""Kolon"", data_type AS ""Tip""
        FROM information_schema.columns
        WHERE column_name ILIKE '{Esc(like)}' AND table_schema NOT IN ('pg_catalog','information_schema')
        ORDER BY table_schema, table_name, ordinal_position";

    public string DescribeColumnsSql(string schema, string name) => $@"
        SELECT c.column_name AS ""Kolon"",
               c.data_type || COALESCE('(' || c.character_maximum_length || ')', '') AS ""Tip"",
               c.is_nullable AS ""Nullable"",
               c.column_default AS ""Default_"",
               CASE WHEN pk.column_name IS NOT NULL THEN '✓' ELSE '' END AS ""PK""
        FROM information_schema.columns c
        LEFT JOIN (
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema = '{Esc(schema)}' AND tc.table_name = '{Esc(name)}'
        ) pk ON pk.column_name = c.column_name
        WHERE c.table_schema = '{Esc(schema)}' AND c.table_name = '{Esc(name)}'
        ORDER BY c.ordinal_position";

    public string DescribeIndexesSql(string schema, string name) =>
        $"SELECT indexname AS \"IndexAdi\", indexdef AS \"Tanim\" FROM pg_indexes WHERE schemaname='{Esc(schema)}' AND tablename='{Esc(name)}' ORDER BY indexname";

    public string DescribeFksSql(string schema, string name) => $@"
        SELECT tc.constraint_name AS ""FK_Adi"", kcu.column_name AS ""Kolon"",
               ccu.table_name AS ""RefTablo"", ccu.column_name AS ""RefKolon""
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND tc.table_schema = '{Esc(schema)}' AND tc.table_name = '{Esc(name)}'
        ORDER BY tc.constraint_name";

    public string RowCountSql(string schema, string name) =>
        $"SELECT n_live_tup AS \"SatirSayisi\" FROM pg_stat_user_tables WHERE schemaname='{Esc(schema)}' AND relname='{Esc(name)}'";

    public string RelOutgoingSql(string schema, string name) => $@"
        SELECT tc.constraint_name AS ""FK_Adi"", kcu.column_name AS ""BizimKolon"",
               ccu.table_schema || '.' || ccu.table_name AS ""RefTablo"", ccu.column_name AS ""RefKolon""
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND tc.table_schema = '{Esc(schema)}' AND tc.table_name = '{Esc(name)}'
        ORDER BY tc.constraint_name";

    public string RelIncomingSql(string schema, string name) => $@"
        SELECT tc.constraint_name AS ""FK_Adi"",
               tc.table_schema || '.' || tc.table_name AS ""KaynakTablo"", kcu.column_name AS ""KaynakKolon"",
               ccu.column_name AS ""BizimKolon""
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
        JOIN information_schema.constraint_column_usage ccu
          ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND ccu.table_schema = '{Esc(schema)}' AND ccu.table_name = '{Esc(name)}'
        ORDER BY tc.table_name, tc.constraint_name";

    public string ActiveSessionsSql => @"
        SELECT
            a.pid AS ""SessionId"",
            a.usename AS ""Kullanici"",
            a.client_addr::text AS ""Host"",
            a.application_name AS ""Program"",
            a.state AS ""Durum"",
            upper(split_part(ltrim(a.query), ' ', 1)) AS ""Komut"",
            a.wait_event_type || ':' || a.wait_event AS ""WaitType"",
            (pg_blocking_pids(a.pid))[1] AS ""BlokeEden"",
            NULL::bigint AS ""CpuMs"",
            (extract(epoch FROM (now() - a.query_start)) * 1000)::bigint AS ""GecenMs"",
            a.datname AS ""Veritabani"",
            left(a.query, 2000) AS ""Sorgu""
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
        ORDER BY (pg_blocking_pids(a.pid))[1] DESC NULLS LAST, a.query_start NULLS LAST";

    public string SlowQueriesSql(int top, string by)
    {
        // PG'de ayrı CPU süresi tutulmaz: --by cpu sessizce başka bir ölçüte eşlenmez, net hata verir.
        var orderBy = by.ToLowerInvariant() switch
        {
            "total" => "s.total_exec_time",
            "cpu" => throw new ArgumentException("PostgreSQL pg_stat_statements CPU süresi tutmaz; --by cpu desteklenmez (avg|total|reads|executions)."),
            "reads" => "(s.shared_blks_hit + s.shared_blks_read)",
            "executions" => "s.calls",
            "avg" => "s.mean_exec_time",
            _ => throw new ArgumentException($"--by yalnız avg|total|cpu|reads|executions olabilir: '{by}'")
        };
        // Sütun şekli SQL Server ile aynı; PG'de karşılığı olmayanlar (CpuMs, SonCalistirma) NULL.
        return $@"
            SELECT
                s.calls AS ""Calistirma"",
                round(s.total_exec_time::numeric, 1) AS ""ToplamMs"",
                round(s.mean_exec_time::numeric, 2) AS ""OrtalamaMs"",
                NULL::numeric AS ""CpuMs"",
                s.shared_blks_hit + s.shared_blks_read AS ""MantiksalOkuma"",
                NULL::timestamptz AS ""SonCalistirma"",
                left(s.query, 2000) AS ""Sorgu""
            FROM pg_stat_statements s
            WHERE s.dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            ORDER BY {orderBy} DESC
            LIMIT {top}";
    }

    public string? SlowQueriesPrecheckSql =>
        "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements')";

    public string SlowQueriesPrecheckMessage =>
        "pg_stat_statements eklentisi bu veritabanında kurulu değil. Etkinleştirmek için: " +
        "postgresql.conf → shared_preload_libraries = 'pg_stat_statements' (sunucu yeniden başlatılır), " +
        "sonra `CREATE EXTENSION pg_stat_statements;`.";

    public string? SlowQueriesErrorHint(DbException ex) =>
        ex is PostgresException { SqlState: "55000" }
            ? "pg_stat_statements kurulu ama yüklü değil: shared_preload_libraries'e ekleyip sunucuyu yeniden başlatın."
            : null;

    public string KillSql(int sessionId, bool hard) =>
        $"SELECT {(hard ? "pg_terminate_backend" : "pg_cancel_backend")}({sessionId})";

    public async Task<ExplainResult> ExplainAsync(DbConnection c, string sql, bool actual, string? format, bool allowSideEffects = false)
    {
        var fmt = (format ?? "text").ToLowerInvariant();
        if (fmt is not ("text" or "json" or "xml"))
            throw new ArgumentException($"PostgreSQL explain --format yalnız text|json|xml olabilir: '{format}'");

        var stmt = sql.Trim().TrimEnd(';', ' ', '\r', '\n', '\t');
        // Tek ifade şartı: "EXPLAIN a; DELETE ..." ikinci ifadeyi plansız GERÇEKTEN çalıştırırdı.
        if (SqlGuard.YorumVeMetinSil(stmt).Contains(';'))
            throw new ArgumentException("explain tek bir SQL ifadesi alır (`;` ile ayrılmış birden fazla ifade yok).");

        if (actual)
        {
            var red = YanEtkiDenetimi.Denetle(stmt, DbProvider.Postgres, allowSideEffects);
            if (red != null) throw new ArgumentException(red);
        }

        var opts = (actual ? "ANALYZE, BUFFERS, " : "") + $"FORMAT {fmt.ToUpperInvariant()}";

        // Hep transaction + ROLLBACK: ANALYZE ifadeyi gerçekten çalıştırır (INSERT/UPDATE/DELETE dahil);
        // plan alındıktan sonra TABLO değişikliği geri alınır. Sequence/dblink/NOTIFY gibi yan etkiler geri
        // alınmaz — bilinen belirteçler yukarıda (YanEtkiDenetimi) reddedilir. Tahmini planda da zararsız.
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"EXPLAIN ({opts}) {stmt}";
            await using var reader = await cmd.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync()) lines.Add(reader[0]?.ToString() ?? "");
            var plan = string.Join("\n", lines);
            var label = $"Execution Plan ({fmt.ToUpperInvariant()}{(actual ? ", ANALYZE+BUFFERS — tablo değişiklikleri geri alındı" : ", tahmini")})";
            return new ExplainResult(label, new[] { plan });
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
