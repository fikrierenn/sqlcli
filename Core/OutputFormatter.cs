using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace SqlCli.Core;

// Query sonucunu farklı formatlarda render eder.
// table: Spectre.Console renkli tablo (default, terminal için)
// json: Pretty-print JSON (Claude/script için parse-friendly)
// csv: Standart CSV
// md: Markdown tablo
public enum OutputFormat
{
    Table,
    Json,
    Csv,
    Md
}

public static class OutputFormatter
{
    public static OutputFormat Parse(string? value) => (value ?? "table").ToLowerInvariant() switch
    {
        "json" => OutputFormat.Json,
        "csv" => OutputFormat.Csv,
        "md" or "markdown" => OutputFormat.Md,
        _ => OutputFormat.Table
    };

    public static void Render(DataTable table, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json: RenderJson(table); break;
            case OutputFormat.Csv: RenderCsv(table); break;
            case OutputFormat.Md: RenderMarkdown(table); break;
            default: RenderTable(table); break;
        }
    }

    private static void RenderTable(DataTable table)
    {
        if (table.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](sonuç yok)[/]");
            return;
        }

        var t = new Table().Border(TableBorder.Rounded);
        foreach (DataColumn col in table.Columns)
            t.AddColumn(new TableColumn($"[cyan]{Markup.Escape(col.ColumnName)}[/]"));

        foreach (DataRow row in table.Rows)
        {
            var cells = new string[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var v = row[i];
                if (v == null || v is DBNull) cells[i] = "[grey]NULL[/]";
                else
                {
                    var s = FormatCell(v);
                    // Çok uzun string'leri kıs (200 char üzeri)
                    if (s.Length > 200) s = s[..197] + "…";
                    cells[i] = Markup.Escape(s);
                }
            }
            t.AddRow(cells);
        }

        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"\n[grey]{table.Rows.Count} satır[/]");
    }

    private static void RenderJson(DataTable table)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (DataColumn col in table.Columns)
            {
                var v = row[col];
                dict[col.ColumnName] = (v == null || v is DBNull) ? null : v;
            }
            rows.Add(dict);
        }
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Console.WriteLine(json);
    }

    private static void RenderCsv(DataTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            table.Columns.Cast<DataColumn>().Select(c => CsvEscape(c.ColumnName))));
        foreach (DataRow row in table.Rows)
        {
            sb.AppendLine(string.Join(",",
                table.Columns.Cast<DataColumn>().Select(c => CsvEscape(FormatCell(row[c])))));
        }
        Console.Write(sb.ToString());
    }

    private static void RenderMarkdown(DataTable table)
    {
        if (table.Rows.Count == 0) { Console.WriteLine("(sonuç yok)"); return; }
        var sb = new StringBuilder();
        sb.Append("| ");
        sb.AppendJoin(" | ", table.Columns.Cast<DataColumn>().Select(c => MdEscape(c.ColumnName)));
        sb.AppendLine(" |");
        sb.Append("|");
        for (int i = 0; i < table.Columns.Count; i++) sb.Append("---|");
        sb.AppendLine();
        foreach (DataRow row in table.Rows)
        {
            sb.Append("| ");
            sb.AppendJoin(" | ", table.Columns.Cast<DataColumn>().Select(c => MdEscape(FormatCell(row[c]))));
            sb.AppendLine(" |");
        }
        sb.AppendLine();
        sb.AppendLine($"**{table.Rows.Count} satır**");
        Console.Write(sb.ToString());
    }

    private static string FormatCell(object? v) => v switch
    {
        null or DBNull => string.Empty,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        _ => v.ToString() ?? string.Empty
    };

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static string MdEscape(string s) =>
        s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}
