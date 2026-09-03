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
    private static readonly int[] GeciciKodlar =
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

    public static bool Gecici(DbException ex)
    {
        // Sağlayıcıdan bağımsız kod okuma (Dialects.ErrorCode aynı işi UI için yapıyor).
        var kod = KodBul(ex);
        if (kod.HasValue && Array.IndexOf(GeciciKodlar, kod.Value) >= 0) return true;

        // Kod okunamadığında mesaja bak — ağ sınıfı ifadeler geçici sayılır.
        var m = ex.Message.ToLowerInvariant();
        return m.Contains("transport-level") || m.Contains("forcibly closed")
            || m.Contains("timeout expired") || m.Contains("network-related")
            || m.Contains("connection was successfully established") // login sonrası kopma
            || m.Contains("semaphore timeout");
    }

    private static int? KodBul(DbException ex)
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
    public static async Task<T> YenidenDeneAsync<T>(
        Func<Task<T>> is_, int ekDeneme, Action<string> bildir)
    {
        DbException? son = null;
        for (int deneme = 0; deneme <= Math.Max(0, ekDeneme); deneme++)
        {
            try
            {
                if (deneme > 0)
                    bildir($"geçici hata sonrası {deneme}. yeniden deneme…");
                return await is_();
            }
            catch (DbException ex) when (Gecici(ex) && deneme < ekDeneme)
            {
                son = ex;
                bildir($"GEÇİCİ HATA ({KodBul(ex)?.ToString() ?? "?"}): {ex.Message.Split('\n')[0]}");
                await Task.Delay(TimeSpan.FromSeconds(2 * (deneme + 1)));
            }
        }
        if (son != null) throw son;
        throw new InvalidOperationException("YenidenDeneAsync: beklenmeyen akış");
    }
}
