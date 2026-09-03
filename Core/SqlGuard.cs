namespace SqlCli.Core;

/// <summary>
/// SALT-OKUMA MUHAFIZI — yalnız okuma ifadelerine İZİN VERİR (allow-list).
///
/// NEDEN VAR: canlı ERP/muhasebe veritabanlarında keşif sorgusu koşturulurken tek yanlış
/// tuş (UPDATE yerine SELECT sanılan bir metin, kopyalanmış bir DELETE) geri alınamaz.
/// Kural dosyası ("ERP'ye yazma yasak") disiplin sağlar ama zorlama yapmaz; bu muhafız yapar.
///
/// ==== NİYE YASAK LİSTESİ DEĞİL, İZİN LİSTESİ ====
///
/// İlk sürüm (03.09.2026) yasak listesiydi: INSERT/UPDATE/DELETE/MERGE… ara, bulursan reddet.
/// O yaklaşım yanlıştı ve yanlışlığı SESSİZDİ: listede olmayan bir yazma yolu (saklı yordam
/// çağrısı, sonradan eklenen bir ifade, sağlayıcıya özgü bir komut) muhafızdan GEÇER ve
/// muhafız "bir şey kaçırdım" demez. Aynı ders başka bir depoda Odoo tarafında ölçüldü:
/// yasak listesi `action_post` / `button_validate` gibi yazan metotları kaçırıyor ve
/// kaçırdığını söylemiyor.
///
/// Bu yüzden kural tersine çevrildi: **tanıdığım okuma biçimleri dışında HER ŞEY reddedilir**
/// ve red mesajı NEYİ tanımadığını söyler. Bilinmeyen bir ifade "belki zararsızdır" diye
/// geçirilmez — geçirmenin maliyeti geri alınamaz, reddetmenin maliyeti bir bayrak kaldırmak.
///
/// İzinli: `SELECT …` · `WITH … SELECT …` (CTE). Tek ifade.
/// Reddedilen örnekler: UPDATE/DELETE/MERGE/DROP · EXEC / sp_executesql (dinamik SQL, içi
/// görünmez) · SELECT … INTO (nesne yaratır) · birden fazla ifade (`;` ile zincir) ·
/// tanınmayan ilk anahtar kelime (DECLARE, SET, CREATE, GRANT, BACKUP, bilinmeyen).
///
/// Açma yolları: `--read-only` bayrağı veya `SQLCLI_READONLY=1` ortam değişkeni
/// (bir profili kalıcı salt-okuma yapmak için kabuk profiline konur).
///
/// Yaklaşım kusursuz bir SQL parser DEĞİL: string ve yorumlar elenir, sonra ifadenin ilk
/// anahtar kelimesine ve birkaç tehlikeli belirtece bakılır. Amaç kararlı bir saldırganı
/// değil, kendi hatamızı durdurmak.
/// </summary>
public static class SqlGuard
{
    /// <summary>Okuma olarak TANINAN ifade başlangıçları. Bu listenin dışı reddedilir.</summary>
    private static readonly string[] IzinliBaslangic = ["SELECT", "WITH"];

    /// <summary>
    /// İzinli bir ifadenin içinde bile reddedilen belirteçler: dinamik SQL (içi görünmez)
    /// ve nesne yaratan/yazan biçimler.
    /// </summary>
    private static readonly string[] IcerdeYasak =
    [
        "EXEC", "EXECUTE", "SP_EXECUTESQL", "XP_CMDSHELL", "OPENROWSET", "OPENQUERY",
        "OPENDATASOURCE", "BULK", "WRITETEXT", "UPDATETEXT", "READTEXT",
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE",
        "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "SHUTDOWN", "RECONFIGURE",
    ];

    public static bool SaltOkumaAcikMi(bool bayrak) =>
        bayrak || Environment.GetEnvironmentVariable("SQLCLI_READONLY") is "1" or "true" or "TRUE";

    /// <summary>
    /// Salt-okuma açıkken sorguyu denetler. Geçerse null, geçmezse NEDEN reddedildiğini
    /// söyleyen mesaj döner (neyi tanımadığı dahil — "sessiz kaçırma" olmasın).
    /// </summary>
    public static string? Denetle(string sql, bool bayrak)
    {
        if (!SaltOkumaAcikMi(bayrak)) return null;

        var temiz = YorumVeMetinSil(sql).Trim();
        if (temiz.Length == 0)
            return "SALT-OKUMA: sorgu boş görünüyor (yorum/metin elendikten sonra) — reddedildi.";

        // Birden fazla ifade: ilki okuma olsa bile ikincisi yazma olabilir.
        var ifadeler = temiz.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Where(x => x.Trim().Length > 0).ToList();
        if (ifadeler.Count > 1)
            return $"SALT-OKUMA: {ifadeler.Count} ayrı ifade var (`;` ile ayrılmış) — " +
                   "tek okuma ifadesi dışında hiçbir şeye izin verilmiyor, reddedildi.";

        var ifade = ifadeler[0].TrimStart('(', ' ', '\t', '\r', '\n');
        var ilk = IlkKelime(ifade);

        if (!IzinliBaslangic.Contains(ilk, StringComparer.OrdinalIgnoreCase))
            return $"SALT-OKUMA: ifade '{ilk}' ile başlıyor; izinli başlangıçlar yalnız " +
                   $"{string.Join(", ", IzinliBaslangic)}. Tanınmayan her şey reddedilir " +
                   "(yasak listesi değil izin listesi — bilinmeyen yazma yolları sessizce geçmesin).";

        var kelimeler = Kelimeler(ifade);
        foreach (var yasak in IcerdeYasak)
            if (kelimeler.Contains(yasak, StringComparer.OrdinalIgnoreCase))
                return $"SALT-OKUMA: okuma ifadesinin içinde '{yasak}' geçiyor — reddedildi. " +
                       "(Dinamik SQL'in içi görünmediği için EXEC/sp_executesql da izinli değil.)";

        // `SELECT … INTO yeni_tablo` nesne yaratır; `INSERT INTO` yukarıda yakalanır.
        for (int i = 0; i < kelimeler.Count - 1; i++)
            if (string.Equals(kelimeler[i], "INTO", StringComparison.OrdinalIgnoreCase))
                return "SALT-OKUMA: `SELECT … INTO` yeni tablo yaratır — reddedildi.";

        return null;
    }

    /// <summary>Geriye dönük uyumluluk: yazma belirteci varsa adını döner (tanılama için).</summary>
    public static string? YazmaBul(string sql)
    {
        var kelimeler = Kelimeler(YorumVeMetinSil(sql));
        foreach (var y in IcerdeYasak)
            if (kelimeler.Contains(y, StringComparer.OrdinalIgnoreCase)) return y;
        return null;
    }

    private static string IlkKelime(string s)
    {
        var k = Kelimeler(s);
        return k.Count > 0 ? k[0].ToUpperInvariant() : "(boş)";
    }

    private static List<string> Kelimeler(string s) =>
        s.Split([' ', '\t', '\r', '\n', '(', ')', ',', ';', '[', ']', '.'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .ToList();

    /// <summary>Tek/çift tırnaklı metinleri ve -- / /* */ yorumlarını boşlukla değiştirir.</summary>
    private static string YorumVeMetinSil(string sql)
    {
        var cikti = new System.Text.StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                cikti.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
                cikti.Append(' ');
                continue;
            }
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
