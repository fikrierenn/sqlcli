# sqlcli — ölçümün eli (v2.4.0)

**Tek komutla SQL Server ve PostgreSQL'e karşı sor, doğrula, dök.** `dotnet tool` olarak kurulur; profiller `sqlcli.json`'dan, şifreler
ortam değişkeninden gelir — hiçbir şifre dosyaya yazılmaz. Omurgadaki her "ölçtüm" bu araçla yapılır: şema denetimi, kod kümesi dökümü,
kapı iddiaları.

*v2.4.0 · .NET 10 · global araç adı `sqlcli`.*

## Neden var

Bir sayı **iddia** ile **ölçüm** arasında fark yaratır. `sqlcli assert --eq 0 "SELECT COUNT(*) …"` bir kapının içine girer ve uymazsa kırmızı
verir; `sqlcli lookup dbo.irsTip_vw --count-from dbo.irsHrk.ehTip` kod listesini **canlı kullanım sayılarıyla** döker, elle yazılan liste
bayatlamaz. `--read-only` izin listesidir (yalnız `SELECT`/`WITH…SELECT`), ERP salt-okuma politikasını araç düzeyinde zorlar.

## Çıkış sözleşmesi

`0` geçti · `1` KIRIK (sonuç beklenene uymadı) · `2` KOŞAMADI (bağlantı/SQL hatası — **yeşil sayılmaz**). Ölçememek, temiz olmakla aynı şey değildir.

## Hızlı başlangıç

```bash
dotnet pack -c Release -o ./nupkg && dotnet tool install -g --add-source ./nupkg SqlCli
cd <profil dosyasının olduğu dizin>          # sqlcli.json çalışma dizininden okunur
sqlcli query  --profile erp   --read-only --format md "SELECT TOP 5 * FROM dbo.urn"
sqlcli assert --profile zirve --eq 0 --why "aktif personelde TC boş olamaz" "SELECT COUNT(*) FROM dbo.vw_PersonelDepartman WHERE Ict IS NULL AND Vatno IS NULL"
sqlcli lookup --profile erp dbo.irsTip_vw --count-from dbo.irsHrk.ehTip
```

Profil örneği (`sqlcli.json`): `"zirve": "Server=${ZIRVE_HOST};Database=${ZIRVE_DATABASE};User Id=${ZIRVE_USER};Password=${ZIRVE_PASSWORD};…"` —
yer tutucu ortamdan, yoksa depo kökündeki `.env`den dolar; bulunamazsa **hata verir**, sessiz geçmez.

## BKM Kitap yazılım omurgası — bu depo nerede duruyor

BKM Kitap'ta yazılım tek tek uygulamalar değil, birbirine oturan **katmanlar** olarak büyüyor. Beş depo, tek omurga:

| Katman | Depo | Görevi |
|---|---|---|
| **Kurallar** | [claude-context-template](https://github.com/fikrierenn/claude-context-template) (→ *Norma*) | Her deponun doğuştan aldığı 19 evrensel kural, kapılar, adlandırma standardı, 3.000+ sözcüklük ak liste. Kopyalanmaz, işaret edilir. |
| **Ortak .NET katmanı** | [Solum](https://github.com/fikrierenn/Solum) | Kimlik, yetki, çok kiracılılık, denetim izi, migrasyon, şema sapma doğrulaması — bir kez yazılır, her ürün referansla alır. |
| **Veri gerçeği** | [pusula](https://github.com/fikrierenn/pusula) | ERP · Encore · Zirve · panel şemalarının **ölçülmüş** tanımı (10.000+ satır YAML), 150+ sorgu, 40+ plan. Her rapor ve uygulama buradan beslenir; iki gerçek doğmaz. |
| **Ölçüm aracı** | [sqlcli](https://github.com/fikrierenn/sqlcli) | "İddia değil ölçüm": profilli SQL CLI, salt-okuma muhafızı, `assert`/`lookup`, çıkış sözleşmesi 0/1/2. Kapıların ve şemanın ölçüm eli. |
| **Ürünler** | [bkm-magaza](https://github.com/fikrierenn/bkm-magaza) ve diğerleri (vardiya, anlık ciro, etiket, el terminali…) | Sahada çalışan uygulamalar. İlk örnek bkm-magaza: ~200 mağaza personelinin telefonunda ürün bulma. |

**Yön (GMY, 2026):** bütün uygulamalar **tek kullanıcı ve yetki yönetimine** taşınır (Solum İSTEK-31: kişi → çalışma dönemi → hesap → cihaz;
kimlik İK sisteminden türer, işten çıkış uygulamayı kendiliğinden kapatır). Yeni bir uygulama bu omurgaya oturur; kendi kullanıcı tablosunu,
kendi kuralını, kendi şema kopyasını yazmaz.

**Çalışma ilkeleri (hepsi kapıyla zorlanır, niyet olarak bırakılmaz):** önce ölç, sonra yaz · kural değil kapı · sözleşme değişince eskisi bir sürüm
çift yaşar · kod İngilizce, insan dili Türkçe · hiçbir sır depoya girmez · her commit tek konu, her kapı sabotajla kırılabilir olduğunu kanıtlar.

---

## Ayrıntılar (teknik README, korunuyor)

### sqlcli v2.2

**SQL Server + PostgreSQL** için modern, hızlı, çoklu-profil destekli yönetim CLI'ı.
Migration + keşif + query + performans + history + export/import/diff hepsi tek araçta.

> **Çok-sağlayıcı (v2.1):** Sağlayıcı connection string'ten otomatik tespit edilir — `Host=` veya `postgres://` → PostgreSQL, aksi → SQL Server. Keşif/query/migrate/seed/export/import/diff iki DB'de de çalışır. Perf DMV (`explain`/`active-sessions`/`slow-queries`/`kill`) ve `copy` (SqlBulkCopy) şimdilik yalnız SQL Server. **Oracle:** yeni bir `ISqlDialect` impl'i ile eklenir (dialect seam hazır — `Core/Dialects.cs`).
>
> **KANONİK KOPYA = `D:\Dev\sqlcli`.** MIMBAL/tools/sqlcli ve fifo/sqlcli ESKİ birebir kopyalardır (drift). Diğer projeler global tool + kendi `sqlcli.json`'unu kullanmalı; kaynak fork'lamayın.

```
sqlcli describe Users          # Şema + indeks + FK + satır sayısı tek bakışta
sqlcli search "%audit%"        # Tablo/kolon adında pattern arama
sqlcli relationships ReportCatalog  # Gelen + giden FK'lar
sqlcli top Notifications 20    # SELECT TOP 20 * FROM ...
sqlcli query "SELECT ..." --format json  # JSON export (Claude/script-friendly)
sqlcli migrate-status          # Disk vs DB migration karşılaştır
sqlcli active-sessions         # Aktif oturum + blocking
sqlcli slow-queries --top 10   # Plan cache'den en yavaşlar
```

## v2.2 — koşulabilir denetim + güvenlik + dayanıklılık (03.09.2026)

```bash
# ASSERT — sorgu sonucunu beklenenle karşılaştır; uymazsa exit 1
sqlcli assert --profile erp --eq 0 --label belge-tipi   --why "Yeni belge tipinin cirosu hiçbir rapora girmez"   "SELECT COUNT(*) FROM Sales WHERE DocumentsTypeId NOT IN (1,2,3,6,7,8)"
# exit 0 = geçti · 1 = KIRIK · 2 = KOŞAMADI (bağlantı/SQL hatası — yeşil DEĞİL)

# LOOKUP — kod/tip tablosunu YAML/JSON dök, canlı kullanım sayısıyla
sqlcli lookup --profile erp dbo.irsTip_vw --count-from dbo.irsHrk.ehTip
#   values:
#     0: 'Alış — 1.458.421 kayıt'
#     15: 'Örnek Alımı — canlıda yok'

# SALT-OKUMA muhafızı (bayrak veya SQLCLI_READONLY=1)
sqlcli query --read-only "UPDATE ..."     # → REDDEDİLDİ, exit 2

# ADLI PARAMETRE — tip açık yazılır, tarih TAHMİN EDİLMEZ
sqlcli query --param bas:date=2026-09-01 "SELECT ... WHERE d >= @bas"
sqlcli query --param kod:str=20260901 "..."   # metin olarak zorla

# ZAMAN AŞIMI + geçici hata retry (yalnız 10053/10060/-2/1205 gibi; görünür)
sqlcli query --timeout 60 --retry 2 "SELECT ..."
```

**Bağlantıda `${ENV}` genişletmesi:** `sqlcli.json` artık şifre TAŞIMAZ —
`Password=${MSSQL_PASSWORD}` yazılır, değer ortam değişkeninden ya da depo kökündeki
`.env`'den gelir. Karşılığı bulunamayan yer tutucu sessiz geçmez, hata verir. Böylece
profil dosyası depoya girebilir.

**UTF-8 çıktı:** Windows konsolu Türkçe'de cp857 yazıyordu; çıktı dosyaya/pipe'a alınıp
bir belgeye yazıldığında veri bozuluyordu ("Fiyat Farkı" → "Fiyat Fark?"). `Program.cs`
artık `Console.OutputEncoding = UTF8`.

### Tasarım sınırı (03.09.2026)

`ISqlDialect` dikişi **SQL lehçeleri** içindir (SQL Server · PostgreSQL · Oracle seam).
SQL olmayan kaynaklar (ör. Odoo `search_read`/`fields_get`, REST API'ler) buraya
**eklenmez** — eklenirse soyutlama yalan söyler: "dialect" artık lehçe değil, kaynak türü
olur ve her komut kendi istisnasını taşımaya başlar. O kaynaklar kendi aracını yazar; ortak
olan **kod değil sözleşmedir**: tek kimlik yolu · izin listesi tabanlı salt-okuma · çıkış
kodu 0/1/2 (2 = koşamadı, yeşil değil) · görünür retry.

`--read-only` **izin listesidir**, yasak listesi değil: yalnız `SELECT` ve `WITH…SELECT`
geçer; çoklu ifade, `EXEC`/`sp_executesql`, `SELECT … INTO`, `SET`, `DECLARE` ve tanınmayan
her başlangıç reddedilir. Sebep: yasak listesi listede olmayan yazma yolunu kaçırır ve
kaçırdığını söylemez. Red mesajı neyi tanımadığını yazar.

**Neden bu dört özellik:** BKM semantik katmanında (`D:\Dev\pusula/sema`) öğrenilen şema
gerçekleri elle yazılmış bir Python koşucusuyla yeniden koşuluyordu; `assert` bunu araç
düzeyine taşır. `lookup` ise "kod listesi elle yazılmaz, lookup tablosundan okunur"
kuralının aracıdır — ölçülen vaka: 20 kod belgelenmişti, lookup'ta 34 vardı ve
belgelenmeyenlerden biri 364.226 satırlık bir hareket tipiydi.

## Kurulum

```bash
cd D:/Dev/sqlcli
dotnet pack
dotnet tool install -g --add-source ./nupkg SqlCli
```

Veya geliştirirken doğrudan:
```bash
cd D:/Dev/sqlcli
dotnet run -- <komut> [args]
```

## Bağlantı Yapılandırması

### `sqlcli.json` (multi-profile)

```jsonc
{
  "Profiles": {
    "mosaik": "Server=BT-FIKRI\\SQLEXPRESS;Database=Mosaik;User Id=sa;Password=...;TrustServerCertificate=true;",
    "zirve":  "Server=192.168.40.25,64507;Database=BKM_GENEL;User Id=rapor_readonly;Password=...;TrustServerCertificate=true;",
    "bkm":    "Server=BKM-PROD;Database=BKMDATA;Integrated Security=true;TrustServerCertificate=true;"
  },
  "DefaultProfile": "mosaik",
  "SqlDir": "./Database"
}
```

Kullanım:
```bash
sqlcli status                      # default profile (mosaik)
sqlcli --profile zirve status      # explicit profile
sqlcli --profile bkm rows
```

### Bağlantı önceliği

1. `SQLCLI_CONN` env var
2. `--conn "..."` argümanı
3. `--profile <name>` → sqlcli.json > Profiles[name]
4. sqlcli.json > Connection (legacy single)
5. sqlcli.json > Profiles[DefaultProfile]
6. appsettings.json > ConnectionStrings:DefaultConnection veya Default

## Komutlar

### Schema yönetimi

| Komut | Açıklama |
|---|---|
| `migrate` | Sıralı NN_*.sql migration veya legacy schema_all.sql |
| `seed` | seed.sql, seed_core.sql çalıştır |
| `script <file>` | Tek SQL dosyası (`--tolerant` opsiyonu var) |
| `migrate-status` | Disk dosyaları vs `__MigrationHistory` tablo karşılaştırma |
| `migrate-create <name> [--template=table\|column\|index\|seed\|bare]` | Sıradaki numara + idempotent şablon |

### Query

| Komut | Açıklama |
|---|---|
| `query "SQL"` | SQL çalıştır, `--format=table\|json\|csv\|md` |
| `top <table> [N=10]` | `SELECT TOP N * FROM ...` shortcut |
| `save <name> "SQL"` | Sık query'i isim ile kaydet |
| `run <name>` | Kaydedilmişi çalıştır |
| `saved-list` / `saved-delete <name>` | Kayıt yönetimi |
| `history [--take=30]` | Son çalışan query'ler (~/.sqlcli/history.log) |

### Schema keşif

| Komut | Açıklama |
|---|---|
| `status` | Sunucu/DB/kullanıcı/versiyon |
| `tablolar` / `tables` | Tablo listesi |
| `rows` / `satir-sayisi` | Tüm tablo satır sayıları (DESC sıralı) |
| `fk-kontrol` | Foreign key listesi (kolon detaylı) |
| `indexler` / `indexes` | Index listesi |
| `baglanti` | Aktif bağlantı + profil bilgisi |
| `profiles` | sqlcli.json'da tanımlı tüm profiller |
| `describe <table>` / `desc <table>` | Kolonlar + tip + nullable + default + PK + FK + index + satır sayısı (HER ŞEY) |
| `search <pattern> [--scope=tables\|columns\|both]` | Tablo/kolon adı arama (LIKE wildcard, `*` → `%`) |
| `relationships <table>` / `rel <table>` | Gelen + giden FK'lar |

### ETL — `copy` (SQL Server → SQL Server, SqlBulkCopy)

```bash
sqlcli copy --from "<kaynak>" --to "<hedef>" --query "SELECT Id, Deger FROM dbo.X WHERE Tarih >= @Bas"   --param Bas:date=2026-01-01 --table dbo.Hedef --batch 50000 --check-constraints --fire-triggers --format json
```

| Bayrak | Anlamı |
|---|---|
| `--read-only` | **Varsayılan AÇIK** (v2.4). Kaynak sorgu salt-okuma muhafızından geçmezse kaynağa hiçbir şey gitmeden red, exit 2. Kapatmak yalnız `--read-only=false` (uyarı basılır); `SQLCLI_READONLY=1` varken kapatılamaz. |
| `--param ad[:tip]=deger` | Kaynak sorguya adlı parametre (query/assert ile aynı kurallar). |
| `--check-constraints` · `--fire-triggers` | SqlBulkCopy varsayılanı CHECK/FK **denetlemez**, INSERT tetikleyicisi **çalıştırmaz**. Hedefte kısıt/mühür tetikleyicisi varsa aç. |
| `--atomic` | TRUNCATE + yükleme tek hedef transaction'ı: hata olursa hedef **önceki hâline** döner. `--parallel` ile olmaz. |
| `--truncate` · `--batch` · `--tablock` (açık) · `--parallel N --partkey <kolon>` · `--timeout` | Önceki gibi. |
| `--format table\|json` | `json`: stdout'a **tek nesne** — `durum`, `satir` (bu koşuda **aktarılan** satır; tablo toplamı değil), `sure_ms`, `kaynak_sunucu`, `kaynak_db`, `hedef_sunucu`, `hedef_db`, `hedef_toplam`, `read_only`, `check_constraints`, `fire_triggers`, `atomic`, `truncate`. Hata: `{"durum":"HATA","hata_no":547,"hata":"..."}`. Banner + ilerleme stderr'e. `table`: son satır `SONUC satir=N sure_ms=M`. |

**Çıkış:** 0 aktarım tamam · 2 KOŞAMADI (muhafız reddi, bağlantı, SQL hatası — kısıt ihlali 547 dahil). Mesajda SQL hata numarası yazar.

**Yarım yükleme (`--atomic` YOKKEN):** TRUNCATE ayrı bağlantıda hemen işlenir; yükleme her `--batch` satırda bir işlenir. Hata olursa
tablo boşaltılmış ve hataya kadar tamamlanan batch'ler hedefte **kalmış** olur (hata mesajı bunu söyler). İstenmiyorsa `--atomic`.

### Performance

| Komut | Açıklama |
|---|---|
| `explain "SQL" [--mode=estimated\|actual]` | Execution plan XML |
| `active-sessions` | Aktif oturumlar + blocking + sorgu metni |
| `slow-queries [--top=20] [--by=avg\|total\|cpu\|reads\|executions]` | Plan cache'den en yavaşlar |
| `kill <session_id> [--yes]` | Oturum sonlandır (onay sorar) |

## Çıktı formatları

```bash
sqlcli rows --format table   # Spectre.Console renkli tablo (varsayılan, terminal)
sqlcli rows --format json    # Pretty-print JSON (script/Claude-friendly)
sqlcli rows --format csv     # Standart CSV
sqlcli rows --format md      # Markdown tablo (rapor/dokümana yapıştır)
```

## Örnekler

```bash
# Multi-profile keşif
sqlcli --profile zirve top vw_PersonelDepartman 5
sqlcli --profile mosaik describe Notifications

# Migration disiplini
sqlcli migrate-create AddUserPhone --template=column
sqlcli migrate-status

# Saved queries
sqlcli save active-users "SELECT * FROM Users WHERE IsActive = 1"
sqlcli run active-users

# Performans incelemesi
sqlcli active-sessions
sqlcli slow-queries --top 5 --by cpu

# Schema arama
sqlcli search "%email%" --scope columns
sqlcli relationships ReportCatalog
```

## State / dosya yapısı

| Yer | Ne |
|---|---|
| `sqlcli.json` (proje kökü) | Connection profilleri + SqlDir |
| `~/.sqlcli/saved.json` | Kaydedilmiş sorgular |
| `~/.sqlcli/history.log` | Son 1000 query (zaman + profil + SQL) |

## Güvenlik

- **`query --max-rows`** (default 1000): SELECT'lere otomatik server-side TOP wrap. SELECT @@VAR / WITH CTE / INSERT-UPDATE-DELETE dokunmaz
- **Password masking:** `baglanti` çıktısında `Password=***`
- **Tolerant mode:** Sadece bilinen idempotent hata kodlarında devam (2714/1913/2627/2601/1505/2705)
- **`kill`** confirm prompt'lı (--yes ile bypass)
- **`copy --read-only` varsayılan açık** (v2.4): kaynağı tipik olarak üretim ERP'si olan bir araç, kaynağa yazabiliyorsa tek yanlış sorgu geri alınamaz

## Sürüm Notları

### v2.4 (2026-10-09) — `copy` sertleştirmesi (FIFO plan 001 S paketi)
- `--read-only` **varsayılan açık**; `--param`; `--check-constraints` / `--fire-triggers`; `--atomic`; `--format json`.
- `satir` artık bu koşuda **aktarılan** sayı (`SqlBulkCopy.RowsCopied64`); eskiden hedef tablonun tamamı sayılıyordu (birden çok yükleme tutan tabloda yanlış).
- Kaynak ve hedef `@@SERVERNAME` / `DB_NAME()` çıktıda (aynı DB adı iki sunucuda olabilir — yanlış sunucuya yazma görünür olsun).
- Her hata exit 2 (eskiden yakalanmayan istisna exit 1 idi).
- `--format json` istenen **her** komutta banner stderr'e gider: stdout doğrudan parse edilir.
- İlk test projesi: `tests/SqlCli.Tests` — muhafız birim testleri + `copy` uçtan uca (gerçek SQL Server, `SQLCLI_TEST_SQL`, yoksa `localhost`). Sunucuya ulaşılamazsa testler kırmızı olur, atlanmaz. `dotnet test tests/SqlCli.Tests`

### v2.3 (2026-09-26) — Sürüm muhafızı
- **Eski kopya çalışmaz:** her açılışta çalışan sürüm, kurulu global tool'un sürümüyle (ve `SQLCLI_SOURCE` verilmişse o kaynaktaki `sqlcli.csproj` ile) karşılaştırılır. Eskiyse kırmızı hata + güncelleme komutu, **exit 2**. Acil kaçış: `SQLCLI_SKIP_VERSION_CHECK=1` (uyarıyla devam). `surum`/`version`/`--version` muaf.
- "Makinede çalışmış en yüksek sürüm" işareti **bilerek yok**: kurulmamış bir Debug derlemesinin tek çalıştırılışı kurulu aracı kilitlerdi.
- **`surum` komutu:** kurulu sürüm + derlendiği commit; `--kaynak <klon>` (ya da `SQLCLI_KAYNAK`) ile kaynağın KOD commit'lerinin gerisinde mi (0 güncel · 1 geride · 2 kontrol edilemedi), `--uzak` önce fetch. `surum` komutu olmayan ≤2.2 kurulumlar için `tools/surum-kontrol.ps1`.
- **Ağ/WebDAV sürücüsünde askıda kalma düzeltildi:** çalışma dizini WebDAV iken (ör. `D:\` → DavWWWRoot) her komut işini bitirip süreç kapanmıyordu (host'un yapılandırma dosyası izleyicisi). CLI'da izleme kapatıldı (`DOTNET_hostBuilder__reloadConfigOnChange=false`).
- Açılış satırı sürümü derlemeden okur (eskiden sabit "v2.2").

### v2.1 (2026-07-02) — Çok-sağlayıcı
- **PostgreSQL desteği** (Npgsql): connection string'ten otomatik sağlayıcı tespiti (`Host=`/`postgres://` → PG).
- **Dialect soyutlaması** (`Core/Dialects.cs`): `ISqlDialect` + SqlServerDialect (orijinal SQL, regresyonsuz) + PostgresDialect (information_schema/pg_catalog). Oracle için seam hazır.
- **Yeni komutlar:** `tables-empty` (boş tablolar), `seed-demo`, `export` (SELECT→dosya json/csv/md), `import` (JSON→tablo INSERT), `diff` (iki profil: tablo listesi + opsiyonel sorgu satır-set farkı).
- Perf DMV + `copy` SQL Server'a özel kaldı (PG'de net mesajla çıkar).
- Bespoke PG migration CLI'larını (Operax.Cli, hal/Hks.Cli) tek araçta topladı.

### v2.0 (2026-05-08)
- Modüler refactor: Core/ + Commands/ ayrımı
- Cocona CLI framework (subcommand routing + auto-help)
- Spectre.Console (renkli tablo + tree + prompt + markup)
- Multi-profile connection (sqlcli.json > Profiles)
- Tier 1: describe, search, relationships, top
- Tier 2: migrate-status, migrate-create
- Tier 3: connection profiles
- Tier 4: save/run/history, --format=table|json|csv|md
- Tier 5: explain, active-sessions, slow-queries, kill

### v1.0
- migrate, seed, script, query, status, tablolar, rows, fk-kontrol, indexler, baglanti
