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
