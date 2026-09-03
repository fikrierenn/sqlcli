using System.Globalization;
using Dapper;

namespace SqlCli.Commands;

/// <summary>
/// ADLI PARAMETRE kurucusu — `--param ad[:tip]=deger` çiftlerini Dapper parametresine çevirir.
///
/// NEDEN VAR: sorguya değer gömmek iki tuzağı birden açar — (a) injection, (b) TARİH BİÇİMİ.
/// Bu ortamda tarih yerelde `dd.MM.yyyy`, linked server'da `YYYYMMDD` yazılır; string
/// birleştirmeyle taşınan bir tarih sessizce yanlış günü seçebilir.
///
/// TÜR AÇIK YAZILIR, TAHMİN EDİLMEZ:
///   --param n=42                → tam sayı (bigint)
///   --param oran=1.5            → ondalık
///   --param ad=Kitap            → metin
///   --param bas:date=2026-09-01 → tarih (ISO yyyy-MM-dd veya yyyyMMdd; DbType.Date)
///   --param kod:str=20260901    → METİN olarak zorla (tarih/kod ayrımı sende)
///   --param aktif:bool=true     → bit
///
/// Tip verilmezse yalnız tam sayı ve ondalık tanınır, gerisi metindir. Tarih ASLA
/// otomatik tanınmaz: "01.02.2026" gün mü ay mı belirsizdir ve `20260901` gibi bir değer
/// tam sayıya düşerse `datetime` karşılaştırması "incompatible" hatası verir (ölçülen
/// vaka 2026-09-03). Niyet açık yazılınca bu sınıf hata kalkar.
/// </summary>
public static class ParamsCommands
{
    public static DynamicParameters? Build(string[]? param)
    {
        if (param == null || param.Length == 0) return null;

        var p = new DynamicParameters();
        foreach (var ham in param)
        {
            var i = ham.IndexOf('=');
            if (i <= 0)
                throw new ArgumentException(
                    $"--param biçimi 'ad=deger' ya da 'ad:tip=deger' olmalı: '{ham}'");

            var solTaraf = ham[..i].Trim().TrimStart('@');
            var deger = ham[(i + 1)..];

            string ad = solTaraf, tip = "";
            var iki = solTaraf.IndexOf(':');
            if (iki > 0)
            {
                ad = solTaraf[..iki].Trim();
                tip = solTaraf[(iki + 1)..].Trim().ToLowerInvariant();
            }

            switch (tip)
            {
                case "":
                    // Tip verilmedi: tam sayı → ondalık → metin. Tarih tanınmaz (bilinçli).
                    if (long.TryParse(deger, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tam))
                        p.Add(ad, tam);
                    else if (decimal.TryParse(deger, NumberStyles.Any, CultureInfo.InvariantCulture, out var ond))
                        p.Add(ad, ond);
                    else
                        p.Add(ad, deger);
                    break;

                case "str" or "string" or "text":
                    p.Add(ad, deger);
                    break;

                case "int" or "bigint":
                    p.Add(ad, long.Parse(deger, CultureInfo.InvariantCulture));
                    break;

                case "dec" or "decimal" or "num":
                    p.Add(ad, decimal.Parse(deger, NumberStyles.Any, CultureInfo.InvariantCulture));
                    break;

                case "date" or "datetime":
                    p.Add(ad, TarihCoz(deger, ad), tip == "date" ? System.Data.DbType.Date : System.Data.DbType.DateTime2);
                    break;

                case "bool" or "bit":
                    p.Add(ad, bool.Parse(deger));
                    break;

                case "guid" or "uniqueidentifier":
                    p.Add(ad, Guid.Parse(deger));
                    break;

                default:
                    throw new ArgumentException(
                        $"--param bilinmeyen tip ':{tip}' ({ad}). Geçerli: str, int, dec, date, datetime, bool, guid.");
            }
        }
        return p;
    }

    /// <summary>
    /// Tarihi YALNIZ belirsizlik taşımayan biçimlerden okur: `yyyy-MM-dd`, `yyyyMMdd`,
    /// `yyyy-MM-dd HH:mm[:ss]`. `01.02.2026` gibi biçim REDDEDİLİR — gün/ay sırası
    /// belirsizdir ve sessiz yanlış gün seçmek en pahalı hata sınıfıdır.
    /// </summary>
    private static DateTime TarihCoz(string deger, string ad)
    {
        string[] kalip = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(deger, kalip, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
            return t;

        throw new ArgumentException(
            $"--param {ad}:date değeri belirsiz biçimde: '{deger}'. " +
            "Kabul edilen: yyyy-MM-dd, yyyyMMdd, yyyy-MM-dd HH:mm[:ss]. " +
            "dd.MM.yyyy KABUL EDİLMEZ (gün/ay sırası belirsiz) — ISO yaz ya da :str kullanıp " +
            "dönüşümü SQL'de açıkça belirt (CONVERT(date, @x, 104)).");
    }
}
