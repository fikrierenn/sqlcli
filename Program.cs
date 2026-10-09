// ============================================================
// sqlcli v2.3 — SQL Server + PostgreSQL yönetim + keşif + migration CLI
//
// Komutlar:
//   migrate / seed / script / migrate-status / migrate-create
//   query / top / save / run / saved-list / saved-delete / history
//   status / tablolar / tables / rows / fk-kontrol / indexler / baglanti / profiles
//   describe / desc / search / relationships / rel
//   explain / active-sessions / slow-queries / kill
//   tables-empty / seed-demo / export / import / diff
//   assert   — sorgu sonucunu beklenenle karşılaştır, uymazsa exit 1 (koşulabilir denetim)
//   lookup   — kod/tip tablosunu YAML/JSON dök (+ canlı kullanım sayısı)
//   surum    — kurulu sürüm + commit; --kaynak ile kaynağın gerisinde mi (exit 0/1/2)
//
// v2.2 ortak bayrakları: --read-only (yazma reddi) · --param ad=deger (adlı parametre)
//   --timeout <sn> · --retry <n> (yalnız geçici hatada, görünür) · bağlantıda ${ENV}
//
// v2.3: SÜRÜM MUHAFIZI — eski bir kopya çalışırsa hata verip "güncelle" der (Core/VersionGuard.cs)
//
// Kurulum:
//   cd D:/Dev/sqlcli
//   dotnet pack
//   dotnet tool install -g --add-source ./nupkg SqlCli
//
// Bağlantı (öncelik sırası):
//   1. SQLCLI_CONN env var
//   2. --conn "..." argümanı
//   3. --profile <name> → sqlcli.json > Profiles[name]
//   4. sqlcli.json > Connection (legacy single)
//   5. sqlcli.json > Profiles[DefaultProfile]
//   6. appsettings.json > ConnectionStrings:DefaultConnection veya Default
// ============================================================

using Cocona;
using Spectre.Console;
using SqlCli.Commands;
using SqlCli.Core;

// ÇIKTI KODLAMASI: Windows konsolu OEM kod sayfasında (Türkçe'de cp857) yazar; çıktı bir
// dosyaya/pipe'a alındığında "Fiyat Farkı" → "Fiyat Fark?" olur ve o metin bir belgeye
// (sema, rapor) yazılırsa veri BOZULUR. UTF-8'e sabitliyoruz: hem terminal hem pipe doğru.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* yönlendirilmiş stdout */ }

// --format json istendiyse banner stderr'e: stdout doğrudan parse edilebilsin (eskiden
// çağıranlar banner'ı ve ANSI kodlarını elle ayıklıyordu — pusula 2026-06-18 günlüğü).
var jsonCikti = args.Select((a, i) => a.Equals("--format=json", StringComparison.OrdinalIgnoreCase)
    || (a == "--format" && i + 1 < args.Length && args[i + 1].Equals("json", StringComparison.OrdinalIgnoreCase))).Any(x => x);
var banner = $"=== sqlcli v{VersionGuard.CurrentText} — SQL Server + PostgreSQL CLI ===";
if (jsonCikti) Console.Error.WriteLine(banner);
else AnsiConsole.MarkupLine($"[grey]{banner}[/]");

// Makinede birden çok kopya var; eskisi çalışırsa burada durur (exit 2 = KOŞAMADI).
VersionGuard.EnsureLatest(args);

// ASKIDA KALMA: Cocona'nın generic host'u çalışma dizinini içerik kökü alıp appsettings.json
// için dosya izleyicisi kuruyor. Çalışma dizini ağ/WebDAV sürücüsüyse (D:\ → DavWWWRoot)
// izleyici kurulumu dönmüyor ve süreç HİÇ bitmiyor — her komut, --help dahil (2026-09-26 ölçüldü:
// yerel dizinde 0,6 sn, D:'de 30 sn+ askıda). CLI'da yapılandırmayı canlı izlemeye gerek yok.
// Kullanıcı açıkça true verdiyse ona dokunmuyoruz.
if (Environment.GetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange") is null)
    Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

var builder = CoconaApp.CreateBuilder(args);
var app = builder.Build();
app.AddCommands<SchemaCommands>();
app.AddCommands<QueryCommands>();
app.AddCommands<IntrospectCommands>();
app.AddCommands<PerfCommands>();
app.AddCommands<EtlCommands>();
app.AddCommands<DataCommands>();
app.AddCommands<AssertCommands>();
app.AddCommands<LookupCommands>();
app.AddCommands<VersionCommands>();

try
{
    app.Run();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"\n[red]HATA:[/] {Markup.Escape(ex.Message)}");
    if (ex.InnerException != null)
        AnsiConsole.MarkupLine($"[grey]DETAY:[/] {Markup.Escape(ex.InnerException.Message)}");
    Environment.Exit(1);
}
