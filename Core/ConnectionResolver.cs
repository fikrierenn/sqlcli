using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace SqlCli.Core;

// Bağlantı string'i çözümleme + multi-profile desteği.
//
// sqlcli.json yapısı (yeni):
//   {
//     "Connection": "Server=...;Database=...;...",  // legacy single-conn
//     "Profiles": {
//       "mosaik": "Server=BT-FIKRI\\SQLEXPRESS;Database=Mosaik;...",
//       "zirve":  "Server=192.168.40.25,64507;Database=BKM_GENEL;User Id=rapor_readonly;..."
//     },
//     "DefaultProfile": "mosaik",
//     "SqlDir": "./docs/sql"
//   }
//
// Öncelik sırası:
//   1. SQLCLI_CONN env var
//   2. --conn "..." argümanı
//   3. --profile <name> → Profiles[name]
//   4. sqlcli.json > Connection (legacy)
//   5. sqlcli.json > Profiles[DefaultProfile]
//   6. appsettings.json > ConnectionStrings:DefaultConnection veya Default
public static class ConnectionResolver
{
    public sealed record ResolvedConnection(string ConnectionString, string? ProfileName)
    {
        private ISqlDialect? _dialect;
        public ISqlDialect Dialect => _dialect ??= ISqlDialect.Detect(ConnectionString);

        /// <summary>`${ENV}` yer tutucuları çözülmüş bağlantı string'i (bağlanırken kullanılır).</summary>
        public string Effective => ExpandEnv(ConnectionString);
    }

    /// <summary>
    /// `${VAR}` yer tutucularını ortam değişkeninden (yoksa depo kökündeki `.env`'den) doldurur.
    ///
    /// NEDEN: `sqlcli.json` bir depo dosyasıdır; şifre orada DÜZ METİN durmamalı. Profil
    /// `Password=${MSSQL_PASSWORD}` yazar, sır `.env`'de (gitignore) kalır. Komut satırına da
    /// yazılmadığı için kabuk geçmişine düşmez.
    /// Karşılığı bulunamayan yer tutucu SESSİZ BIRAKILMAZ — hata verir (yoksa bağlantı
    /// "şifre yanlış" diye anlaşılmaz biçimde patlar).
    /// </summary>
    public static string ExpandEnv(string connectionString)
    {
        if (!connectionString.Contains("${")) return connectionString;

        var dotenv = LoadDotEnv();
        var eksik = new List<string>();
        var sonuc = System.Text.RegularExpressions.Regex.Replace(
            connectionString, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
            {
                var ad = m.Groups[1].Value;
                var deger = Environment.GetEnvironmentVariable(ad);
                if (string.IsNullOrEmpty(deger)) dotenv.TryGetValue(ad, out deger);
                if (string.IsNullOrEmpty(deger)) { eksik.Add(ad); return m.Value; }
                return deger;
            });

        if (eksik.Count > 0)
            throw new InvalidOperationException(
                "Bağlantı string'inde karşılığı olmayan yer tutucu: " + string.Join(", ", eksik) +
                ". Ortam değişkeni ver ya da .env dosyasına ekle.");
        return sonuc;
    }

    private static Dictionary<string, string> LoadDotEnv()
    {
        var sonuc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var yol = FindFile(".env");
        if (yol == null) return sonuc;
        foreach (var satir in File.ReadLines(yol))
        {
            var s = satir.Trim();
            if (s.Length == 0 || s.StartsWith('#') || !s.Contains('=')) continue;
            var i = s.IndexOf('=');
            sonuc[s[..i].Trim()] = s[(i + 1)..].Trim();
        }
        return sonuc;
    }

    public static ResolvedConnection? Resolve(string? explicitConn, string? profileName)
    {
        // 1. Ortam değişkeni
        var env = Environment.GetEnvironmentVariable("SQLCLI_CONN");
        if (!string.IsNullOrWhiteSpace(env)) return new(env, "env");

        // 2. --conn argümanı
        if (!string.IsNullOrWhiteSpace(explicitConn)) return new(explicitConn, "argv");

        // 3+4+5. sqlcli.json
        var sqlcliJson = FindFile("sqlcli.json");
        if (sqlcliJson != null)
        {
            var cfg = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(sqlcliJson)!)
                .AddJsonFile("sqlcli.json")
                .Build();

            // 3. Açık profile param
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                var cs = cfg[$"Profiles:{profileName}"];
                if (!string.IsNullOrWhiteSpace(cs)) return new(cs, profileName);
                throw new InvalidOperationException($"Profile bulunamadı: '{profileName}'. sqlcli.json > Profiles içinde tanımlı değil.");
            }

            // 4. Legacy Connection
            var legacy = cfg["Connection"];
            if (!string.IsNullOrWhiteSpace(legacy)) return new(legacy, "default");

            // 5. DefaultProfile
            var defaultProfile = cfg["DefaultProfile"];
            if (!string.IsNullOrWhiteSpace(defaultProfile))
            {
                var cs = cfg[$"Profiles:{defaultProfile}"];
                if (!string.IsNullOrWhiteSpace(cs)) return new(cs, defaultProfile);
            }
        }

        // 6. appsettings.json
        var appSettings = FindFile("appsettings.json");
        if (appSettings != null)
        {
            var cfg = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(appSettings)!)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .Build();
            foreach (var name in new[] { "DefaultConnection", "Default" })
            {
                var cs = cfg.GetConnectionString(name);
                if (!string.IsNullOrWhiteSpace(cs)) return new(cs, $"appsettings:{name}");
            }
        }

        return null;
    }

    public static string ResolveSqlDir(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir)) return explicitDir;

        var sqlcliJson = FindFile("sqlcli.json");
        if (sqlcliJson != null)
        {
            var cfg = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(sqlcliJson)!)
                .AddJsonFile("sqlcli.json").Build();
            var d = cfg["SqlDir"];
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }

        var docsql = Path.Combine(Directory.GetCurrentDirectory(), "docs", "sql");
        if (Directory.Exists(docsql)) return docsql;

        var dbDir = Path.Combine(Directory.GetCurrentDirectory(), "Database");
        if (Directory.Exists(dbDir)) return dbDir;

        return Directory.GetCurrentDirectory();
    }

    public static List<string> ListProfiles()
    {
        var sqlcliJson = FindFile("sqlcli.json");
        if (sqlcliJson == null) return new();
        var cfg = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(sqlcliJson)!)
            .AddJsonFile("sqlcli.json").Build();
        return cfg.GetSection("Profiles").GetChildren().Select(c => c.Key).ToList();
    }

    public static string MaskPassword(string connStr) =>
        System.Text.RegularExpressions.Regex.Replace(
            connStr, @"Password=[^;]+", "Password=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Dialect-aware bağlantı açma. Sağlayıcıyı connection string'ten tespit eder.
    public static async Task<DbConnection> OpenAsync(string connStr)
    {
        // `${ENV}` yer tutucuları BURADA çözülür → tüm çağrı yerleri (query/describe/export…)
        // otomatik faydalanır, hiçbirinin ayrıca genişletme yapması gerekmez.
        connStr = ExpandEnv(connStr);
        var dialect = ISqlDialect.Detect(connStr);
        var conn = dialect.Create(connStr);
        await conn.OpenAsync();
        await dialect.InitAsync(conn);
        return conn;
    }

    private static string? FindFile(string filename)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, filename);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
