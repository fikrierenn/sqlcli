using System.Text;

namespace SqlCli.Core;

// WITH ENCRYPTION çözme — saf (DB'siz, test edilebilir) çekirdek.
//
// SQL Server bir modülü (proc/view/function/trigger) WITH ENCRYPTION ile saklarken
// metni UCS-2 (UTF-16LE) olarak alır ve nesnenin KİMLİĞİNE (objid + subobjid/colid)
// bağlı bir akış şifresiyle XOR'lar. Anahtar akışı İÇERİĞE değil yalnız nesne kimliğine
// bağlı olduğundan, aynı nesneyi bilinen bir metne ALTER edip onun şifreli halini okursak:
//
//     sifreli_hedef = duz_hedef  XOR  K
//     sifreli_kukla = duz_kukla  XOR  K
//  => duz_hedef     = sifreli_hedef XOR sifreli_kukla XOR duz_kukla
//
// K (anahtar akışı) iki okumada AYNIdır çünkü ikisi de aynı objid'dir. Bu yüzden kukla,
// AYRI bir nesne değil, hedefin kendisinin tek transaction içinde ALTER'lanıp hemen geri
// alınmış (ROLLBACK) halidir — hedef kalıcı olarak DEĞİŞMEZ. dbForge SQL Decryptor da
// özünde bunu yapar.
public static class DecryptCore
{
    /// <summary>
    /// Çözme için kukla modül: `ALTER` ile GÖNDERİLEN metin ve SQL Server'ın `sys.sysobjvalues`'a
    /// SAKLADIĞI düz metin.
    ///
    /// KRİTİK: SQL Server, `ALTER PROCEDURE ...` gönderildiğinde tanımı `CREATE PROCEDURE ...`
    /// olarak saklar ("ALTER"→"CREATE", +1 karakter). Bilinen-düz-metin XOR'u SAKLANAN metni ister;
    /// bu yüzden <see cref="DummyModule.StoredPlain"/> `CREATE` ile başlar ve hedefle birebir aynı
    /// karakter uzunluğundadır. Dolgu, iskelet ile kapanış arasına boşlukla yapılır (yorum değil:
    /// metinde `*/` çakışması riski olmasın).
    /// </summary>
    /// <param name="type">sys.objects.type (P, PC, RF, V, FN, IF, TF, TR).</param>
    /// <param name="qualified">`[şema].[ad]` biçiminde hedef.</param>
    /// <param name="parentQualified">Trigger için bağlı olduğu tablo `[şema].[ad]`; diğerlerinde null.</param>
    /// <param name="storedCharCount">Hedefin SAKLANAN tanım uzunluğu (şifreli bayt / 2).</param>
    public static DummyModule BuildDummyModule(string type, string qualified, string? parentQualified, int storedCharCount)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        // İskelet, SAKLANAN biçimdir (CREATE ... WITH ENCRYPTION ...).
        var (header, footer) = t switch
        {
            "P" or "PC" or "RF" => ($"CREATE PROCEDURE {qualified} WITH ENCRYPTION AS", " RETURN"),
            "V"                 => ($"CREATE VIEW {qualified} WITH ENCRYPTION AS SELECT", " 1 AS x"),
            "FN"                => ($"CREATE FUNCTION {qualified}() RETURNS int WITH ENCRYPTION AS BEGIN RETURN", " 0 END"),
            "IF"                => ($"CREATE FUNCTION {qualified}() RETURNS TABLE WITH ENCRYPTION AS RETURN SELECT", " 1 AS x"),
            "TF"                => ($"CREATE FUNCTION {qualified}() RETURNS @t TABLE (x int) WITH ENCRYPTION AS BEGIN RETURN", " END"),
            "TR"                => parentQualified is null
                ? throw new InvalidOperationException("Trigger için bağlı tablo (parent) gerekli.")
                : ($"CREATE TRIGGER {qualified} ON {parentQualified} WITH ENCRYPTION AFTER INSERT AS", " RETURN"),
            _ => throw new NotSupportedException(
                     $"Şifre çözme için desteklenmeyen nesne türü: '{type}'. Desteklenen: P, PC, RF, V, FN, IF, TF, TR.")
        };

        var padCount = storedCharCount - header.Length - footer.Length;
        if (padCount < 0)
            throw new InvalidOperationException(
                $"Hedef tanım ({storedCharCount} karakter) '{t}' türü için gereken en kısa kukla iskeletten " +
                $"({header.Length + footer.Length} karakter) daha kısa — bu nesne bu yöntemle çözülemiyor.");

        var storedPlain = header + new string(' ', padCount) + footer;      // CREATE … (storedCharCount karakter)
        var submitSql = "ALTER" + storedPlain["CREATE".Length..];           // ALTER … (storedCharCount-1 karakter)
        return new DummyModule(submitSql, storedPlain);
    }

    /// <summary>Kukla: çalıştırılacak `ALTER` metni + SQL Server'ın saklayacağı `CREATE` düz metni.</summary>
    public readonly record struct DummyModule(string SubmitSql, string StoredPlain);

    /// <summary>
    /// Bilinen-düz-metin XOR: duz_hedef = sifreli_hedef XOR sifreli_kukla XOR duz_kukla.
    /// Üç dizi de aynı uzunlukta olmalı; değilse çözme güvensizdir, hata verir.
    /// </summary>
    public static byte[] XorRecover(byte[] cipherTarget, byte[] cipherDummy, byte[] plainDummy)
    {
        if (cipherTarget.Length != cipherDummy.Length || cipherTarget.Length != plainDummy.Length)
            throw new InvalidOperationException(
                $"Uzunluk uyuşmazlığı — çözme iptal: hedef_şifreli={cipherTarget.Length}, " +
                $"kukla_şifreli={cipherDummy.Length}, kukla_düz={plainDummy.Length} bayt.");

        var sonuc = new byte[cipherTarget.Length];
        for (int i = 0; i < sonuc.Length; i++)
            sonuc[i] = (byte)(cipherTarget[i] ^ cipherDummy[i] ^ plainDummy[i]);
        return sonuc;
    }

    /// <summary>Çözülmüş UCS-2 (UTF-16LE) baytlarını metne çevirir.</summary>
    public static string DecodeUcs2(byte[] bytes) => Encoding.Unicode.GetString(bytes);

    /// <summary>Kukla modül metnini UCS-2 (UTF-16LE) bayta çevirir (şifreli blob ile hizalı).</summary>
    public static byte[] EncodeUcs2(string text) => Encoding.Unicode.GetBytes(text);

    /// <summary>
    /// Normal bağlantı string'indeki sunucu adını DAC (Dedicated Admin Connection) için
    /// `admin:` önekine çevirir. Örnek instance korunur (`admin:HOST\INST`). dacPort verilirse
    /// `admin:HOST,PORT`; verilmezse normal port DÜŞÜRÜLÜR (DAC portu farklıdır) ve SQL Browser'ın
    /// çözmesine bırakılır.
    /// </summary>
    public static string BuildDacDataSource(string dataSource, int? dacPort)
    {
        var ds = (dataSource ?? "").Trim();
        if (ds.StartsWith("admin:", StringComparison.OrdinalIgnoreCase)) ds = ds[6..].Trim();
        if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) ds = ds[4..].Trim();

        var comma = ds.IndexOf(',');
        var host = comma >= 0 ? ds[..comma] : ds;   // "HOST" veya "HOST\INST"

        return dacPort is int p ? $"admin:{host},{p}" : $"admin:{host}";
    }
}
