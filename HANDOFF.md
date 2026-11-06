# HANDOFF — sqlcli (02.07.2026)

Bu oturumda sqlcli **v2.0 → v2.1: çok-sağlayıcı (SqlServer + PostgreSQL)** yapıldı.
Bağlam: BKM görev-tanımları projesi Mosaik'e taşınırken sqlcli araç olarak kullanıldı (`D:\Dev\gorevtanimlari\bkm`).

## Bu oturumda yapıldı
### Yeni
- `Core/Dialects.cs` — `ISqlDialect` + `SqlServerDialect` (orijinal SQL, regresyonsuz) + `PostgresDialect` (information_schema/pg_catalog). **Oracle = seam** (yeni ISqlDialect impl'i ekle: katalog ALL_TABLES/USER_TAB_COLUMNS, sayfalama FETCH FIRST).
- `Commands/DataCommands.cs` — `tables-empty`, `seed-demo`, `export` (SELECT→json/csv/md dosya), `import` (JSON→tablo), `diff` (iki profil).

### Değişen
- `sqlcli.csproj` — Npgsql eklendi, Version 2.1.0.
- `Core/ConnectionResolver.cs` — dialect detection (Host= → PG), ResolvedConnection.Dialect, OpenAsync→DbConnection.
- `Core/SqlExecutor.cs` — DbException + dialect batch/warn.
- `Commands/QueryCommands.cs` — dialect limit/top.
- `Commands/IntrospectCommands.cs` — dialect-supplied introspection SQL.
- `Commands/PerfCommands.cs` + `EtlCommands.cs` — MSSQL-only guard (DMV/SqlBulkCopy).
- `Program.cs` (banner) · `README.md` (v2.1 notları + canonical/kopya uyarısı).

## Durum
- Build **0 hata**. Global tool rebuild → **v2.1.0** kuruldu.
- **SQL Server yolu regresyonsuz** (Mosaik'te test: status/describe/tables-empty/export). **Postgres yolu CANLI TEST YOK** (ortamda PG DB yok — PG DB çıkınca doğrula).
- **COMMIT ATILMADI** (git repo, kullanıcı kararı).

## Not
- Canonical kopya = **`D:\Dev\sqlcli`**. `MIMBAL\tools\sqlcli` + `fifo\sqlcli` ESKİ birebir kopya (drift) — fork'lama, global tool + kendi sqlcli.json kullan.
- `--profile` komuttan SONRA gelir (`sqlcli status --profile x`).

## Sıradaki
- PG canlı doğrulama (bir Postgres DB ile export/import/introspection).
- Oracle dialect (gerçek Oracle hedefi çıkınca).

## v2.7.2 (06.11.2026) — PR #4 Codex P2 bulguları
- Bulgu 1 (`Core/PostgresCopy.cs`): `--truncate` + kaynak hedefi okuyor → kendi kilidinde takılma. Çözüm: `pg_locks` ile kaynak pid'inin hedef tablodaki kilidi TRUNCATE'ten önce ölçülür, `InvalidOperationException` ("kendi kaynağını boşaltırdı").
- Bulgu 2 (`Core/Dialects.cs`): analiz rollback'i sequence/dblink/NOTIFY'yi geri almaz. Çözüm (reddet, uyarıyla geçme): `Core/YanEtkiDenetimi.cs` belirteç listesi; `--allow-side-effects` ile aşılır. Sınır README'de: UDF/tetikleyici/identity görülmez.
- Sürüm tag'i (`v2.7.2`) merge sonrası atılır.

## v2.7.1 (05.11.2026) — PostgreSQL perf + copy
- `explain` (`--analyze`, `--format text|json`), `active-sessions`, `slow-queries`, `kill` (`--terminate`) PG'de çalışır; SQL `ISqlDialect`'te (`Core/Dialects.cs`).
- `copy` PG→PG: `Core/PostgresCopy.cs` (ham ikili COPY, tek hedef transaction). Karışık çift reddedilir.
- Doğrulama (+ParityTests: SQLCLI_TEST_PG): geçici PG 17 cluster'ında (port 55477, tek kullanımlık) elle; Nexus dev DB (55433) o sırada kapalıydı, dokunulmadı. Sunucusuz birim testi: `DialectPerfTests`.
- Açık: `copy` PG'de `--param`/`--parallel` yok (COPY parametre almaz). Gerçek Nexus DB'ye karşı `explain`/`active-sessions` salt-okuma doğrulaması yapılmadı (DB kapalıydı).
- Sürüm tag'i (`v2.7.1`) merge sonrası atılır (README "Yeni sürüm yayınlama"); v2.6.2 tag'i de hâlâ atılmadı.
