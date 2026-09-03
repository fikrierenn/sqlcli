using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Query yürütme + saved/run + history + format
public class QueryCommands
{
    [Command("query", Description = "Tek SQL sorgusu çalıştırır ve tabular sonuç gösterir")]
    public async Task Query(
        [Argument(Description = "SQL sorgusu")] string sql,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Çıktı formatı: table|json|csv|md")] string format = "table",
        [Option("max-rows", Description = "Max satır sayısı (server-side TOP wrap)")] int maxRows = 1000,
        [Option("param", Description = "Adlı parametre: ad=deger (tekrarlanabilir)")] string[]? param = null,
        [Option("read-only", Description = "Yazma ifadesi içeriyorsa reddet (SQLCLI_READONLY=1 ile kalıcı)")] bool readOnly = false,
        [Option(Description = "Komut zaman aşımı (saniye, 0=varsayılan)")] int timeout = 0,
        [Option(Description = "Geçici hatada ek deneme sayısı")] int retry = 0)
    {
        var yasak = SqlGuard.Denetle(sql, readOnly);
        if (yasak != null)
        {
            AnsiConsole.MarkupLine($"[red]REDDEDİLDİ:[/] {Markup.Escape(yasak)}");
            throw new CommandExitedException(2);
        }
        var resolved = Resolve(conn, profile);
        var wrapped = resolved.Dialect.WrapLimit(sql, maxRows);
        var dt = await SqlExecutor.RunQueryAsync(
            resolved.ConnectionString, wrapped, ParamsCommands.Build(param), timeout, retry, null);
        OutputFormatter.Render(dt, OutputFormatter.Parse(format));
        ConfigStore.AppendHistory(sql, resolved.ProfileName);
    }

    [Command("top", Description = "SELECT TOP N * FROM <table> shortcut")]
    public async Task Top(
        [Argument(Description = "Tablo adı (schema.tablo veya tablo)")] string table,
        [Argument(Description = "Satır sayısı (default 10)")] int n = 10,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option(Description = "Çıktı formatı: table|json|csv|md")] string format = "table")
    {
        var resolved = Resolve(conn, profile);
        var sql = resolved.Dialect.TopSql(table, n);
        AnsiConsole.MarkupLine($"[grey]>>>[/] [cyan]{Markup.Escape(sql)}[/]\n");
        var dt = await SqlExecutor.RunQueryAsync(resolved.ConnectionString, sql);
        OutputFormatter.Render(dt, OutputFormatter.Parse(format));
        ConfigStore.AppendHistory(sql, resolved.ProfileName);
    }

    [Command("save", Description = "Bir SQL sorgusunu isim ile kaydet (~/.sqlcli/saved.json)")]
    public void Save(
        [Argument(Description = "Kayıt adı")] string name,
        [Argument(Description = "SQL sorgusu")] string sql)
    {
        ConfigStore.SaveQuery(name, sql);
        AnsiConsole.MarkupLine($"[green]✓[/] Kaydedildi: [cyan]{Markup.Escape(name)}[/]");
    }

    [Command("run", Description = "Kaydedilmiş bir sorguyu çalıştırır (sqlcli save ile kayıtlı)")]
    public async Task Run(
        [Argument(Description = "Kayıt adı")] string name,
        [Option] string? conn = null,
        [Option] string? profile = null,
        [Option] string format = "table")
    {
        var saved = ConfigStore.LoadSaved();
        if (!saved.TryGetValue(name, out var sql))
        {
            AnsiConsole.MarkupLine($"[red]HATA: '{Markup.Escape(name)}' adlı kayıt bulunamadı.[/]");
            AnsiConsole.MarkupLine("[grey]Listele:[/] [cyan]sqlcli saved-list[/]");
            return;
        }
        AnsiConsole.MarkupLine($"[grey]>>>[/] [cyan]{Markup.Escape(sql)}[/]\n");
        var resolved = Resolve(conn, profile);
        var dt = await SqlExecutor.RunQueryAsync(resolved.ConnectionString, sql);
        OutputFormatter.Render(dt, OutputFormatter.Parse(format));
        ConfigStore.AppendHistory(sql, resolved.ProfileName);
    }

    [Command("saved-list", Description = "Kaydedilmiş tüm sorguları listeler")]
    public void SavedList()
    {
        var saved = ConfigStore.LoadSaved();
        if (saved.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](henüz kayıtlı sorgu yok)[/]");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("[cyan]Ad[/]");
        t.AddColumn("[cyan]SQL[/]");
        foreach (var (name, sql) in saved.OrderBy(kv => kv.Key))
            t.AddRow(Markup.Escape(name), Markup.Escape(sql.Length > 80 ? sql[..77] + "…" : sql));
        AnsiConsole.Write(t);
    }

    [Command("saved-delete", Description = "Kaydedilmiş sorguyu sil")]
    public void SavedDelete([Argument] string name)
    {
        if (ConfigStore.DeleteSaved(name))
            AnsiConsole.MarkupLine($"[green]✓[/] Silindi: [cyan]{Markup.Escape(name)}[/]");
        else
            AnsiConsole.MarkupLine($"[yellow]Bulunamadı: {Markup.Escape(name)}[/]");
    }

    [Command("history", Description = "Son çalışan sorguları gösterir (~/.sqlcli/history.log)")]
    public void History([Option(Description = "Kaç son satır")] int take = 30)
    {
        var entries = ConfigStore.ReadHistory(take);
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](geçmiş yok)[/]");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("[cyan]Zaman[/]");
        t.AddColumn("[cyan]Profil[/]");
        t.AddColumn("[cyan]SQL[/]");
        foreach (var e in entries)
            t.AddRow(
                e.At.ToString("MM-dd HH:mm:ss"),
                Markup.Escape(e.Profile ?? "-"),
                Markup.Escape(e.Sql.Length > 100 ? e.Sql[..97] + "…" : e.Sql));
        AnsiConsole.Write(t);
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
