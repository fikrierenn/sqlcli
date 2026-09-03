# sqlcli v2.2

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

## Sürüm Notları

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
