using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlCli.Core;

/// <summary>
/// Sohbetin oturumda öğrendiği gerçeklerin DOĞRULANMAMIŞ aday deposu (~/.sqlcli/sema-adaylari.jsonl).
/// Yalnız bu dosyaya EKLENİR; sema YAML'ına ve iş veritabanına hiçbir yazma yolu yoktur.
/// Terfi pusula'da (sema-ogren / consolidate-sema) kanıtla yapılır.
/// Alan adları pusula'nın docs/sema-adaylari.yaml biçimini izler (id, hedef, gercek, confidence, evidence,
/// kaynak_proje, tarih, ilgili, durum) ki dönüşüm tek küçük betik olsun.
/// Dosya ekleme-yalnız: durum güncellemesi aynı id'li yeni satırdır (sonraki satır alanları ezer).
/// </summary>
public static class SemaCandidates
{
    public const double MaxConfidence = 0.8, MinConfidence = 0.3, DefaultConfidence = 0.5;
    public const int MaxTextLength = 2000, MinTextLength = 10, MaxRelated = 10, MaxPerSession = 20;
    public static readonly string[] Targets = ["bridges", "entities", "codes", "metrics"];

    private static int _addedThisSession;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("SQLCLI_SEMA_CANDIDATES") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlcli", "sema-adaylari.jsonl");

    /// <summary>Aday ekler. Aynı hedef+metin zaten aday ise yenisini yazmaz, mevcut kimliği döner.</summary>
    public static (string Id, bool Duplicate) Add(string target, string fact, string evidence,
        double? confidence, IEnumerable<string>? related, string? profile, string? path = null)
    {
        path ??= DefaultPath;
        target = (target ?? "").Trim().ToLowerInvariant();
        if (!Targets.Contains(target))
            throw new ArgumentException($"target şunlardan biri olmalı: {string.Join(", ", Targets)}.");
        fact = (fact ?? "").Trim();
        evidence = (evidence ?? "").Trim();
        if (fact.Length < MinTextLength || fact.Length > MaxTextLength)
            throw new ArgumentException($"fact {MinTextLength}-{MaxTextLength} karakter olmalı (tek, atomik bir gerçek).");
        if (evidence.Length < MinTextLength || evidence.Length > MaxTextLength)
            throw new ArgumentException($"evidence {MinTextLength}-{MaxTextLength} karakter olmalı: koştuğun sorguyu ve ne gösterdiğini yaz.");
        var conf = Math.Clamp(confidence ?? DefaultConfidence, MinConfidence, MaxConfidence);
        var rel = (related ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Take(MaxRelated)
                                 .Select(x => x.Length > 100 ? x[..100] : x).ToArray();

        if (Interlocked.Increment(ref _addedThisSession) > MaxPerSession)
        {
            Interlocked.Decrement(ref _addedThisSession);
            throw new InvalidOperationException($"Bu oturumda en çok {MaxPerSession} aday eklenebilir.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fileLock = AcquireLock(path + ".lock");
            var existing = ReadAll(path);
            var norm = SemaIndex.Fold(fact);
            var same = existing.FirstOrDefault(c =>
                (c["hedef"]?.ToString() ?? "") == target &&
                (c["durum"]?.ToString() ?? "aday") == "aday" &&
                SemaIndex.Fold(c["gercek"]?.ToString() ?? "") == norm);
            if (same != null)
            {
                Interlocked.Decrement(ref _addedThisSession);
                return (same["id"]!.ToString(), true);
            }

            var gun = DateTime.Now.ToString("yyyyMMdd");
            int sequence = existing.Count(c => (c["id"]?.ToString() ?? "").StartsWith($"aday-{gun}-", StringComparison.Ordinal)) + 1;
            var id = $"aday-{gun}-{sequence:000}";

            var line = new JsonObject
            {
                ["id"] = id,
                ["hedef"] = target,
                ["gercek"] = fact,
                ["confidence"] = conf,
                ["evidence"] = evidence,
                ["kaynak_proje"] = "sqlcli-mcp",
                ["profil"] = profile,
                ["tarih"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["ogren"] = "sqlcli mcp sema_learn",
                ["ilgili"] = new JsonArray(rel.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["durum"] = "aday",
            };
            // JSON satırı tek satırdır (satır sonları \n olarak kaçışlanır): kullanıcı metni dosya biçimini bozamaz.
            File.AppendAllText(path, line.ToJsonString(Json) + "\n", new UTF8Encoding(false));
            return (id, false);
        }
        catch
        {
            Interlocked.Decrement(ref _addedThisSession);
            throw;
        }
    }

    /// <summary>Tüm adaylar; aynı id'li sonraki satırlar alanları ezer. Bozuk satırlar atlanır.</summary>
    public static List<JsonObject> ReadAll(string? path = null)
    {
        path ??= DefaultPath;
        var result = new List<JsonObject>();
        if (!File.Exists(path)) return result;
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            var id = o?["id"]?.ToString();
            if (o == null || string.IsNullOrEmpty(id)) continue;
            if (index.TryGetValue(id, out var previous))
                foreach (var (k, v) in o) previous[k] = v?.DeepClone();
            else { index[id] = o; result.Add(o); }
        }
        return result;
    }

    /// <summary>Adayı arama kaydına çevirir (tür "candidate").</summary>
    public static SemaRecord ToRecord(JsonObject c)
    {
        var d = (JsonObject)c.DeepClone();
        return new SemaRecord("candidate", d["id"]!.ToString(), d, "sema-adaylari.jsonl");
    }

    // Süreçler arası basit kilit: dışlayıcı açılan dosya; kısa yeniden denemeyle bekler.
    private static FileStream AcquireLock(string lockPath)
    {
        for (int i = 0; ; i++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 100) { Thread.Sleep(50); }
        }
    }

    // Testler için: oturum sayacını sıfırlar.
    public static void ResetSessionCounter() => Interlocked.Exchange(ref _addedThisSession, 0);
}
