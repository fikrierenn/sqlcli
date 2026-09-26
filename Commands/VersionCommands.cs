using System.Diagnostics;
using System.Reflection;
using Cocona;
using Spectre.Console;

namespace SqlCli.Commands;

// Araç sürümü: kurulu sqlcli hangi commit'ten derlendi, kaynağın gerisinde mi?
//
// NEDEN: sqlcli birden çok makinede global tool olarak kurulu (laptop, SANAL-FIKRI) ve
// kaynak iki klonda duruyor. "Hangi sqlcli çalışıyor?" sorusunun cevabı elle
// karşılaştırmaya kalıyordu. .NET SDK derlenen commit'i InformationalVersion'a ekliyor
// (2.3.0+<sha>); bu komut onu okur ve kaynağın KOD commit'leriyle karşılaştırır.
// Yalnız doküman değiştiren commit "geride" sayılmaz: kod aynıysa araç güncel.
//
// Çıkış (sqlcli sözleşmesi): 0 = güncel · 1 = geride · 2 = kontrol edilemedi.
// --kaynak verilmezse yalnız bilgi basar ve 0 döner (karşılaştırma yapılmadı der).
public class VersionCommands
{
    // Kod sayılan yollar. sqlcli.json kullanıcı profili — kod değil.
    private static readonly string[] CodePaths =
        { "*.cs", "*.csproj", "*.props", "*.json", ":!**/sqlcli.json" };

    [Command("surum", Description = "Kurulu sürüm + derlendiği commit; --kaynak ile kaynağın gerisinde mi (alias: version)")]
    public void Surum(
        [Option(Description = "sqlcli kaynak klasörü (git klonu). Yoksa SQLCLI_KAYNAK ortam değişkeni.")] string? kaynak = null,
        [Option(Description = "Önce git fetch: kaynak klon uzağın gerisindeyse uyar")] bool uzak = false)
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = info.IndexOf('+');
        var version = plus < 0 ? info : info[..plus];
        var commit = plus < 0 ? null : info[(plus + 1)..];
        var exe = Environment.ProcessPath ?? "?";

        AnsiConsole.MarkupLine($"\n[cyan]Sürüm:[/] {Markup.Escape(version)}");
        AnsiConsole.MarkupLine($"[cyan]Commit:[/] {Markup.Escape(commit ?? "(sürüm bilgisinde yok)")}");
        AnsiConsole.MarkupLine($"[cyan]Çalışan:[/] {Markup.Escape(exe)}");
        if (File.Exists(exe))
            AnsiConsole.MarkupLine($"[cyan]Derleme:[/] {File.GetLastWriteTime(exe):dd.MM.yyyy HH:mm}");

        kaynak ??= Environment.GetEnvironmentVariable("SQLCLI_KAYNAK");
        if (string.IsNullOrWhiteSpace(kaynak))
        {
            AnsiConsole.MarkupLine("[grey]Kaynakla karşılaştırılmadı (--kaynak <klasör> ya da SQLCLI_KAYNAK).[/]\n");
            return;
        }
        if (commit is null) Fail("sürüm bilgisinde commit yok — karşılaştırılamaz");
        if (!Directory.Exists(Path.Combine(kaynak, ".git"))) Fail($"git klonu değil: {kaynak}");

        if (uzak)
        {
            Git(kaynak, "fetch", "-q");
            var ab = Git(kaynak, "rev-list", "--left-right", "--count", "HEAD...@{u}").Split('\t', ' ');
            if (ab.Length == 2 && ab[1].Trim() != "0")
                AnsiConsole.MarkupLine($"[yellow]UYARI:[/] kaynak klon uzağın {ab[1].Trim()} commit gerisinde (git pull gerekli).");
        }

        // Kurulu commit kaynakta YOKSA sessizce "güncel" deme: bilinmeyen bir derleme.
        if (Git(kaynak, "cat-file", "-t", commit!, allowFail: true) != "commit")
            Fail($"kurulu commit {Short(commit!)} kaynakta yok (başka bir klondan ya da yerel değişiklikle derlenmiş)");

        var behind = Git(kaynak, new[] { "log", "--oneline", $"{commit}..HEAD", "--" }.Concat(CodePaths).ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (behind.Length > 0)
        {
            AnsiConsole.MarkupLine($"\n[red]GERİDE:[/] kaynakta {behind.Length} kod commit'i kurulu sürümde yok:");
            foreach (var l in behind) AnsiConsole.MarkupLine($"  {Markup.Escape(l)}");
            AnsiConsole.MarkupLine($"[grey]Yeniden kur: dotnet pack \"{Markup.Escape(kaynak)}\" -o ./nupkg ; dotnet tool update -g SqlCli --add-source ./nupkg[/]\n");
            throw new CommandExitedException(1);
        }
        AnsiConsole.MarkupLine($"[green]GÜNCEL:[/] kaynağın son kod commit'i kurulu sürüme dahil ({Markup.Escape(kaynak)})\n");
    }

    [Command("version", Description = "surum alias")]
    public void Version([Option] string? kaynak = null, [Option] bool uzak = false) => Surum(kaynak, uzak);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static void Fail(string why)
    {
        AnsiConsole.MarkupLine($"[red]KONTROL EDİLEMEDİ:[/] {Markup.Escape(why)}\n");
        throw new CommandExitedException(2);
    }

    private static string Git(string dir, params string[] args) => Git(dir, args, allowFail: false);

    private static string Git(string dir, string a1, string a2, string a3, bool allowFail) =>
        Git(dir, new[] { a1, a2, a3 }, allowFail);

    private static string Git(string dir, string[] args, bool allowFail)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Ağ sürücüsündeki klonlarda git "dubious ownership" verir; bu komut yalnız okur.
        foreach (var a in new[] { "-c", "safe.directory=*", "-C", dir }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git başlatılamadı");
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFail) Fail($"git {string.Join(' ', args)}: {err.Trim()}");
        return outp.Trim();
    }
}
