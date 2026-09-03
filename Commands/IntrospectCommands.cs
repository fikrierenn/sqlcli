using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Schema keşif (dialect-aware): status, tablolar, rows, fk, indexes, conn, profiles, describe, search, relationships
public class IntrospectCommands
{
    [Command("status", Description = "Sunucu/veritabanı durum bilgisi")]
    public async Task Status([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        await Run(r, r.Dialect.StatusSql, format);
    }

    [Command("tablolar", Description = "Tablo listesi (alias: list-tables, tables)")]
    public async Task Tablolar([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        await Run(r, r.Dialect.TablesSql, format);
    }

    [Command("tables", Description = "Tablo listesi (alias)")]
    public Task TablesAlias([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
        => Tablolar(conn, profile, format);

    [Command("list-tables", Description = "Tablo listesi (alias)")]
    public Task ListTables([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
        => Tablolar(conn, profile, format);

    [Command("rows", Description = "Tüm tablolardaki satır sayıları (alias: satir-sayisi)")]
    public async Task Rows([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        await Run(r, r.Dialect.RowsSql, format);
    }

    [Command("satir-sayisi", Description = "rows alias")]
    public Task SatirSayisi([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
        => Rows(conn, profile, format);

    [Command("fk-kontrol", Description = "Foreign key listesi")]
    public async Task FkKontrol([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        await Run(r, r.Dialect.FkSql, format);
    }

    [Command("indexler", Description = "Index listesi (alias: indexes)")]
    public async Task Indexler([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        await Run(r, r.Dialect.IndexesSql, format);
    }

    [Command("indexes", Description = "indexler alias")]
    public Task Indexes([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
        => Indexler(conn, profile, format);

    [Command("baglanti", Description = "Aktif bağlantı bilgisi (alias: conn-info)")]
    public async Task Baglanti([Option] string? conn = null, [Option] string? profile = null)
    {
        var r = Resolve(conn, profile);
        AnsiConsole.MarkupLine($"\n[cyan]Profil:[/] {Markup.Escape(r.ProfileName ?? "default")}");
        AnsiConsole.MarkupLine($"[cyan]Sağlayıcı:[/] {r.Dialect.Name}");
        AnsiConsole.MarkupLine($"[cyan]Bağlantı:[/] {Markup.Escape(ConnectionResolver.MaskPassword(r.ConnectionString))}\n");
        var dt = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.ConnInfoSql);
        OutputFormatter.Render(dt, OutputFormat.Table);
    }

    [Command("profiles", Description = "sqlcli.json'da tanımlı bağlantı profillerini listeler")]
    public void Profiles()
    {
        var profiles = ConnectionResolver.ListProfiles();
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](sqlcli.json'da profil tanımlı değil)[/]");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("[cyan]Profil[/]");
        foreach (var p in profiles) t.AddRow(Markup.Escape(p));
        AnsiConsole.Write(t);
    }

    // ─── describe / search / relationships ──────────────────────────────

    [Command("describe", Description = "Tablo şeması: kolonlar + tip + nullable + default + PK + index + FK (alias: desc)")]
    public async Task Describe(
        [Argument(Description = "Tablo adı (schema.tablo veya tablo)")] string table,
        [Option] string? conn = null,
        [Option] string? profile = null)
    {
        var r = Resolve(conn, profile);
        var (schema, name) = r.Dialect.SplitTable(table);

        var cols = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.DescribeColumnsSql(schema, name));
        if (cols.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Tablo bulunamadı: {Markup.Escape(schema)}.{Markup.Escape(name)}[/]");
            return;
        }
        AnsiConsole.MarkupLine($"\n[cyan bold]{Markup.Escape(schema)}.{Markup.Escape(name)}[/] — Kolonlar");
        OutputFormatter.Render(cols, OutputFormat.Table);

        var idx = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.DescribeIndexesSql(schema, name));
        if (idx.Rows.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[cyan bold]Index'ler[/]");
            OutputFormatter.Render(idx, OutputFormat.Table);
        }

        var fks = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.DescribeFksSql(schema, name));
        if (fks.Rows.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[cyan bold]Foreign Key'ler (giden)[/]");
            OutputFormatter.Render(fks, OutputFormat.Table);
        }

        var rowCount = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.RowCountSql(schema, name));
        if (rowCount.Rows.Count > 0)
            AnsiConsole.MarkupLine($"\n[grey]Tahmini satır:[/] [cyan]{rowCount.Rows[0][0]}[/]");
    }

    [Command("desc", Description = "describe alias")]
    public Task Desc([Argument] string table, [Option] string? conn = null, [Option] string? profile = null)
        => Describe(table, conn, profile);

    [Command("search", Description = "Tablo veya kolon adında pattern ara (LIKE/ILIKE wildcard)")]
    public async Task Search(
        [Argument(Description = "Aranacak pattern (örn: User, %role%, *audit*)")] string pattern,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "tables|columns|both")] string scope = "both",
        [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        var like = pattern.Replace('*', '%');
        if (!like.Contains('%')) like = $"%{like}%";

        if (scope != "columns")
        {
            AnsiConsole.MarkupLine($"\n[cyan bold]Tablolar (LIKE '{Markup.Escape(like)}')[/]");
            await Run(r, r.Dialect.SearchTablesSql(like), format);
        }
        if (scope != "tables")
        {
            AnsiConsole.MarkupLine($"\n[cyan bold]Kolonlar (LIKE '{Markup.Escape(like)}')[/]");
            await Run(r, r.Dialect.SearchColumnsSql(like), format);
        }
    }

    [Command("relationships", Description = "Tablonun gelen + giden FK'larını gösterir (alias: rel)")]
    public async Task Relationships(
        [Argument(Description = "Tablo adı (schema.tablo veya tablo)")] string table,
        [Option] string? conn = null,
        [Option] string? profile = null)
    {
        var r = Resolve(conn, profile);
        var (schema, name) = r.Dialect.SplitTable(table);

        AnsiConsole.MarkupLine("\n[cyan bold]→ Bu tablo başkalarına referans veriyor (giden FK)[/]");
        var outgoing = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.RelOutgoingSql(schema, name));
        OutputFormatter.Render(outgoing, OutputFormat.Table);

        AnsiConsole.MarkupLine("\n[cyan bold]← Bu tabloya referans verenler (gelen FK)[/]");
        var incoming = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.RelIncomingSql(schema, name));
        OutputFormatter.Render(incoming, OutputFormat.Table);
    }

    [Command("rel", Description = "relationships alias")]
    public Task Rel([Argument] string table, [Option] string? conn = null, [Option] string? profile = null)
        => Relationships(table, conn, profile);

    // ─── helpers ────────────────────────────────────────────────────────

    private static async Task Run(ConnectionResolver.ResolvedConnection r, string sql, string format)
    {
        var dt = await SqlExecutor.RunQueryAsync(r.ConnectionString, sql);
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
}
