using System.Text.RegularExpressions;
using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Schema yönetimi: migrate, seed, script, migrate-status, migrate-create
public class SchemaCommands
{
    [Command("migrate", Description = "docs/sql/schema_all.sql + db_objects.sql uygular (legacy) VEYA dizindeki numaralı NN_*.sql dosyalarını sırayla çalıştırır")]
    public async Task Migrate(
        [Option("conn", Description = "Bağlantı string'i (override)")] string? conn = null,
        [Option("profile", Description = "sqlcli.json > Profiles içinden profil adı")] string? profile = null,
        [Option("sql-dir", Description = "SQL dosya dizini (override)")] string? sqlDir = null,
        [Option(Description = "Tolerant mod (idempotent hatalarda devam et)")] bool tolerant = true)
    {
        var resolved = ResolveOrFail(conn, profile);
        var dir = ConnectionResolver.ResolveSqlDir(sqlDir);
        AnsiConsole.MarkupLine($"\n[cyan]Şema dizini:[/] {Markup.Escape(dir)}");

        // Legacy: schema_all.sql + db_objects.sql
        var schemaAll = Path.Combine(dir, "schema_all.sql");
        if (File.Exists(schemaAll))
        {
            await SqlExecutor.RunScriptAsync(resolved.ConnectionString, schemaAll, tolerant);
            var dbObj = Path.Combine(dir, "db_objects.sql");
            if (File.Exists(dbObj))
                await SqlExecutor.RunScriptAsync(resolved.ConnectionString, dbObj, tolerant: false);
            return;
        }

        // Modern: NN_*.sql sıralı çalıştır
        var migrations = Directory.GetFiles(dir, "*.sql")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d+[_-]"))
            .OrderBy(f => f)
            .ToList();

        if (migrations.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Uyarı: schema_all.sql ya da NN_*.sql migration dosyası bulunamadı.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]{migrations.Count} migration dosyası bulundu.[/]\n");
        foreach (var m in migrations)
            await SqlExecutor.RunScriptAsync(resolved.ConnectionString, m, tolerant);
    }

    [Command("seed", Description = "Standart seed dosyalarını çalıştırır (seed.sql, seed_core.sql vb.)")]
    public async Task Seed(
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option("sql-dir")] string? sqlDir = null)
    {
        var resolved = ResolveOrFail(conn, profile);
        var dir = ConnectionResolver.ResolveSqlDir(sqlDir);
        foreach (var f in new[] { "seed.sql", "seed_core.sql", "seed_company_claims.sql" })
        {
            var p = Path.Combine(dir, f);
            if (File.Exists(p)) await SqlExecutor.RunScriptAsync(resolved.ConnectionString, p, tolerant: true);
            else AnsiConsole.MarkupLine($"  [grey][[ATLA]] {f} bulunamadı[/]");
        }
    }

    [Command("script", Description = "Belirtilen SQL dosyasını çalıştırır")]
    public async Task Script(
        [Argument(Description = "SQL dosya yolu")] string file,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Hatalarda devam et")] bool tolerant = false)
    {
        var resolved = ResolveOrFail(conn, profile);
        await SqlExecutor.RunScriptAsync(resolved.ConnectionString, file, tolerant);
    }

    [Command("migrate-status", Description = "Migration dizinindeki dosyaları + DB'deki uygulanmış migration tablosunu kıyaslar")]
    public async Task MigrateStatus(
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option("sql-dir")] string? sqlDir = null,
        [Option("table", Description = "Migration history tablo adı")] string historyTable = "__MigrationHistory")
    {
        var resolved = ResolveOrFail(conn, profile);
        var dir = ConnectionResolver.ResolveSqlDir(sqlDir);

        var diskMigrations = Directory.GetFiles(dir, "*.sql")
            .Select(f => Path.GetFileName(f))
            .Where(n => Regex.IsMatch(n, @"^\d+[_-]"))
            .OrderBy(n => n)
            .ToList();

        // History tablosu var mı?
        DataTableLight applied;
        try
        {
            var t = await SqlExecutor.RunQueryAsync(resolved.ConnectionString,
                $"SELECT MigrationName, AppliedAt FROM dbo.{historyTable} ORDER BY MigrationName");
            applied = DataTableLight.From(t);
        }
        catch
        {
            AnsiConsole.MarkupLine($"[yellow]Migration history tablosu yok ({historyTable}).[/]");
            AnsiConsole.MarkupLine("[grey]İpucu:[/] [cyan]sqlcli migrate-create-history-table[/] (manuel oluştur)");
            applied = new DataTableLight();
        }

        var appliedSet = applied.Rows.Select(r => r["MigrationName"]?.ToString() ?? "").ToHashSet();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[cyan]Migration[/]");
        table.AddColumn("[cyan]Durum[/]");
        table.AddColumn("[cyan]Uygulanma Tarihi[/]");

        foreach (var m in diskMigrations)
        {
            var isApplied = appliedSet.Contains(m);
            var status = isApplied ? "[green]✓ Uygulanmış[/]" : "[yellow]○ Bekliyor[/]";
            var when = applied.Rows.FirstOrDefault(r => r["MigrationName"]?.ToString() == m)?["AppliedAt"]?.ToString() ?? "[grey]-[/]";
            table.AddRow(Markup.Escape(m), status, when);
        }

        // DB'de var ama diskte yok (orphan)
        foreach (var a in appliedSet.Where(a => !diskMigrations.Contains(a)))
            table.AddRow(Markup.Escape(a), "[red]⚠ Disk'te yok[/]", "[grey]-[/]");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{diskMigrations.Count} dosya / {appliedSet.Count} uygulanmış[/]");
    }

    [Command("migrate-create", Description = "Yeni numaralı migration dosyası iskelet üretir (sıradaki numara + idempotent şablon)")]
    public void MigrateCreate(
        [Argument(Description = "Migration adı (örn: AddUserEmail)")] string name,
        [Option("sql-dir")] string? sqlDir = null,
        [Option(Description = "Şablon: table | column | index | seed | bare")] string template = "bare")
    {
        var dir = ConnectionResolver.ResolveSqlDir(sqlDir);
        Directory.CreateDirectory(dir);

        // Sıradaki numara
        var existing = Directory.GetFiles(dir, "*.sql")
            .Select(f => Path.GetFileName(f))
            .Where(n => Regex.IsMatch(n, @"^\d+[_-]"))
            .Select(n => int.Parse(Regex.Match(n, @"^\d+").Value))
            .DefaultIfEmpty(0)
            .Max();
        var next = existing + 1;
        var safeName = Regex.Replace(name, @"[^A-Za-z0-9]", "");
        var fileName = $"{next:D2}_{safeName}.sql";
        var path = Path.Combine(dir, fileName);

        if (File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Hata: Dosya zaten var: {Markup.Escape(fileName)}[/]");
            return;
        }

        var content = template.ToLowerInvariant() switch
        {
            "table" => TemplateTable(safeName, next),
            "column" => TemplateColumn(safeName, next),
            "index" => TemplateIndex(safeName, next),
            "seed" => TemplateSeed(safeName, next),
            _ => TemplateBare(safeName, next)
        };

        File.WriteAllText(path, content);
        AnsiConsole.MarkupLine($"[green]✓[/] Oluşturuldu: [cyan]{Markup.Escape(path)}[/]");
        AnsiConsole.MarkupLine($"[grey]Şablon:[/] {template}");
    }

    private static string TemplateBare(string name, int n) => $@"-- Migration {n:D2}: {name}
-- Tarih: {DateTime.Now:yyyy-MM-dd}
-- İdempotent: çoklu çalıştırma güvenli

SET NOCOUNT ON;
GO

-- Buraya değişiklik kodunu yaz

GO

PRINT 'Migration {n:D2} tamamlandi.';
GO
";

    private static string TemplateTable(string name, int n) => $@"-- Migration {n:D2}: Create {name}
-- Tarih: {DateTime.Now:yyyy-MM-dd}
-- İdempotent: çoklu çalıştırma güvenli

SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{name}' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.{name}
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{name} PRIMARY KEY,
        -- TODO: kolonları ekle
        CreatedAt   DATETIME2(0) NOT NULL CONSTRAINT DF_{name}_CreatedAt DEFAULT GETUTCDATE(),
        CreatedBy   NVARCHAR(100) NULL
    );
    PRINT 'Tablo dbo.{name} olusturuldu.';
END
ELSE
    PRINT 'Tablo dbo.{name} zaten mevcut, atlandi.';
GO

PRINT 'Migration {n:D2} tamamlandi.';
GO
";

    private static string TemplateColumn(string name, int n) => $@"-- Migration {n:D2}: Add column to {name}
-- Tarih: {DateTime.Now:yyyy-MM-dd}

SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.TODO_TABLE')
               AND name = 'TODO_COLUMN')
BEGIN
    ALTER TABLE dbo.TODO_TABLE ADD TODO_COLUMN NVARCHAR(100) NULL;
    PRINT 'Kolon TODO_COLUMN eklendi.';
END
ELSE
    PRINT 'Kolon TODO_COLUMN zaten var.';
GO
";

    private static string TemplateIndex(string name, int n) => $@"-- Migration {n:D2}: Add index
-- Tarih: {DateTime.Now:yyyy-MM-dd}

SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TODO')
    CREATE NONCLUSTERED INDEX IX_TODO ON dbo.TODO_TABLE (TODO_COLUMN);
GO
";

    private static string TemplateSeed(string name, int n) => $@"-- Migration {n:D2}: Seed {name}
-- Tarih: {DateTime.Now:yyyy-MM-dd}
-- İdempotent: aynı kayıt varsa atla

SET NOCOUNT ON;
GO

INSERT dbo.TODO_TABLE (TODO_COL)
SELECT v.Val
FROM (VALUES (N'value1'), (N'value2')) v(Val)
WHERE NOT EXISTS (SELECT 1 FROM dbo.TODO_TABLE WHERE TODO_COL = v.Val);
GO
";

    private static ConnectionResolver.ResolvedConnection ResolveOrFail(string? conn, string? profile)
    {
        var r = ConnectionResolver.Resolve(conn, profile);
        if (r == null)
        {
            AnsiConsole.MarkupLine("[red]HATA: Bağlantı bilgisi bulunamadı.[/]");
            AnsiConsole.MarkupLine("[grey]SQLCLI_CONN env, --conn, --profile veya sqlcli.json gerekli.[/]");
            throw new CommandExitedException(1);
        }
        return r;
    }
}

// DataTable wrapper — null-safety için
internal sealed class DataTableLight
{
    public List<Dictionary<string, object?>> Rows { get; } = new();

    public static DataTableLight From(System.Data.DataTable dt)
    {
        var light = new DataTableLight();
        foreach (System.Data.DataRow r in dt.Rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (System.Data.DataColumn c in dt.Columns)
                dict[c.ColumnName] = r[c] is DBNull ? null : r[c];
            light.Rows.Add(dict);
        }
        return light;
    }
}
