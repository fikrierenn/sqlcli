using System.Reflection;
using Spectre.Console;

namespace SqlCli.Core;

/// <summary>
/// SÜRÜM MUHAFIZI — eski bir sqlcli kopyası çalıştırılırsa HATA verip durur.
///
/// NEDEN VAR: makinede birden çok sqlcli kopyası dolaşıyor (MIMBAL/tools/sqlcli,
/// fifo/sqlcli, eski global tool…). Eski kopya sessizce çalışınca yeni muhafızlar
/// (--read-only izin listesi vb.) devrede değilmiş gibi görünmeden devre dışı kalır;
/// "ölçtüm" dediğimiz sonuç eski kodla ölçülmüş olur.
///
/// "EN GÜNCEL" ŞU KAYNAKLARIN EN BÜYÜĞÜDÜR (hepsi yerel, ağ yok):
///   1. ~/.dotnet/tools/.store/sqlcli/&lt;sürüm&gt; — kurulu global tool sürüm(ler)i
///   2. SQLCLI_SOURCE env → &lt;dizin&gt;/sqlcli.csproj içindeki &lt;Version&gt; (kanonik kaynak)
///
/// "Makinede çalışmış en yüksek sürüm" işaret dosyası BİLEREK YOK: kurulmamış bir
/// Debug/geçici derlemenin tek bir çalıştırılışı işareti yükseltir ve ardından kurulu
/// araç her açılışta durur (2026-09-25'te fiilen oldu). Yalnız KURULU olan "güncel" sayılır.
///
/// `surum` / `version` / `--version` muaf: "hangi sürüm çalışıyor?" diye soran komut
/// bu hatayla kapanmamalı.
///
/// Çalışan sürüm bunlardan küçükse: kırmızı hata + güncelleme komutu + exit 2 (KOŞAMADI).
/// Acil kaçış: SQLCLI_SKIP_VERSION_CHECK=1 (görünür uyarıyla devam eder).
/// </summary>
public static class VersionGuard
{
    public static Version Current { get; } = ReadCurrent();

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static Version ReadCurrent()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // "2.3.0+abc123" → "2.3.0"
        if (info != null && Version.TryParse(info.Split('+', '-')[0], out var v)) return Normalize(v);
        return Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static readonly string[] MuafKomutlar = ["surum", "version", "--version"];

    /// <summary>Eskiyse hata basıp süreci 2 ile bitirir.</summary>
    public static void EnsureLatest(string[] args)
    {
        if (args.Length > 0 && MuafKomutlar.Contains(args[0], StringComparer.OrdinalIgnoreCase)) return;

        var kaynaklar = new List<(Version Surum, string Nereden)>();

        var store = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", ".store", "sqlcli");
        try
        {
            if (Directory.Exists(store))
                foreach (var d in Directory.GetDirectories(store))
                    if (Version.TryParse(Path.GetFileName(d), out var v))
                        kaynaklar.Add((Normalize(v), "kurulu global tool"));
        }
        catch { /* erişilemeyen store kontrolü engellemez */ }

        var src = Environment.GetEnvironmentVariable("SQLCLI_SOURCE");
        if (!string.IsNullOrWhiteSpace(src))
        {
            var v = ReadCsprojVersion(Path.Combine(src, "sqlcli.csproj"));
            if (v != null) kaynaklar.Add((v, $"kanonik kaynak {src}"));
        }

        var enYeni = kaynaklar.OrderByDescending(k => k.Surum).FirstOrDefault();

        if (enYeni.Surum != null && Current < enYeni.Surum)
        {
            var konum = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Environment.GetEnvironmentVariable("SQLCLI_SKIP_VERSION_CHECK") == "1")
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]UYARI:[/] eski sqlcli v{CurrentText} çalışıyor (güncel v{Fmt(enYeni.Surum)}) — " +
                    "SQLCLI_SKIP_VERSION_CHECK=1 ile kontrol atlandı.");
                return;
            }

            AnsiConsole.MarkupLine($"\n[red]HATA: Bu sqlcli kopyası ESKİ.[/]");
            AnsiConsole.MarkupLine($"  Çalışan : v{CurrentText}  [grey]({Markup.Escape(konum)})[/]");
            AnsiConsole.MarkupLine($"  Güncel  : v{Fmt(enYeni.Surum)}  [grey]({Markup.Escape(enYeni.Nereden)})[/]");
            AnsiConsole.MarkupLine("\n[yellow]Güncelle:[/] kanonik kaynakta");
            AnsiConsole.MarkupLine("  dotnet pack -c Release -o ./nupkg");
            AnsiConsole.MarkupLine("  dotnet tool update -g --add-source ./nupkg SqlCli");
            AnsiConsole.MarkupLine("[grey]Bu bir kopya ise (MIMBAL/tools, fifo…) onu kullanma — global `sqlcli` aracını kullan.[/]");
            AnsiConsole.MarkupLine("[grey]Acil durumda: SQLCLI_SKIP_VERSION_CHECK=1[/]");
            Environment.Exit(2);
        }
    }

    private static Version? ReadCsprojVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(path), @"<Version>\s*([0-9.]+)\s*</Version>");
            return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? Normalize(v) : null;
        }
        catch { return null; }
    }

    private static string Fmt(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
