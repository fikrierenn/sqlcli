using System.Globalization;
using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

/// <summary>
/// ASSERT — bir sorgunun SONUCUNU beklenen değerle karşılaştırır; uymazsa çıkış kodu 1.
///
/// NEDEN VAR: bir şema gerçeği öğrenildiğinde ("belge tipi kümesi {1,2,3,6,7,8}'dir",
/// "bu köprü ürünlerin %99'unu eşler") o gerçek bir belgeye yazılır ve orada BAYATLAR —
/// kimse yeniden koşmaz. Ölçüt saklanan SQL METNİ değil, ÇALIŞTIRMA SONUCU olmalı.
/// Bu komut, öğrenilen gerçeği tek satırlık koşulabilir bir denetime çevirir:
///
///   sqlcli assert "SELECT COUNT(*) FROM Sales WHERE DocumentsTypeId NOT IN (1,2,3,6,7,8)" --eq 0
///   sqlcli assert "SELECT COUNT(*) FROM Products WHERE ..." --ge 800000 --label pos-kopru
///
/// CI/betikten çağrılabilir: 0 = geçti, 1 = kırık, 2 = koşamadı (bağlantı/SQL hatası).
/// KOŞAMAMAK GEÇMEK DEĞİLDİR — bir ölçümün boş dönmesi ile hiç koşmaması ekranda aynı
/// görünür, o yüzden ayrı çıkış kodu.
/// </summary>
public class AssertCommands
{
    [Command("assert", Description = "Sorgu sonucunu beklenenle karşılaştırır (uymazsa exit 1)")]
    public async Task Assert(
        [Argument(Description = "Skaler döndüren SQL (ilk satır/ilk kolon)")] string sql,
        [Option("eq", Description = "Beklenen: eşit")] string? eq = null,
        [Option("ne", Description = "Beklenen: farklı")] string? ne = null,
        [Option("ge", Description = "Beklenen: en az (>=)")] string? ge = null,
        [Option("le", Description = "Beklenen: en çok (<=)")] string? le = null,
        [Option("gt", Description = "Beklenen: büyük (>)")] string? gt = null,
        [Option("lt", Description = "Beklenen: küçük (<)")] string? lt = null,
        [Option(Description = "Denetimin adı (çıktıda ve CI günlüğünde görünür)")] string? label = null,
        [Option(Description = "Kırılınca gösterilecek gerekçe — NEDEN önemli")] string? why = null,
        [Option("param", Description = "Adlı parametre: ad=deger (tekrarlanabilir)")] string[]? param = null,
        [Option("read-only", Description = "Yazma ifadesi içeriyorsa reddet")] bool readOnly = false,
        [Option(Description = "Komut zaman aşımı (saniye)")] int timeout = 0,
        [Option(Description = "Geçici hatada ek deneme sayısı")] int retry = 1,
        [Option] string? conn = null,
        [Option] string? profile = null)
    {
        var ad = label ?? "assert";

        var kosullar = new List<(string Ad, string Deger)>();
        if (eq != null) kosullar.Add(("eq", eq));
        if (ne != null) kosullar.Add(("ne", ne));
        if (ge != null) kosullar.Add(("ge", ge));
        if (le != null) kosullar.Add(("le", le));
        if (gt != null) kosullar.Add(("gt", gt));
        if (lt != null) kosullar.Add(("lt", lt));
        if (kosullar.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]HATA:[/] en az bir beklenti gerekli (--eq/--ne/--ge/--le/--gt/--lt).");
            throw new CommandExitedException(2);
        }

        var yasak = SqlGuard.Denetle(sql, readOnly);
        if (yasak != null)
        {
            AnsiConsole.MarkupLine($"[red]REDDEDİLDİ:[/] {Markup.Escape(yasak)}");
            throw new CommandExitedException(2);
        }

        var r = ConnectionResolver.Resolve(conn, profile);
        if (r == null)
        {
            AnsiConsole.MarkupLine("[red]HATA: Bağlantı bilgisi bulunamadı.[/]");
            throw new CommandExitedException(2);
        }

        object? olculen;
        try
        {
            olculen = await SqlExecutor.ScalarAsync(
                r.ConnectionString, sql, ParamsCommands.Build(param), timeout, retry,
                m => AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(m)}[/]"));
        }
        catch (Exception ex)
        {
            // KOŞAMADI ≠ KIRIK. Ayrı çıkış kodu (2) ile bildirilir; yeşil sayılmaz.
            AnsiConsole.MarkupLine($"[red]KOŞAMADI[/] [cyan]{Markup.Escape(ad)}[/] — {Markup.Escape(ex.Message.Split('\n')[0])}");
            throw new CommandExitedException(2);
        }

        var sayi = SayiyaCevir(olculen);
        var kirik = new List<string>();
        foreach (var (kAd, kDeger) in kosullar)
        {
            bool gecti = Karsilastir(kAd, olculen, sayi, kDeger);
            if (!gecti) kirik.Add($"{kAd} {kDeger}");
        }

        var gosterim = olculen?.ToString() ?? "NULL";
        if (kirik.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]OK[/]    [cyan]{Markup.Escape(ad)}[/] = {Markup.Escape(gosterim)} " +
                                   $"[grey]({string.Join(" ve ", kosullar.Select(k => k.Ad + " " + k.Deger))})[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[red]KIRIK[/] [cyan]{Markup.Escape(ad)}[/] = {Markup.Escape(gosterim)} " +
                               $"[grey](beklenen: {string.Join(" ve ", kirik)})[/]");
        if (!string.IsNullOrWhiteSpace(why))
            AnsiConsole.MarkupLine($"      [yellow]NEDEN ÖNEMLİ:[/] {Markup.Escape(why)}");
        AnsiConsole.MarkupLine("      [grey]Veri değişmiş olabilir — önce ÖLÇ, sonra ya kodu ya beklentiyi düzelt.[/]");
        throw new CommandExitedException(1);
    }

    private static decimal? SayiyaCevir(object? v)
    {
        if (v == null) return null;
        if (v is bool b) return b ? 1 : 0;
        try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static bool Karsilastir(string op, object? olculen, decimal? sayi, string beklenen)
    {
        var bSayi = decimal.TryParse(beklenen, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd)
            ? bd : (decimal?)null;

        // Sayısal karşılaştırma mümkünse sayısal; değilse metin (eq/ne için).
        if (sayi.HasValue && bSayi.HasValue)
        {
            return op switch
            {
                "eq" => sayi.Value == bSayi.Value,
                "ne" => sayi.Value != bSayi.Value,
                "ge" => sayi.Value >= bSayi.Value,
                "le" => sayi.Value <= bSayi.Value,
                "gt" => sayi.Value > bSayi.Value,
                "lt" => sayi.Value < bSayi.Value,
                _ => false,
            };
        }

        var metin = olculen?.ToString() ?? "";
        return op switch
        {
            "eq" => string.Equals(metin, beklenen, StringComparison.Ordinal),
            "ne" => !string.Equals(metin, beklenen, StringComparison.Ordinal),
            // sayısal olmayan değerde sıralama karşılaştırması anlamsız → kırık say
            _ => false,
        };
    }
}
