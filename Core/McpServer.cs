using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlCli.Core;

/// <summary>
/// `sqlcli mcp` — stdio MCP (Model Context Protocol) sunucusu. Claude Desktop/Code gibi istemciler
/// veritabanını YALNIZ OKUMA araçlarıyla sorgular.
///
/// ==== GÜVENLİK KATMANLARI (hepsi birlikte; biri tek başına yetmez) ====
///  1. Salt-okuma muhafızı HER ZAMAN açık: SqlGuard izin listesi (yalnız SELECT/WITH, tek ifade).
///     Model bunu kapatamaz — araçlarda böyle bir parametre yok.
///  2. Bağlantı sunucu başlarken sabitlenir (--profile / --conn / ortam). Model bağlantı seçemez,
///     bağlantı dizesini göremez (connection_info şifre içermez).
///  3. Her sorgu transaction içinde koşar ve SONUNDA ROLLBACK edilir (muhafızı aşan bir yazma
///     yolu olsa bile kalıcı olmaz).
///  4. Satır tavanı (varsayılan 200, en çok 5000), çıktı karakter tavanı ve komut zaman aşımı.
///  5. Her çağrı ~/.sqlcli/history.log'a "mcp:" önekli profille yazılır (denetim izi).
///  6. --sema <klasör> verilirse pusula semantik katmanı SALT-OKUNUR araçlarla açılır (sema_*); tek yazan araç
///     sema_learn yalnız ~/.sqlcli/sema-adaylari.jsonl'e DOĞRULANMAMIŞ aday ekler (bkz. McpSemaTools).
///  ASIL SINIR veritabanı yetkisidir: bağlanan kullanıcıya yalnız db_datareader verin.
///
/// stdout YALNIZ JSON-RPC içindir; tanı/günlük stderr'e gider (aksi halde istemci protokolü bozulur).
/// </summary>
public static class McpServer
{
    private const int DefaultRows = 200, MaxRows = 5000;
    private const int DefaultTimeoutSeconds = 30, MaxTimeoutSeconds = 120;
    private const int OutputCharCap = 60_000;

    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string? _conn, _profile, _config, _semaDir;
    private static SemaIndex? _sema;
    private static ConnectionResolver.ResolvedConnection? _resolved;
    private static Stream _stdout = Stream.Null;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static async Task<int> RunAsync(string[] args)
    {
        // stdout'u protokole ayır; Console.Out'a düşen her şey (Spectre dahil) stderr'e gitsin.
        _stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Length) _profile = args[++i];
            else if (args[i] == "--conn" && i + 1 < args.Length) _conn = args[++i];
            else if (args[i] == "--config" && i + 1 < args.Length) _config = args[++i];
            else if (args[i] == "--sema" && i + 1 < args.Length) _semaDir = args[++i];
        }

        // MCP istemcisi sunucuyu rastgele bir çalışma dizininde başlatır; sqlcli.json ve .env ise
        // çalışma dizininden YUKARI doğru aranır. --config verilen klasöre geçerek ikisi de bulunur
        // (şifre yine config JSON'a değil, o klasördeki .env'ye / ortam değişkenine konur).
        if (_config != null)
        {
            var dir = File.Exists(_config) ? Path.GetDirectoryName(Path.GetFullPath(_config))! : _config;
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"[sqlcli mcp] --config klasörü yok: {dir}");
                return 2;
            }
            Directory.SetCurrentDirectory(dir);
        }
        // Sema araçları yalnız --sema verilirse açılır (salt-okuma; pusula semantik katmanı). Yol, --config'in
        // çalışma dizini değişikliğinden ÖNCE mutlak yola çevrilir.
        if (_semaDir != null)
        {
            try { _sema = SemaIndex.Open(_semaDir); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[sqlcli mcp] --sema açılamadı: {ex.Message}");
                return 2;
            }
        }
        Console.Error.WriteLine($"[sqlcli mcp] v{VersionGuard.CurrentText} başladı (salt-okuma; profil={_profile ?? "(varsayılan)"})");

        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await input.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(RpcError(null, -32700, "Parse error"));
                continue;
            }
            if (request is not JsonObject obj) { await WriteAsync(RpcError(null, -32600, "Invalid Request")); continue; }

            var response = await HandleAsync(obj);
            if (response != null) await WriteAsync(response);
        }
        return 0;
    }

    // ── JSON-RPC ─────────────────────────────────────────────────────────

    private static async Task<JsonObject?> HandleAsync(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"] as JsonObject;

        // Bildirim (id yok): yanıt verilmez.
        if (id == null) return null;
        if (method == null) return RpcError(id, -32600, "Invalid Request");

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    var version = requested != null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
                    return RpcResult(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "sqlcli", ["version"] = VersionGuard.CurrentText },
                        ["instructions"] =
                            "Read-only SQL database access (SQL Server / PostgreSQL). Only SELECT/WITH statements are allowed; " +
                            "everything else is rejected. Explore with list_tables / search_schema / describe_table first, " +
                            "then use sql_query. Results are capped (default 200 rows); filter and aggregate in SQL." +
                            (_sema != null ? McpSemaTools.InstructionsExtra(_sema) : ""),
                    });
                }
                case "ping": return RpcResult(id, new JsonObject());
                case "tools/list": return RpcResult(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call":
                {
                    var name = parameters?["name"]?.GetValue<string>() ?? "";
                    return RpcResult(id, await CallToolAsync(name, parameters?["arguments"] as JsonObject ?? new JsonObject()));
                }
                case "resources/list": return RpcResult(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list": return RpcResult(id, new JsonObject { ["prompts"] = new JsonArray() });
                default: return RpcError(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sqlcli mcp] {method}: {ex}");
            return RpcError(id, -32603, ex.Message);
        }
    }

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(JsonObject obj)
    {
        var payload = Encoding.UTF8.GetBytes(obj.ToJsonString(JsonOptions) + "\n");
        await WriteLock.WaitAsync();
        try { await _stdout.WriteAsync(payload); await _stdout.FlushAsync(); }
        finally { WriteLock.Release(); }
    }

    // ── Araç tanımları ───────────────────────────────────────────────────

    internal static JsonObject Schema(JsonObject properties, params string[] requiredNames) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(requiredNames.Select(z => (JsonNode)JsonValue.Create(z)!).ToArray()),
    };

    internal static JsonObject Property(string type, string description) => new() { ["type"] = type, ["description"] = description };

    // dosyaYazar: veritabanına değil yalnız dışa aktarma klasörüne dosya yazan araç (export_query).
    internal static JsonObject Tool(string name, string description, JsonObject sema, bool writesFile = false) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = sema,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = !writesFile, ["destructiveHint"] = false, ["idempotentHint"] = !writesFile, ["openWorldHint"] = false,
        },
    };

    private static JsonArray ToolList() => AddSemaTools(new(
        Tool("sql_query",
            "Run ONE read-only SQL statement (SELECT or WITH…SELECT). Anything else (INSERT/UPDATE/DELETE/EXEC/DDL, multiple statements, SELECT…INTO) is rejected. " +
            "Rows are capped; use WHERE/aggregation. Returns {columns, rows, rowCount, truncated}.",
            Schema(new JsonObject
            {
                ["sql"] = Property("string", "The SELECT statement."),
                ["max_rows"] = Property("integer", $"Row cap (default {DefaultRows}, max {MaxRows})."),
                ["timeout_seconds"] = Property("integer", $"Command timeout (default {DefaultTimeoutSeconds}, max {MaxTimeoutSeconds})."),
            }, "sql")),
        Tool("list_tables",
            "List base tables (schema, table). Optional name filter; optional approximate row counts.",
            Schema(new JsonObject
            {
                ["filter"] = Property("string", "Name pattern, e.g. 'STK' or 'STK%'. Without % it matches as a substring."),
                ["with_row_counts"] = Property("boolean", "Include approximate row counts (from catalog statistics)."),
                ["max_rows"] = Property("integer", "Row cap (default 200)."),
            })),
        Tool("search_schema",
            "Find tables AND columns whose name matches a pattern (substring unless % is given).",
            Schema(new JsonObject { ["pattern"] = Property("string", "Name pattern.") }, "pattern")),
        Tool("describe_table",
            "Columns (type, nullable, default, PK), indexes, foreign keys and approximate row count of one table.",
            Schema(new JsonObject { ["table"] = Property("string", "'schema.table' or 'table'.") }, "table")),
        Tool("table_relationships",
            "Outgoing and incoming foreign keys of one table.",
            Schema(new JsonObject { ["table"] = Property("string", "'schema.table' or 'table'.") }, "table")),
        Tool("sample_rows",
            "First N rows of a table (default 10, max 100) — to see what the data looks like.",
            Schema(new JsonObject
            {
                ["table"] = Property("string", "'schema.table' or 'table'."),
                ["n"] = Property("integer", "Number of rows (default 10, max 100)."),
            }, "table")),
        Tool("explain_query",
            "Estimated execution plan of a read-only query (the query is NOT executed).",
            Schema(new JsonObject { ["sql"] = Property("string", "The SELECT statement.") }, "sql")),
        Tool("profiles",
            "Names of the connection profiles defined in sqlcli.json and the one this session is pinned to (no connection strings, no secrets).",
            Schema(new JsonObject())),
        Tool("export_query",
            "Run ONE read-only SELECT and save the full result as CSV or JSON in the fixed exports folder (Documents\\mcp\\exports). " +
            "You give only a file name hint (no path); returns the saved path, row count and whether the row cap truncated it.",
            Schema(new JsonObject
            {
                ["sql"] = Property("string", "The SELECT statement."),
                ["format"] = Property("string", "'csv' (default) or 'json'."),
                ["file_name"] = Property("string", "Name hint, letters/digits/_/- only; a timestamp and extension are added."),
                ["max_rows"] = Property("integer", $"Row cap (default {DefaultExportRows}, max {MaxExportRows})."),
            }, "sql"), writesFile: true),
        Tool("connection_info",
            "Which server / database / login this session is connected as, and whether that login has write rights (never returns secrets).",
            Schema(new JsonObject()))));

    private static JsonArray AddSemaTools(JsonArray list)
    {
        if (_sema != null) foreach (var t in McpSemaTools.Definitions()) list.Add(t);
        return list;
    }

    // ── Araç yürütme ─────────────────────────────────────────────────────

    private static async Task<JsonObject> CallToolAsync(string name, JsonObject a)
    {
        try
        {
            if (McpSemaTools.IsSemaTool(name) && _sema == null)
                throw new InvalidOperationException("Sema araçları kapalı: sunucu --sema <klasör> ile başlatılmadı.");
            var text = name switch
            {
                _ when McpSemaTools.IsSemaTool(name) => CallSemaTool(name, a),
                "sql_query" => await QueryAsync(Str(a, "sql"), Int(a, "max_rows", DefaultRows, 1, MaxRows),
                                                Int(a, "timeout_seconds", DefaultTimeoutSeconds, 1, MaxTimeoutSeconds), "sql_query"),
                "list_tables" => await ListTablesAsync(a),
                "search_schema" => await SearchSchemaAsync(Str(a, "pattern")),
                "describe_table" => await DescribeTableAsync(Str(a, "table")),
                "table_relationships" => await RelationshipsAsync(Str(a, "table")),
                "sample_rows" => await SampleRowsAsync(Str(a, "table"), Int(a, "n", 10, 1, 100)),
                "explain_query" => await ExplainQueryAsync(Str(a, "sql")),
                "profiles" => ProfilesJson(),
                "export_query" => await ExportAsync(a),
                "connection_info" => await ConnectionInfoAsync(),
                _ => throw new ArgumentException($"Bilinmeyen araç: {name}"),
            };
            return Content(text, false);
        }
        catch (Exception ex)
        {
            // Veritabanı/doğrulama hatası protokol hatası değil, araç hatasıdır: model görüp düzeltsin.
            Console.Error.WriteLine($"[sqlcli mcp] {name} hata: {ex.Message}");
            return Content(MaskSecrets(ex.Message), true);
        }
    }

    // Sema araçları: salt-okuma + aday ekleme. Denetim izi "mcp-sema:" önekiyle history.log'a yazılır.
    private static string CallSemaTool(string name, JsonObject a)
    {
        var result = McpSemaTools.Run(name, a, _sema!, SessionDatabase(), _profile);
        ConfigStore.AppendHistory($"{name} {a.ToJsonString()}", $"mcp-sema:{_profile ?? "-"}");
        return result;
    }

    // Oturumun veritabanı adı (yalnız ad; bağlantı dizesi hiçbir yere çıkmaz). Bağlantı yoksa null.
    private static string? SessionDatabase()
    {
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = GetConnection().Effective };
            foreach (var k in new[] { "Initial Catalog", "Database" })
                if (b.TryGetValue(k, out var v) && v is string s && s.Length > 0) return s;
        }
        catch { /* bağlantı çözülemedi: sema araçları öncelik vermeden çalışır */ }
        return null;
    }

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static string Str(JsonObject a, string name) =>
        a[name]?.GetValue<string>() is { Length: > 0 } s ? s : throw new ArgumentException($"'{name}' parametresi zorunlu.");

    private static int Int(JsonObject a, string name, int defaultValue, int min, int max)
    {
        if (a[name] == null) return defaultValue;
        var v = a[name]!.GetValue<int>();
        return Math.Clamp(v, min, max);
    }

    private static ConnectionResolver.ResolvedConnection GetConnection()
    {
        if (_resolved != null) return _resolved;
        var r = ConnectionResolver.Resolve(_conn, _profile)
            ?? throw new InvalidOperationException(
                "Bağlantı bilgisi bulunamadı. SQLCLI_CONN ortam değişkeni, sqlcli.json profili veya --profile verin.");
        return _resolved = r;
    }

    // Sorgu: muhafız → LIMIT sarmalı → transaction (ROLLBACK) → satır/karakter tavanı.
    private static async Task<string> QueryAsync(string sql, int maxRows, int timeout, string source)
    {
        var rejection = SqlGuard.Check(sql, true);
        if (rejection != null) throw new InvalidOperationException(rejection);
        var r = GetConnection();
        var json = await ExecuteAsync(r, CappedSql(r.Dialect, sql, maxRows), maxRows, timeout);
        ConfigStore.AppendHistory(sql, $"mcp:{source}:{r.ProfileName}");
        return json;
    }

    // Satır tavanı SQL'i: sunucuya maxRows+1 satır sorulur. Okuyucu (maxRows+1)'inci satırı görürse sonucun
    // KESİLDİĞİNİ bilir; yalnız maxRows sorulsaydı fazladan satır hiç gelmez ve "truncated" hep false kalırdı
    // (2026-11-15 denetiminde 200/5 satırda takılan milyonluk sorguda gözlendi).
    public static string CappedSql(ISqlDialect d, string sql, int maxRows) => d.WrapLimit(sql, maxRows + 1);

    // Katalog sorguları dialect'in kendi (güvenilir) SQL'idir; kullanıcı girdisi SQL içinde yalnız
    // dialect'in kaçışladığı yerlerde geçer. Yine de aynı transaction+tavan hattından geçer.
    private static async Task<string> CatalogAsync(string sql, int maxRows = MaxRows)
    {
        var r = GetConnection();
        return await ExecuteAsync(r, sql, maxRows, DefaultTimeoutSeconds);
    }

    private static async Task<string> ExecuteAsync(
        ConnectionResolver.ResolvedConnection r, string sql, int maxRows, int timeout)
    {
        var (columns, rows, truncated) = await RunRawAsync(r, sql, maxRows, timeout);
        return Serialize(columns, rows, truncated);
    }

    // Ham sonuç: transaction + ROLLBACK + satır tavanı. Hem JSON çıktısı hem dışa aktarma kullanır.
    private static async Task<(List<string> Columns, List<object?[]> Rows, bool Truncated)> RunRawAsync(
        ConnectionResolver.ResolvedConnection r, string sql, int maxRows, int timeout)
    {
        var columns = new List<string>();
        var rows = new List<object?[]>();
        bool truncated = false;

        await using var conn = await ConnectionResolver.OpenAsync(r.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeout;
            await using var reader = await cmd.ExecuteReaderAsync();
            for (int i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            while (await reader.ReadAsync())
            {
                if (rows.Count >= maxRows) { truncated = true; break; }
                var s = new object?[reader.FieldCount];
                reader.GetValues(s!);
                rows.Add(s.Select(x => x is DBNull ? null : x).ToArray());
            }
        }
        finally
        {
            try { await tx.RollbackAsync(); } catch { /* bağlantı zaten kopmuş olabilir */ }
        }

        return (columns, rows, truncated);
    }

    // ── Dışa aktarma ─────────────────────────────────────────────────────
    // Dosya YALNIZ sabit dışa aktarma klasörüne yazılır; model yalnız bir ad ipucu verir
    // (yol değil), uzantıyı biz koyarız, mevcut dosya ezilmez (zaman damgası eklenir).

    private const int DefaultExportRows = 10_000, MaxExportRows = 100_000;

    private static string ExportFolder() =>
        Environment.GetEnvironmentVariable("SQLCLI_MCP_EXPORT_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "mcp", "exports");

    private static async Task<string> ExportAsync(JsonObject a)
    {
        var sql = Str(a, "sql");
        var format = (a["format"]?.GetValue<string>() ?? "csv").ToLowerInvariant();
        if (format is not ("csv" or "json"))
            throw new ArgumentException("format yalnız 'csv' veya 'json' olabilir.");
        var name = a["file_name"]?.GetValue<string>() ?? "export";
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,64}$"))
            throw new ArgumentException("file_name yalnız harf/rakam/_/- içerebilir (en çok 64); yol ve uzantı verilmez.");
        int maxRows = Int(a, "max_rows", DefaultExportRows, 1, MaxExportRows);

        var rejection = SqlGuard.Check(sql, true);
        if (rejection != null) throw new InvalidOperationException(rejection);
        var r = GetConnection();
        var (columns, rows, truncated) = await RunRawAsync(
            r, CappedSql(r.Dialect, sql, maxRows), maxRows, MaxTimeoutSeconds);
        ConfigStore.AppendHistory(sql, $"mcp:export_query:{r.ProfileName}");

        var folder = Path.GetFullPath(ExportFolder());
        Directory.CreateDirectory(folder);
        var path = Path.GetFullPath(Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.{format}"));
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dışa aktarma yolu izinli klasörün dışına çıkıyor — reddedildi.");

        if (format == "json")
        {
            await File.WriteAllTextAsync(path, Serialize(columns, rows, truncated, uncapped: true), new UTF8Encoding(false));
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(CsvField)));
            foreach (var s in rows) sb.AppendLine(string.Join(",", s.Select(x => CsvField(CsvValue(x)))));
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel Türkçe karakter
        }

        return JsonSerializer.Serialize(new
        {
            path = path, format = format, rowCount = rows.Count, truncated = truncated,
            note = truncated ? "Satır tavanına takıldı; WHERE/TOP ile daraltın veya max_rows artırın." : null,
        }, JsonOptions);
    }

    private static string CsvValue(object? d) => d switch
    {
        null => "",
        byte[] b => Convert.ToHexString(b),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => d.ToString() ?? "",
    };

    private static string CsvField(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string ProfilesJson()
    {
        var list = ConnectionResolver.ListProfiles();
        return JsonSerializer.Serialize(new
        {
            active = _resolved?.ProfileName ?? _profile,
            profiles = list,
            note = "Bağlantı sunucu başlarken sabitlendi; model profil değiştiremez. Yalnız adlar listelenir.",
        }, JsonOptions);
    }

    private static string Serialize(List<string> columns, List<object?[]> rows, bool truncated, bool uncapped = false)
    {
        string Build(int rowCount, bool isTruncated)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JsonOptions.Encoder }))
            {
                w.WriteStartObject();
                w.WriteStartArray("columns"); foreach (var k in columns) w.WriteStringValue(k); w.WriteEndArray();
                w.WriteStartArray("rows");
                for (int i = 0; i < rowCount; i++)
                {
                    w.WriteStartArray();
                    foreach (var d in rows[i]) WriteValue(w, d);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteNumber("rowCount", rowCount);
                w.WriteBoolean("truncated", isTruncated);
                if (isTruncated) w.WriteString("note", "Sonuç tavana takıldı: WHERE/GROUP BY/TOP ile daraltın.");
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // Karakter tavanı: sığana kadar satır sayısını yarıla.
        int n = rows.Count;
        var s = Build(n, truncated);
        while (!uncapped && s.Length > OutputCharCap && n > 1)
        {
            n /= 2;
            s = Build(n, true);
        }
        return s;
    }

    private static void WriteValue(Utf8JsonWriter w, object? d)
    {
        switch (d)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int or uint or long: w.WriteNumberValue(Convert.ToInt64(d)); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case double db when double.IsFinite(db): w.WriteNumberValue(db); break;
            case decimal m: w.WriteNumberValue(m); break;
            case DateTime dt: w.WriteStringValue(dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF")); break;
            case DateTimeOffset dto: w.WriteStringValue(dto.ToString("o")); break;
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)));
                w.WriteStringValue("0x" + hex + (bytes.Length > 32 ? $"…({bytes.Length} bayt)" : ""));
                break;
            default:
                var t = d.ToString() ?? "";
                w.WriteStringValue(t.Length > 4000 ? t[..4000] + "…(truncated)" : t);
                break;
        }
    }

    private static string MaskSecrets(string message) =>
        Regex.Replace(message, @"(Password|Pwd)\s*=\s*[^;\s]+", "$1=***", RegexOptions.IgnoreCase);

    // ── Keşif araçları ───────────────────────────────────────────────────

    private static string LikePattern(string s)
    {
        s = s.Replace("'", "''");
        return s.Contains('%') ? s : $"%{s}%";
    }

    private static async Task<string> ListTablesAsync(JsonObject a)
    {
        var d = GetConnection().Dialect;
        int max = Int(a, "max_rows", DefaultRows, 1, MaxRows);
        bool withCounts = a["with_row_counts"]?.GetValue<bool>() ?? false;
        var filter = a["filter"]?.GetValue<string>();

        var sql = withCounts ? d.RowsSql : d.TablesSql;
        if (!string.IsNullOrWhiteSpace(filter))
            sql = d.SearchTablesSql(filter!.Contains('%') ? filter : $"%{filter}%");
        // Satır sayımıyla filtre birlikte: filtreli listeyi alıp sayımı katalogdan ayrıca vermek yerine
        // sayımlı listeyi alıp ad filtresini sonuçta uygularız.
        if (withCounts && !string.IsNullOrWhiteSpace(filter))
        {
            var all = await CatalogAsync(d.RowsSql);
            return FilterRows(all, filter!, max);
        }
        return await CatalogAsync(sql, max);
    }

    private static string FilterRows(string json, string filter, int max)
    {
        var doc = JsonNode.Parse(json)!.AsObject();
        var needle = filter.Replace("%", "");
        var kept = new JsonArray();
        foreach (var line in doc["rows"]!.AsArray())
        {
            var table = line!.AsArray()[1]?.ToString() ?? "";
            if (table.Contains(needle, StringComparison.OrdinalIgnoreCase) && kept.Count < max)
                kept.Add(line.DeepClone());
        }
        doc["rows"] = kept;
        doc["rowCount"] = kept.Count;
        return doc.ToJsonString(JsonOptions);
    }

    private static async Task<string> SearchSchemaAsync(string pattern)
    {
        var d = GetConnection().Dialect;
        var t = await CatalogAsync(d.SearchTablesSql(LikePattern(pattern).Replace("''", "'")), 500);
        var k = await CatalogAsync(d.SearchColumnsSql(LikePattern(pattern).Replace("''", "'")), 500);
        return $"{{\"tables\":{t},\"columns\":{k}}}";
    }

    private static (string Schema, string Name) ParseTable(string input)
    {
        if (!Regex.IsMatch(input, @"^[\p{L}\p{N}_.\[\]""$# -]+$"))
            throw new ArgumentException("Geçersiz tablo adı (yalnız harf/rakam/_/./[]/\" kullanılabilir).");
        return GetConnection().Dialect.SplitTable(input);
    }

    private static async Task<string> DescribeTableAsync(string table)
    {
        var d = GetConnection().Dialect;
        var (s, n) = ParseTable(table);
        var cols = await CatalogAsync(d.DescribeColumnsSql(s, n));
        if (cols.Contains("\"rowCount\":0"))
            throw new InvalidOperationException($"Tablo bulunamadı: {s}.{n} (search_schema ile adı arayın).");
        var idx = await CatalogAsync(d.DescribeIndexesSql(s, n));
        var fk = await CatalogAsync(d.DescribeFksSql(s, n));
        var count = await CatalogAsync(d.RowCountSql(s, n));
        return $"{{\"table\":\"{s}.{n}\",\"columns\":{cols},\"indexes\":{idx},\"foreignKeys\":{fk},\"approxRows\":{count}}}";
    }

    private static async Task<string> RelationshipsAsync(string table)
    {
        var d = GetConnection().Dialect;
        var (s, n) = ParseTable(table);
        var outgoing = await CatalogAsync(d.RelOutgoingSql(s, n));
        var incoming = await CatalogAsync(d.RelIncomingSql(s, n));
        return $"{{\"table\":\"{s}.{n}\",\"outgoing\":{outgoing},\"incoming\":{incoming}}}";
    }

    private static async Task<string> SampleRowsAsync(string table, int n)
    {
        var r = GetConnection();
        ParseTable(table); // ad doğrulaması
        return await QueryAsync(r.Dialect.TopSql(table, n), n, DefaultTimeoutSeconds, "sample_rows");
    }

    private static async Task<string> ExplainQueryAsync(string sql)
    {
        var rejection = SqlGuard.Check(sql, true);
        if (rejection != null) throw new InvalidOperationException(rejection);
        var r = GetConnection();
        await using var conn = await ConnectionResolver.OpenAsync(r.ConnectionString);
        var plan = await r.Dialect.ExplainAsync(conn, sql, actual: false, format: null);
        ConfigStore.AppendHistory(sql, $"mcp:explain_query:{r.ProfileName}");
        var body = string.Join("\n\n", plan.Plans);
        return body.Length > OutputCharCap ? body[..OutputCharCap] + "\n…(truncated)" : body;
    }

    private static async Task<string> ConnectionInfoAsync()
    {
        var r = GetConnection();
        var info = await CatalogAsync(r.Dialect.ConnInfoSql, 1);
        string privileges = "n/a";
        if (r.Dialect.Provider == DbProvider.SqlServer)
        {
            var y = await CatalogAsync(
                "SELECT IS_ROLEMEMBER('db_datawriter') AS db_datawriter, IS_ROLEMEMBER('db_owner') AS db_owner, " +
                "IS_SRVROLEMEMBER('sysadmin') AS sysadmin", 1);
            privileges = y;
        }
        return $"{{\"provider\":\"{r.Dialect.Name}\",\"profile\":\"{r.ProfileName}\",\"connection\":{info},\"writeRoles\":{(privileges == "n/a" ? "null" : privileges)},\"mode\":\"read-only (SqlGuard + rollback)\"}}";
    }
}
