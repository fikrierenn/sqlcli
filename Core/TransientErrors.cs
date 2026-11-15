using System.Data.Common;

namespace SqlCli.Core;

/// <summary>
/// GEÇİCİ HATA SINIFLANDIRICI — hangi hata yeniden denenir, hangisi denenmez.
///
/// NEDEN VAR: uzak ERP sunucusuna açılan bağlantı ağ/oturum sebebiyle düşebilir
/// (10053 "existing connection was forcibly closed", 10060 timeout, -2 komut zaman aşımı).
/// Bunlar tek denemede çöken bir CLI'ı gereksiz yere durdurur. Buna karşılık
/// "Invalid column name" (207) veya izin hatası SONSUZA KADAR aynı kalır — onu denemek
/// yalnız zaman kaybı ve gürültü.
///
/// İLKE: retry yalnız geçici + SINIRLI (max 2 ek deneme) + her deneme GÖRÜNÜR.
/// Sessiz retry, "yavaş ama çalışıyor" görünen bir sistemin gerçekte kopuk olduğunu saklar.
/// </summary>
public static class TransientErrors
{
    // SQL Server hata numaraları (geçici sınıf).
    private static readonly int[] TransientCodes =
    [
        -2,      // komut zaman aşımı (client)
        20,      // instance'a bağlanılamadı
        64,      // oturum kurulurken bağlantı kapandı
        233,     // named pipe / transport
        1205,    // deadlock kurbanı
        4060,    // DB açılamadı (geçici olabilir)
        10053,   // bağlantı zorla kapatıldı
        10054,   // karşı taraf sıfırladı
        10060,   // ağ zaman aşımı
        10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920, // Azure throttle/failover
    ];

    public static bool IsTransient(DbException ex)
    {
        // Sağlayıcıdan bağımsız kod okuma (Dialects.ErrorCode aynı işi UI için yapıyor).
        var kod = FindCode(ex);
        if (kod.HasValue && Array.IndexOf(TransientCodes, kod.Value) >= 0) return true;

        // Kod okunamadığında mesaja bak — ağ sınıfı ifadeler geçici sayılır.
        var m = ex.Message.ToLowerInvariant();
        return m.Contains("transport-level") || m.Contains("forcibly closed")
            || m.Contains("timeout expired") || m.Contains("network-related")
            || m.Contains("connection was successfully established") // login sonrası kopma
            || m.Contains("semaphore timeout");
    }

    private static int? FindCode(DbException ex)
    {
        var t = ex.GetType();
        var p = t.GetProperty("Number") ?? t.GetProperty("ErrorCode");
        if (p?.GetValue(ex) is int n) return n;
        return null;
    }

    /// <summary>
    /// İşi en fazla (1 + ekDeneme) kez çalıştırır; yalnız geçici hatada bekleyip yeniden dener.
    /// Her yeniden deneme <paramref name="bildir"/> ile GÖRÜNÜR kılınır (sessiz retry yok).
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> is_, int extraAttempts, Action<string> notify)
    {
        DbException? last = null;
        for (int attempt = 0; attempt <= Math.Max(0, extraAttempts); attempt++)
        {
            try
            {
                if (attempt > 0)
                    notify($"geçici hata sonrası {attempt}. yeniden deneme…");
                return await is_();
            }
            catch (DbException ex) when (IsTransient(ex) && attempt < extraAttempts)
            {
                last = ex;
                notify($"GEÇİCİ HATA ({FindCode(ex)?.ToString() ?? "?"}): {ex.Message.Split('\n')[0]}");
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
            }
        }
        if (last != null) throw last;
        throw new InvalidOperationException("YenidenDeneAsync: beklenmeyen akış");
    }
}
