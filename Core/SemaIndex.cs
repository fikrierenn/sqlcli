using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace SqlCli.Core;

/// <summary>
/// Pusula semantik katmanından (entities/bridges/codes/metrics/queries .yaml) tek bir sema kaydı.
/// Kayıt YALNIZ OKUNUR; sema klasörüne hiçbir yerde yazılmaz (pusula tek yazardır).
/// </summary>
public sealed class SemaRecord
{
    public string Kind { get; }          // entity | bridge | code | metric | query | candidate
    public string Id { get; }
    public JsonObject Data { get; }
    public string File { get; }

    // Aramada kullanılan, Türkçe-duyarsız katlanmış metinler (SemaIndex.Fold).
    public string IdFolded { get; }
    public string Head { get; }          // id + başlık benzeri alanlar + kısa not
    public string Body { get; }          // tüm skaler değerler ve anahtarlar
    public string? ScopeFolded { get; }

    public double? Confidence { get; }
    public DateOnly? LastVerified { get; }
    public int? TtlDays { get; }

    private static readonly string[] HeadFields =
        ["title", "question", "from", "to", "type", "grain", "scope", "metrics", "hedef", "key"];

    public SemaRecord(string kind, string id, JsonObject data, string file)
    {
        Kind = kind; Id = id; Data = data; File = file;
        IdFolded = SemaIndex.Fold(id);

        var head = new StringBuilder(IdFolded).Append(' ');
        foreach (var f in HeadFields) if (data[f] is { } n) { SemaIndex.Flatten(n, head); }
        if (data["note"] is JsonValue nv) head.Append(' ').Append(Truncate(nv.ToString(), 300));
        if (data["gercek"] is JsonValue gv) head.Append(' ').Append(Truncate(gv.ToString(), 300));
        Head = SemaIndex.Fold(head.ToString());

        var body = new StringBuilder();
        SemaIndex.Flatten(data, body);
        Body = SemaIndex.Fold(body.ToString());

        ScopeFolded = data["scope"] is JsonValue sv ? SemaIndex.Fold(sv.ToString()) : null;
        Confidence = Num(data["confidence"]);
        LastVerified = Date(data["last_verified"]) ?? Date(data["tarih"]);
        TtlDays = Num(data["ttl_days"]) is { } t ? (int)t : null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static DateOnly? Date(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        var s = v.ToString();
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    /// <summary>
    /// Bayatlık (sema/README.md § Decay ile aynı kural): confidence 1.0 muaf; ttl_days yoksa
    /// confidence'tan türetilir (≥0.9→180, ≥0.5→90, altı→30; confidence yoksa 180 gün).
    /// Bayat = otomatik aksiyon değil, "canlı sorguyla yeniden doğrula" bayrağı.
    /// </summary>
    public string Freshness(DateOnly today)
    {
        if (Kind == "candidate") return "UNVERIFIED CANDIDATE";
        if (Confidence is >= 1.0) return "permanent";
        if (LastVerified is null) return "undated";
        int ttl = TtlDays ?? (Confidence switch { null => 180, >= 0.9 => 180, >= 0.5 => 90, _ => 30 });
        var due = LastVerified.Value.AddDays(ttl);
        return due < today
            ? $"STALE ({today.DayNumber - due.DayNumber} days past re-verification date; confirm with sql_query before relying on it)"
            : "fresh";
    }

    /// <summary>Arama listesinde gösterilen tek satırlık özet.</summary>
    public string Summary()
    {
        string? Get(string k) => Data[k] is JsonValue v ? v.ToString() : null;
        string s = Kind switch
        {
            "bridge" => $"{Get("from")} -> {Get("to")}" + (Get("note") is { } n ? " | " + n : ""),
            "query" => Get("question") ?? "",
            "metric" => Get("title") ?? Get("note") ?? "",
            "candidate" => Get("gercek") ?? "",
            _ => Get("note") ?? Get("grain") ?? Get("type") ?? "",
        };
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= 220 ? s : s[..220] + "…";
    }

    /// <summary>`uses` alanındaki "tür:id" başvuruları (çapraz referans).</summary>
    public IEnumerable<(string Kind, string Id)> Uses()
    {
        if (Data["uses"] is not JsonArray arr) yield break;
        foreach (var x in arr)
        {
            if (x is not JsonValue v) continue;
            var s = v.ToString();
            int i = s.IndexOf(':');
            if (i <= 0) continue;
            yield return (SemaIndex.NormalizeKind(s[..i]) ?? s[..i], s[(i + 1)..]);
        }
    }
}

public sealed record SemaHit(SemaRecord Record, int Score);

/// <summary>
/// Pusula sema klasörünün SALT-OKUNUR bellek içi dizini. ~540 kayıt: doğrusal tarama milisaniye sürer,
/// ayrı bir veritabanı gerekmez. Dosya tarihi değişince bir sonraki çağrıda yeniden yüklenir.
/// Model yol veremez: yalnız aşağıdaki SABİT dosya adları okunur.
/// </summary>
public sealed class SemaIndex
{
    private static readonly (string File, string Kind, string Key)[] Sources =
    [
        ("entities.yaml", "entity", "entities"),
        ("bridges.yaml", "bridge", "bridges"),
        ("codes.yaml", "code", "codes"),
        ("metrics.yaml", "metric", "metrics"),
        ("queries.yaml", "query", "queries"),
    ];

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "ve", "ile", "bir", "bu", "icin", "nedir", "nasil", "hangi", "olan", "the", "and", "for", "what", "how", "ne", "mi", "mu",
    };

    private readonly object _lock = new();
    private string _signature = "";
    private List<SemaRecord> _records = [];

    public string Dir { get; }
    public List<string> LoadErrors { get; private set; } = [];
    public List<string> AiHints { get; private set; } = [];
    public JsonObject? Databases { get; private set; }
    public IReadOnlyList<SemaRecord> Records { get { Refresh(); return _records; } }

    private SemaIndex(string dir) { Dir = dir; }

    public static SemaIndex Open(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Sema klasörü yok: {full}");
        if (!Sources.Any(s => File.Exists(Path.Combine(full, s.File))))
            throw new FileNotFoundException($"Klasörde sema dosyası (entities/bridges/codes/metrics/queries .yaml) yok: {full}");
        return new SemaIndex(full);
    }

    // ── Yükleme ──────────────────────────────────────────────────────────

    public void Refresh()
    {
        lock (_lock)
        {
            var sig = string.Join('|', Sources.Select(s =>
            {
                var p = Path.Combine(Dir, s.File);
                return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString() : "0";
            }));
            if (sig == _signature) return;

            var records = new List<SemaRecord>();
            var errors = new List<string>();
            var hints = new List<string>();
            JsonObject? databases = null;
            var deserializer = new DeserializerBuilder().Build();

            foreach (var (file, kind, key) in Sources)
            {
                var path = Path.Combine(Dir, file);
                if (!File.Exists(path)) continue;
                try
                {
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        text = sr.ReadToEnd();

                    var root = deserializer.Deserialize<object>(new StringReader(text)) as IDictionary<object, object>
                        ?? throw new InvalidDataException("kök bir harita değil");

                    if (kind == "entity")
                    {
                        if (root.TryGetValue("ai_hints", out var h) && h is IList<object> hl)
                            hints.AddRange(hl.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
                        if (root.TryGetValue("databases", out var d) && ToNode(d) is JsonObject dn) databases = dn;
                    }

                    if (!root.TryGetValue(key, out var section)) continue;
                    LoadSection(records, kind, file, section);
                }
                catch (Exception ex)
                {
                    errors.Add($"{file} okunamadı: {ex.Message}");
                }
            }

            _records = records;
            AiHints = hints;
            Databases = databases;
            LoadErrors = errors;
            // Hata varsa imzayı saklama: bir sonraki çağrıda yeniden denensin (yazım sırasında yarım dosya olabilir).
            _signature = errors.Count == 0 ? sig : "";
        }
    }

    private static void LoadSection(List<SemaRecord> into, string kind, string file, object? section)
    {
        if (section is IDictionary<object, object> map)
        {
            foreach (var (k, v) in map)
            {
                var id = k?.ToString() ?? "";
                if (id.Length == 0) continue;
                var obj = new JsonObject { ["id"] = id };
                if (ToNode(v) is JsonObject vo)
                    foreach (var (pk, pv) in vo) { if (pk != "id") obj[pk] = pv?.DeepClone(); }
                else obj["value"] = ToNode(v);
                into.Add(new SemaRecord(kind, id, obj, file));
            }
        }
        else if (section is IList<object> list)
        {
            int i = 0;
            foreach (var item in list)
            {
                i++;
                if (ToNode(item) is not JsonObject o) continue;
                var id = o["id"] is JsonValue iv ? iv.ToString() : $"{kind}-{i}";
                into.Add(new SemaRecord(kind, id, o, file));
            }
        }
    }

    // YAML ağacı → JsonNode. Skalerler YamlDotNet'ten string gelir; sayı/bool olanlar tipine çevrilir
    // (baştaki sıfırlı değerler string kalır, kimlik/kod bozulmasın).
    internal static JsonNode? ToNode(object? o) => o switch
    {
        null => null,
        IDictionary<object, object> d => ToObject(d),
        IList<object> l => new JsonArray(l.Select(ToNode).ToArray()),
        string s => Scalar(s),
        _ => JsonValue.Create(o.ToString()),
    };

    private static JsonObject ToObject(IDictionary<object, object> d)
    {
        var r = new JsonObject();
        foreach (var (k, v) in d) r[k?.ToString() ?? ""] = ToNode(v);
        return r;
    }

    private static JsonNode Scalar(string s)
    {
        if (s == "true") return JsonValue.Create(true);
        if (s == "false") return JsonValue.Create(false);
        if (Regex.IsMatch(s, @"^-?(0|[1-9]\d{0,14})$")) return JsonValue.Create(long.Parse(s));
        if (Regex.IsMatch(s, @"^-?(0|[1-9]\d{0,14})\.\d{1,10}$"))
            return JsonValue.Create(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        return JsonValue.Create(s)!;
    }

    internal static void Flatten(JsonNode? n, StringBuilder sb)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var kv in o) { sb.Append(kv.Key).Append(' '); Flatten(kv.Value, sb); }
                break;
            case JsonArray a:
                foreach (var x in a) Flatten(x, sb);
                break;
            case JsonValue v:
                sb.Append(v.ToString()).Append(' ');
                break;
        }
    }

    // ── Normalleştirme ───────────────────────────────────────────────────

    /// <summary>Küçük harfe indirir ve Türkçe harfleri ASCII karşılığına katlar (İ/ı/I→i, ş→s, ğ→g, ü→u, ö→o, ç→c).</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' or 'i' => 'i',
                'Ş' or 'ş' => 's',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                _ => char.ToLowerInvariant(ch),
            });
        return sb.ToString();
    }

    public static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "entity" or "entities" => "entity",
        "bridge" or "bridges" => "bridge",
        "code" or "codes" => "code",
        "metric" or "metrics" => "metric",
        "query" or "queries" => "query",
        "candidate" or "candidates" or "aday" => "candidate",
        _ => throw new ArgumentException("kind şunlardan biri olmalı: entity, bridge, code, metric, query, candidate, all."),
    };

    private static List<string> Tokens(string folded) =>
        Regex.Split(folded, @"[^a-z0-9_]+")
             .Where(t => (t.Length >= 3 || (t.Length == 2 && t.Any(char.IsDigit))) && !Stop.Contains(t))
             .Distinct().ToList();

    // ── Arama ────────────────────────────────────────────────────────────

    /// <param name="dbKey">Oturumun veritabanı adı; eşleşen kayıtlara küçük bir öncelik verir (gizleme yok).</param>
    public List<SemaHit> Search(string query, string? kind, int max, string? dbKey = null,
                                IEnumerable<SemaRecord>? extra = null)
    {
        var qf = Fold(query.Trim());
        var tokens = Tokens(qf);
        if (tokens.Count == 0 && qf.Length == 0) return [];
        if (tokens.Count == 0) tokens.Add(qf);
        var dbf = string.IsNullOrWhiteSpace(dbKey) ? null : Fold(dbKey);
        int need = tokens.Count == 1 ? 1 : (tokens.Count + 1) / 2;

        IEnumerable<SemaRecord> all = Records;
        if (extra != null) all = all.Concat(extra);

        var hits = new List<SemaHit>();
        foreach (var r in all)
        {
            if (kind != null && r.Kind != kind) continue;

            int score = 0, matched = 0;
            if (r.IdFolded == qf) score += 50;
            else if (r.IdFolded.Contains(qf, StringComparison.Ordinal)) score += 15;

            foreach (var t in tokens)
            {
                var stem = t.Length >= 6 ? t[..^2] : null;   // "cirosu" → "ciro" (kaba Türkçe ek payı)
                int s = 0;
                if (r.IdFolded.Contains(t, StringComparison.Ordinal)) s = 6;
                else if (r.Head.Contains(t, StringComparison.Ordinal)) s = 3;
                else if (r.Body.Contains(t, StringComparison.Ordinal)) s = 1;
                else if (stem != null && r.Head.Contains(stem, StringComparison.Ordinal)) s = 2;
                else if (stem != null && r.Body.Contains(stem, StringComparison.Ordinal)) s = 1;
                if (s > 0) { matched++; score += s; }
            }
            if (matched < need) continue;
            if (matched == tokens.Count) score += 5;
            if (dbf != null && ((r.ScopeFolded != null && dbf.Contains(r.ScopeFolded, StringComparison.Ordinal))
                                || r.IdFolded.Contains(dbf, StringComparison.Ordinal)))
                score += 2;
            hits.Add(new SemaHit(r, score));
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Record.Id, StringComparer.Ordinal)
                   .Take(max).ToList();
    }

    /// <summary>Tür + kimlik (büyük/küçük harf ve Türkçe harf duyarsız) ile tek kayıt.</summary>
    public SemaRecord? Get(string? kind, string id, IEnumerable<SemaRecord>? extra = null)
    {
        var f = Fold(id.Trim());
        IEnumerable<SemaRecord> all = Records;
        if (extra != null) all = all.Concat(extra);
        return all.FirstOrDefault(r => (kind == null || r.Kind == kind) && r.IdFolded == f);
    }
}
