using System.Data;
using System.Text;
using Cocona;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

/// <summary>
/// LOOKUP — kod/tip tablosunu makine-okunur (YAML/JSON) olarak döker, istenirse
/// kullanım sayısıyla birlikte.
///
/// NEDEN VAR: kod kümesi (belge tipi, hareket tipi, ödeme tipi) bir belgeye ELLE
/// yazıldığında eksik kalır ve eksikliği ancak bir rapor yanlış rakam üretince görülür.
/// Ölçülen vaka: bir hareket-tipi kümesi 20 kod olarak belgelenmişti, lookup'ta 34 vardı;
/// belgelenmeyenlerden biri 364.226 satırlık bir giriş tipiydi. Liste elle yazılmaz —
/// lookup tablosundan okunur. Bu komut o okumayı tek adıma indirir.
///
///   sqlcli lookup dbo.irsTip_vw                         # YAML: kod → ad
///   sqlcli lookup dbo.irsTip_vw --count-from dbo.irsHrk.ehTip   # + canlı kullanım sayısı
///   sqlcli lookup dbo.fatTip_vw --format json
///
/// `--count-from <tablo>.<kolon>` verildiğinde her kodun canlı veride kaç satırda
/// kullanıldığı sayılır; hiç kullanılmayan kod SİLİNMEZ, "canlıda yok" diye işaretlenir
/// (kullanılmıyor olması tanımsız olması demek değildir).
/// </summary>
public class LookupCommands
{
    [Command("lookup", Description = "Kod/tip tablosunu YAML/JSON döker (+ opsiyonel kullanım sayısı)")]
    public async Task Lookup(
        [Argument(Description = "Lookup tablosu/view (ör. dbo.irsTip_vw)")] string table,
        [Option("count-from", Description = "Kullanım sayacı: <tablo>.<kolon> (ör. dbo.irsHrk.ehTip)")] string? countFrom = null,
        [Option(Description = "Çıktı: yaml|json|table")] string format = "yaml",
        [Option("key-col", Description = "Kod kolonu (varsayılan: ilk kolon)")] string? keyCol = null,
        [Option("name-col", Description = "Ad kolonu (varsayılan: ikinci kolon)")] string? nameCol = null,
        [Option(Description = "Komut zaman aşımı (saniye)")] int timeout = 0,
        [Option(Description = "Geçici hatada ek deneme")] int retry = 1,
        [Option] string? conn = null,
        [Option] string? profile = null)
    {
        var r = ConnectionResolver.Resolve(conn, profile);
        if (r == null)
        {
            AnsiConsole.MarkupLine("[red]HATA: Bağlantı bilgisi bulunamadı.[/]");
            throw new CommandExitedException(2);
        }

        var dt = await SqlExecutor.RunQueryAsync(
            r.ConnectionString, $"SELECT * FROM {table}", null, timeout, retry, null);

        if (dt.Columns.Count < 2)
        {
            AnsiConsole.MarkupLine($"[red]HATA:[/] {Markup.Escape(table)} en az 2 kolon içermeli (kod + ad).");
            throw new CommandExitedException(2);
        }

        var kodKol = keyCol ?? dt.Columns[0].ColumnName;
        var adKol = nameCol ?? dt.Columns[1].ColumnName;

        // Kullanım sayacı — kodun canlı veride kaç satırda geçtiği.
        Dictionary<string, long>? sayim = null;
        if (!string.IsNullOrWhiteSpace(countFrom))
        {
            var son = countFrom.LastIndexOf('.');
            if (son <= 0)
            {
                AnsiConsole.MarkupLine("[red]HATA:[/] --count-from biçimi <tablo>.<kolon> olmalı.");
                throw new CommandExitedException(2);
            }
            var sayimTablo = countFrom[..son];
            var sayimKolon = countFrom[(son + 1)..];
            var sdt = await SqlExecutor.RunQueryAsync(
                r.ConnectionString,
                $"SELECT {sayimKolon} AS kod, COUNT(*) AS adet FROM {sayimTablo} GROUP BY {sayimKolon}",
                null, timeout, retry, null);
            sayim = new Dictionary<string, long>();
            foreach (DataRow s in sdt.Rows)
            {
                if (s["kod"] is DBNull) continue;
                sayim[s["kod"].ToString()!] = Convert.ToInt64(s["adet"]);
            }
        }

        if (format.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            OutputFormatter.Render(dt, OutputFormat.Table);
            return;
        }

        var satirlar = dt.Rows.Cast<DataRow>()
            .Where(x => x[kodKol] is not DBNull)
            .Select(x => (Kod: x[kodKol].ToString()!, Ad: x[adKol]?.ToString() ?? ""))
            .OrderBy(x => long.TryParse(x.Kod, out var n) ? n : long.MaxValue)
            .ThenBy(x => x.Kod, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("{");
            for (int i = 0; i < satirlar.Count; i++)
            {
                var (kod, ad) = satirlar[i];
                var etiket = Etiket(ad, kod, sayim);
                sb.Append("  \"").Append(kod).Append("\": ")
                  .Append(System.Text.Json.JsonSerializer.Serialize(etiket))
                  .AppendLine(i < satirlar.Count - 1 ? "," : "");
            }
            sb.AppendLine("}");
        }
        else // yaml
        {
            sb.AppendLine("values:");
            foreach (var (kod, ad) in satirlar)
                sb.Append("  ").Append(kod).Append(": '")
                  .Append(Etiket(ad, kod, sayim).Replace("'", "’")).AppendLine("'");
            sb.Append("kume_kaynagi: \"").Append(table).Append(" — ")
              .Append(satirlar.Count).Append(" kod, lookup'tan okundu (elle yazılmadı). ")
              .Append("Ölçüm: ").Append(DateTime.Now.ToString("yyyy-MM-dd")).AppendLine("\"");
        }

        Console.Out.Write(sb.ToString());
    }

    private static string Etiket(string ad, string kod, Dictionary<string, long>? sayim)
    {
        if (sayim == null) return ad;
        var adet = sayim.TryGetValue(kod, out var n) ? n : 0;
        return adet > 0 ? $"{ad} — {adet:N0} kayıt" : $"{ad} — canlıda yok";
    }
}
