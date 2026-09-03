namespace SqlCli.Core;

/// <summary>
/// SALT-OKUMA MUHAFIZI — yazma ifadelerini araç düzeyinde reddeder.
///
/// NEDEN VAR: canlı ERP/muhasebe veritabanlarında keşif sorgusu koşturulurken tek yanlış
/// tuş (UPDATE yerine SELECT sanılan bir metin, kopyalanmış bir DELETE) geri alınamaz.
/// Kural dosyası ("ERP'ye yazma yasak") disiplin sağlar ama zorlama yapmaz; bu muhafız yapar.
///
/// Açma yolları: `--read-only` bayrağı veya `SQLCLI_READONLY=1` ortam değişkeni
/// (bir profili kalıcı salt-okuma yapmak için kabuk profiline konur).
///
/// Yaklaşım: string/yorum içindeki kelimeler ELENİR, sonra yazma anahtar kelimesi aranır.
/// Amaç kusursuz bir SQL parser değil — kazayla yazmayı durdurmak. Kararlı bir saldırgan
/// için değil, kendi hatamıza karşı.
/// </summary>
public static class SqlGuard
{
    private static readonly string[] YazmaAnahtarlari =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE",
        "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "SHUTDOWN", "RECONFIGURE",
        "BULK", "WRITETEXT", "UPDATETEXT", "SP_EXECUTESQL", "XP_CMDSHELL", "OPENROWSET",
    ];

    public static bool SaltOkumaAcikMi(bool bayrak) =>
        bayrak || Environment.GetEnvironmentVariable("SQLCLI_READONLY") is "1" or "true" or "TRUE";

    /// <summary>Yazma ifadesi bulursa onun adını döner; salt-okumaysa null.</summary>
    public static string? YazmaBul(string sql)
    {
        var temiz = YorumVeMetinSil(sql);
        var kelimeler = temiz.Split(
            [' ', '\t', '\r', '\n', '(', ')', ',', ';', '[', ']', '.'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var k in kelimeler)
        {
            var buyuk = k.ToUpperInvariant();
            if (Array.IndexOf(YazmaAnahtarlari, buyuk) >= 0)
            {
                // `CREATE`/`DROP` yalnız geçici tablo/CTE bağlamında da geçebilir; ayırt etmiyoruz.
                // Salt-okuma modunda şüphe reddedilir (yanlış negatif pahalı, yanlış pozitif ucuz).
                return buyuk;
            }
        }
        return null;
    }

    /// <summary>Salt-okuma açıksa ve sorgu yazma içeriyorsa açıklayıcı hata mesajı döner.</summary>
    public static string? Denetle(string sql, bool bayrak)
    {
        if (!SaltOkumaAcikMi(bayrak)) return null;
        var bulunan = YazmaBul(sql);
        if (bulunan == null) return null;
        return $"SALT-OKUMA modu açık ve sorgu '{bulunan}' içeriyor — reddedildi. " +
               "Yazma gerçekten isteniyorsa --read-only'yi kaldır (ve SQLCLI_READONLY=0 yap).";
    }

    /// <summary>Tek/çift tırnaklı metinleri ve -- / /* */ yorumlarını boşlukla değiştirir.</summary>
    private static string YorumVeMetinSil(string sql)
    {
        var cikti = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            // satır yorumu
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                cikti.Append(' ');
                continue;
            }
            // blok yorumu
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
                cikti.Append(' ');
                continue;
            }
            // tek tırnaklı metin ('' kaçışı dahil)
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                    if (sql[i] == '\'') { i++; break; }
                    i++;
                }
                cikti.Append(' ');
                continue;
            }
            // çift tırnaklı tanımlayıcı (PostgreSQL)
            if (c == '"')
            {
                i++;
                while (i < sql.Length && sql[i] != '"') i++;
                i = Math.Min(sql.Length, i + 1);
                cikti.Append(' ');
                continue;
            }

            cikti.Append(c);
            i++;
        }
        return cikti.ToString();
    }
}
