using System.Text.Json;

namespace SqlCli.Core;

// Kullanıcı dizininde kaydedilen state:
//   ~/.sqlcli/
//     saved.json   — { "name": "SQL", ... }  isimli kaydedilmiş query'ler
//     history.log  — append-only çalışan query log (line-based, son 1000)
public static class ConfigStore
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sqlcli");

    private static string SavedPath => Path.Combine(Root, "saved.json");
    private static string HistoryPath => Path.Combine(Root, "history.log");

    private const int MaxHistoryLines = 1000;

    static ConfigStore()
    {
        Directory.CreateDirectory(Root);
    }

    // ─── Saved queries ─────────────────────────────────────

    public static Dictionary<string, string> LoadSaved()
    {
        if (!File.Exists(SavedPath)) return new();
        try
        {
            var json = File.ReadAllText(SavedPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void SaveQuery(string name, string sql)
    {
        var dict = LoadSaved();
        dict[name] = sql;
        File.WriteAllText(SavedPath,
            JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool DeleteSaved(string name)
    {
        var dict = LoadSaved();
        if (!dict.Remove(name)) return false;
        File.WriteAllText(SavedPath,
            JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    // ─── History ───────────────────────────────────────────

    public static void AppendHistory(string sql, string? profile)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{profile ?? "-"}\t{sql.Replace("\r", " ").Replace("\n", " ")}";
            File.AppendAllText(HistoryPath, line + Environment.NewLine);

            // Periyodik olarak truncate (her 50 satırda bir kontrol)
            if (Random.Shared.Next(50) == 0) TruncateHistoryIfNeeded();
        }
        catch
        {
            // history yazımı kritik değil — sessiz devam
        }
    }

    public static List<HistoryEntry> ReadHistory(int take = 50)
    {
        if (!File.Exists(HistoryPath)) return new();
        var lines = File.ReadAllLines(HistoryPath);
        return lines.Reverse().Take(take).Select(ParseLine).Where(e => e != null).Cast<HistoryEntry>().ToList();
    }

    private static void TruncateHistoryIfNeeded()
    {
        if (!File.Exists(HistoryPath)) return;
        var lines = File.ReadAllLines(HistoryPath);
        if (lines.Length <= MaxHistoryLines) return;
        File.WriteAllLines(HistoryPath, lines.Skip(lines.Length - MaxHistoryLines));
    }

    private static HistoryEntry? ParseLine(string line)
    {
        var parts = line.Split('\t', 3);
        if (parts.Length < 3) return null;
        if (!DateTime.TryParse(parts[0], out var ts)) return null;
        return new HistoryEntry(ts, parts[1] == "-" ? null : parts[1], parts[2]);
    }

    public sealed record HistoryEntry(DateTime At, string? Profile, string Sql);
}
