// ============================================================
// sqlcli v2.2 — SQL Server + PostgreSQL yönetim + keşif + migration CLI
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
//
// v2.2 ortak bayrakları: --read-only (yazma reddi) · --param ad=deger (adlı parametre)
//   --timeout <sn> · --retry <n> (yalnız geçici hatada, görünür) · bağlantıda ${ENV}
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

// ÇIKTI KODLAMASI: Windows konsolu OEM kod sayfasında (Türkçe'de cp857) yazar; çıktı bir
// dosyaya/pipe'a alındığında "Fiyat Farkı" → "Fiyat Fark?" olur ve o metin bir belgeye
// (sema, rapor) yazılırsa veri BOZULUR. UTF-8'e sabitliyoruz: hem terminal hem pipe doğru.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* yönlendirilmiş stdout */ }

AnsiConsole.MarkupLine("[grey]=== sqlcli v2.2 — SQL Server + PostgreSQL CLI ===[/]");

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
