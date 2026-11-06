using System.Text.RegularExpressions;

namespace SqlCli.Core;

/// <summary>
/// `explain --analyze` / `--mode actual` için "geri alınamayan yan etki" denetimi.
///
/// NEDEN VAR: analiz ifadeyi GERÇEKTEN çalıştırır ve sonunda ROLLBACK yapar; ama rollback yalnız tablo
/// verisini geri alır. Sequence ilerlemesi (nextval / NEXT VALUE FOR), dblink üzerinden uzak yazma,
/// NOTIFY, sunucu-oturum seviyesinde kilit/ayar, large object dosya işlemleri, dış programlar (xp_cmdshell)
/// geri alınmaz. "Değişiklikler geri alındı" cümlesi bu durumlarda yanlış olurdu.
///
/// KARAR (güvenli taraf): bu belirteçlerden biri varsa analiz REDDEDİLİR; kullanıcı etkiyi bilerek
/// `--allow-side-effects` verirse çalışır. Uyarıyla-geçmek yerine reddetmek seçildi: yan etki geri alınamaz,
/// reddin maliyeti bir bayrak eklemek (SqlGuard ile aynı felsefe).
///
/// SINIR: metin tabanlı, parser değil. Kullanıcı tanımlı fonksiyon / tetikleyici / view içindeki yan etkiler
/// ya da INSERT'in tükettiği identity/serial değerleri GÖRÜLEMEZ — bu yüzden README'de "garanti" değil
/// "bilinen belirteçleri yakalar" diye anlatılır. Tahmini plan (analyze kapalı) hiç çalıştırmaz, denetime girmez.
/// </summary>
public static class YanEtkiDenetimi
{
    private static readonly HashSet<string> Pg = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextval", "setval",
        "notify", "pg_notify",
        "dblink", "dblink_exec", "dblink_connect", "dblink_connect_u", "dblink_send_query", "dblink_open",
        "pg_advisory_lock", "pg_advisory_lock_shared", "pg_try_advisory_lock", "pg_try_advisory_lock_shared",
        "set_config",
        "lo_import", "lo_export", "lo_create", "lo_creat", "lo_unlink", "lo_open", "lowrite", "lo_put", "lo_from_bytea",
        "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_switch_wal", "pg_create_restore_point",
    };

    private static readonly HashSet<string> SqlServer = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "execute", "sp_executesql", "xp_cmdshell", "sp_send_dbmail",
        "openrowset", "openquery", "opendatasource", "bulk",
    };

    private static readonly Regex Kelime = new(@"[A-Za-z_][A-Za-z0-9_$]*", RegexOptions.Compiled);

    /// <summary>Sorguda bulunan geri alınamaz yan etki belirteçleri (küçük harf, tekil, sırayla). Yoksa boş.</summary>
    public static IReadOnlyList<string> Bul(string sql, DbProvider provider)
    {
        var temiz = SqlGuard.YorumVeMetinSil(sql);
        var kume = provider == DbProvider.Postgres ? Pg : SqlServer;
        var bulunan = new List<string>();
        var ms = Kelime.Matches(temiz);
        for (int i = 0; i < ms.Count; i++)
        {
            var k = ms[i].Value.ToLowerInvariant();
            if (kume.Contains(k) || (provider == DbProvider.SqlServer && (k.StartsWith("xp_") || k == "next" && SonrakiDeger(ms, i))))
            {
                var ad = k == "next" ? "next value for" : k;
                if (!bulunan.Contains(ad)) bulunan.Add(ad);
            }
        }
        return bulunan;
    }

    // `NEXT VALUE FOR seq`: NEXT tek başına sıradan bir sözcük olabilir (ör. kolon adı), VALUE FOR ile birlikte sequence'tir.
    private static bool SonrakiDeger(MatchCollection ms, int i) =>
        i + 2 < ms.Count
        && ms[i + 1].Value.Equals("value", StringComparison.OrdinalIgnoreCase)
        && ms[i + 2].Value.Equals("for", StringComparison.OrdinalIgnoreCase);

    /// <summary>Analiz reddi mesajı; belirteç yoksa veya izin verildiyse null.</summary>
    public static string? Denetle(string sql, DbProvider provider, bool izinVer)
    {
        if (izinVer) return null;
        var b = Bul(sql, provider);
        if (b.Count == 0) return null;
        return $"explain --analyze ifadeyi gerçekten çalıştırır ve ROLLBACK yapar; ancak rollback şunları GERİ ALMAZ: " +
               $"{string.Join(", ", b)} (sequence ilerlemesi, uzak yazma, NOTIFY, oturum kilidi/ayarı, dış komut). " +
               "Etkiyi bilerek kabul ediyorsan --allow-side-effects ekle; yoksa --analyze'sız tahmini plan al.";
    }
}
