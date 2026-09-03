using System.Globalization;
using Dapper;

namespace SqlCli.Commands;

/// <summary>
/// ADLI PARAMETRE kurucusu — `--param ad=deger` çiftlerini Dapper parametresine çevirir.
///
/// NEDEN VAR: sorguya değer gömmek iki tuzağı birden açar — (a) injection, (b) TARİH BİÇİMİ.
/// Bu ortamda tarih yerelde `dd.MM.yyyy`, linked server'da `YYYYMMDD` yazılır; string
/// birleştirmeyle taşınan bir tarih sessizce yanlış günü seçebilir. Parametre olarak
/// geçirildiğinde biçim sorunu ortadan kalkar (`WHERE eTarih >= @bas`).
///
/// TÜR: tam sayı ve ondalık tanınır; GERİSİ STRING olarak geçer. Tarih TAHMİN EDİLMEZ —
/// "01.02.2026" gün mü ay mı belirsizdir; SQL tarafında `CONVERT(date, @bas, 104)` yazmak
/// niyeti açık kılar. Sessiz tarih yorumu tam olarak kaçındığımız hata sınıfı.
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
                throw new ArgumentException($"--param biçimi 'ad=deger' olmalı: '{ham}'");

            var ad = ham[..i].Trim().TrimStart('@');
            var deger = ham[(i + 1)..];

            if (long.TryParse(deger, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tam))
                p.Add(ad, tam);
            else if (decimal.TryParse(deger, NumberStyles.Any, CultureInfo.InvariantCulture, out var ond))
                p.Add(ad, ond);
            else
                p.Add(ad, deger);   // tarih dahil: string geçer, dönüşüm SQL'de açıkça yapılır
        }
        return p;
    }
}
