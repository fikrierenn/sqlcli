using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cocona;
using Dapper;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Veri komutları (çok-sağlayıcı): tables-empty, seed-demo, export, import, diff.
public class DataCommands
{
    [Command("tables-empty", Description = "Boş (0 satır) tabloları listeler")]
    public async Task TablesEmpty([Option] string? conn = null, [Option] string? profile = null, [Option] string format = "table")
    {
        var r = Resolve(conn, profile);
        var dt = await SqlExecutor.RunQueryAsync(r.ConnectionString, r.Dialect.EmptyTablesSql);
        OutputFormatter.Render(dt, OutputFormatter.Parse(format));
    }

    [Command("seed-demo", Description = "Demo seed dosyalarını çalıştırır (seed_demo.sql, seed-demo.sql)")]
    public async Task SeedDemo([Option] string? conn = null, [Option] string? profile = null, [Option("sql-dir")] string? sqlDir = null)
    {
        var r = Resolve(conn, profile);
        var dir = ConnectionResolver.ResolveSqlDir(sqlDir);
        var found = false;
        foreach (var f in new[] { "seed_demo.sql", "seed-demo.sql", "demo.sql" })
        {
            var p = Path.Combine(dir, f);
            if (File.Exists(p)) { await SqlExecutor.RunScriptAsync(r.ConnectionString, p, tolerant: true); found = true; }
        }
        if (!found) AnsiConsole.MarkupLine($"[yellow]Demo seed dosyası bulunamadı ({dir}).[/]");
    }

    [Command("export", Description = "SELECT sonucunu dosyaya yazar (json|csv|md)")]
    public async Task Export(
        [Argument(Description = "SQL sorgusu")] string sql,
        [Option("out", Description = "Çıktı dosyası (uzantıdan format çıkar: .json/.csv/.md)")] string outFile,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Format (dosya uzantısını ezmek için): json|csv|md")] string? format = null)
    {
        var r = Resolve(conn, profile);
        var dt = await SqlExecutor.RunQueryAsync(r.ConnectionString, sql);
        var fmt = format ?? Path.GetExtension(outFile).TrimStart('.');
        var text = Serialize(dt, fmt);
        await File.WriteAllTextAsync(outFile, text, new UTF8Encoding(false));
        AnsiConsole.MarkupLine($"[green]✓[/] {dt.Rows.Count} satır → [cyan]{Markup.Escape(outFile)}[/] ({fmt})");
    }

    [Command("import", Description = "JSON dizisini (array of objects) hedef tabloya INSERT eder")]
    public async Task Import(
        [Argument(Description = "JSON dosya yolu (array of objects)")] string file,
        [Option("table", Description = "Hedef tablo (schema.tablo)")] string table,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option("batch", Description = "Kaç satırda bir COMMIT/ilerleme")] int batch = 500)
    {
        if (!File.Exists(file)) { AnsiConsole.MarkupLine($"[red]Dosya yok: {Markup.Escape(file)}[/]"); return; }
        var r = Resolve(conn, profile);

        var json = await File.ReadAllTextAsync(file);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            AnsiConsole.MarkupLine("[red]JSON kökü bir dizi (array) olmalı.[/]");
            return;
        }
        var rows = doc.RootElement.EnumerateArray().ToList();
        if (rows.Count == 0) { AnsiConsole.MarkupLine("[yellow]Boş dizi.[/]"); return; }

        // Kolonlar = ilk objenin anahtarları
        var cols = rows[0].EnumerateObject().Select(p => p.Name).ToList();
        var colList = string.Join(", ", cols);
        var paramList = string.Join(", ", cols.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT INTO {table} ({colList}) VALUES ({paramList})";

        await using var db = await ConnectionResolver.OpenAsync(r.ConnectionString);
        int done = 0;
        foreach (var row in rows)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = insertSql;
            for (int i = 0; i < cols.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@p{i}";
                p.Value = JsonValue(row, cols[i]);
                cmd.Parameters.Add(p);
            }
            await cmd.ExecuteNonQueryAsync();
            if (++done % batch == 0) AnsiConsole.MarkupLine($"[grey]  {done}/{rows.Count}...[/]");
        }
        AnsiConsole.MarkupLine($"[green]✓[/] {done} satır → {Markup.Escape(table)}");
    }

    [Command("diff", Description = "İki profil arasında tablo listesi + satır sayısı farkı")]
    public async Task Diff(
        [Option("a", Description = "1. profil")] string a,
        [Option("b", Description = "2. profil")] string b,
        [Option("query", Description = "Opsiyonel: iki tarafta çalıştırılıp satır-set farkı alınacak SQL")] string? query = null)
    {
        var ra = ResolveProfile(a);
        var rb = ResolveProfile(b);

        if (query != null)
        {
            var da = await SqlExecutor.RunQueryAsync(ra.ConnectionString, query);
            var db2 = await SqlExecutor.RunQueryAsync(rb.ConnectionString, query);
            var sa = RowSet(da);
            var sb = RowSet(db2);
            var onlyA = sa.Except(sb).ToList();
            var onlyB = sb.Except(sa).ToList();
            AnsiConsole.MarkupLine($"\n[cyan]Sorgu satır-set farkı[/] — sadece [green]{a}[/]: {onlyA.Count} · sadece [yellow]{b}[/]: {onlyB.Count} · ortak: {sa.Intersect(sb).Count()}");
            foreach (var x in onlyA.Take(20)) AnsiConsole.MarkupLine($"  [green]-{a}[/] {Markup.Escape(x)}");
            foreach (var x in onlyB.Take(20)) AnsiConsole.MarkupLine($"  [yellow]+{b}[/] {Markup.Escape(x)}");
            return;
        }

        // Tablo listesi farkı
        var ta = TableNames(await SqlExecutor.RunQueryAsync(ra.ConnectionString, ra.Dialect.TablesSql));
        var tb = TableNames(await SqlExecutor.RunQueryAsync(rb.ConnectionString, rb.Dialect.TablesSql));
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("[cyan]Tablo[/]"); t.AddColumn($"[green]{Markup.Escape(a)}[/]"); t.AddColumn($"[yellow]{Markup.Escape(b)}[/]");
        foreach (var name in ta.Union(tb).OrderBy(x => x))
            t.AddRow(Markup.Escape(name), ta.Contains(name) ? "✓" : "[grey]-[/]", tb.Contains(name) ? "✓" : "[grey]-[/]");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[grey]{a}: {ta.Count} tablo · {b}: {tb.Count} tablo · sadece {a}: {ta.Except(tb).Count()} · sadece {b}: {tb.Except(ta).Count()}[/]");
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static object JsonValue(JsonElement row, string col)
    {
        if (!row.TryGetProperty(col, out var v)) return DBNull.Value;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
            JsonValueKind.String => v.GetString() ?? (object)DBNull.Value,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            _ => v.GetRawText()   // nested obj/array → ham JSON metni
        };
    }

    private static HashSet<string> TableNames(DataTable dt)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in dt.Rows)
            set.Add(string.Join(".", dt.Columns.Cast<DataColumn>().Select(c => r[c]?.ToString())));
        return set;
    }

    private static HashSet<string> RowSet(DataTable dt)
    {
        var set = new HashSet<string>();
        foreach (DataRow r in dt.Rows)
            set.Add(string.Join("", dt.Columns.Cast<DataColumn>().Select(c => Cell(r[c]))));
        return set;
    }

    private static string Cell(object? v) => v switch
    {
        null or DBNull => "∅",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    private static string Serialize(DataTable dt, string fmt)
    {
        fmt = fmt.ToLowerInvariant();
        if (fmt is "json")
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (DataRow r in dt.Rows)
            {
                var d = new Dictionary<string, object?>();
                foreach (DataColumn c in dt.Columns) d[c.ColumnName] = r[c] is DBNull ? null : r[c];
                list.Add(d);
            }
            return JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        if (fmt is "md" or "markdown")
        {
            var sb = new StringBuilder();
            sb.Append("| ").AppendJoin(" | ", dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)).AppendLine(" |");
            sb.Append('|'); for (int i = 0; i < dt.Columns.Count; i++) sb.Append("---|"); sb.AppendLine();
            foreach (DataRow r in dt.Rows)
                sb.Append("| ").AppendJoin(" | ", dt.Columns.Cast<DataColumn>().Select(c => Cell(r[c]).Replace("|", "\\|"))).AppendLine(" |");
            return sb.ToString();
        }
        // csv (default)
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", dt.Columns.Cast<DataColumn>().Select(c => CsvEsc(c.ColumnName))));
        foreach (DataRow r in dt.Rows)
            csv.AppendLine(string.Join(",", dt.Columns.Cast<DataColumn>().Select(c => CsvEsc(Cell(r[c])))));
        return csv.ToString();
    }

    private static string CsvEsc(string s) =>
        (s.Contains(',') || s.Contains('"') || s.Contains('\n')) ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static ConnectionResolver.ResolvedConnection Resolve(string? conn, string? profile)
    {
        var r = ConnectionResolver.Resolve(conn, profile);
        if (r == null) { AnsiConsole.MarkupLine("[red]HATA: Bağlantı bilgisi bulunamadı.[/]"); throw new CommandExitedException(1); }
        return r;
    }

    private static ConnectionResolver.ResolvedConnection ResolveProfile(string profile)
    {
        var r = ConnectionResolver.Resolve(null, profile);
        if (r == null) { AnsiConsole.MarkupLine($"[red]Profil çözülemedi: {Markup.Escape(profile)}[/]"); throw new CommandExitedException(1); }
        return r;
    }
}
