using System.Diagnostics;
using System.Text.Json;
using Cocona;
using Dapper;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// Cross-server ETL: kaynak SELECT sonucunu hedef tabloya bulk-copy eder (linked server gerekmez).
//   sqlcli copy --from "<kaynak>" --to "<hedef>" --query "SELECT ..." --table dbo.Hedef --truncate
// Hizli yukleme: hedef heap (clustered index yok) + SIMPLE recovery + TABLOCK + --parallel.
//   Paralel: heap'e TABLOCK ile cok sayida BU-lock UYUMLU stream yazilabilir.
//   --parallel N --partkey <sayisal_kolon> => kaynak ABS(kolon)%N ile bolunur, N es-zamanli stream.
//
// v2.4 (FIFO S paketi):
//   --read-only VARSAYILAN ACIK: kaynak sorgu SqlGuard izin listesinden gecmezse kaynaga HIC
//     gitmeden reddedilir. Kapatmak yalniz acikca `--read-only=false` ile (uyari basilir);
//     SQLCLI_READONLY=1 ise kapatilamaz. Neden varsayilan acik: copy'nin kaynagi tipik olarak
//     uretim ERP'si; hedefe yazan arac kaynaga da yazabiliyorsa tek yanlis sorgu geri alinamaz.
//   --param ad[:tip]=deger : kaynak sorguya adli parametre (query/assert ile ayni kurallar).
//   --check-constraints / --fire-triggers : SqlBulkCopy varsayilani CHECK/FK denetlemez ve
//     tetikleyici calistirmaz (Microsoft belgesi). Hedefte kisit/muhur tetikleyicisi varsa ac.
//   --atomic : TRUNCATE + yukleme TEK hedef transaction'i; hata olursa hedef ONCEKI haline doner.
//   --format json : stdout'a TEK JSON nesnesi (satir, sure_ms, kaynak/hedef sunucu+db...).
//     Ilerleme ve banner stderr'e gider; stdout dogrudan parse edilebilir.
//
// HATA SOZLESMESI: 0 = aktarim tamam · 2 = KOSAMADI (muhafiz reddi, baglanti, SQL hatasi,
//   kisit ihlali 547 dahil). Hata mesajinda SQL hata numarasi yazar.
// YARIM YUKLEME (--atomic YOKKEN): TRUNCATE ayri baglantida hemen islenir; yukleme her
//   --batch satirda bir islenir. Hata olursa: tablo bosaltilmis + hata anina kadar tamamlanan
//   batch'ler hedefte KALIR. Yarim yukleme istenmiyorsa --atomic kullan (paralelle birlikte olmaz).
public class EtlCommands
{
    [Command("copy", Description = "Kaynak SELECT sonucunu hedef tabloya bulk-copy eder (cross-server ETL)")]
    public async Task Copy(
        [Option("from", Description = "Kaynak connection string")] string from,
        [Option("to", Description = "Hedef connection string")] string to,
        [Option("query", Description = "Kaynak SELECT (kolon adlari hedef tablo kolonlariyla eslesmeli)")] string query,
        [Option("table", Description = "Hedef tablo (schema.tablo)")] string table,
        [Option("truncate", Description = "Hedef tabloyu once temizle")] bool truncate = false,
        [Option("batch", Description = "Bulk copy batch boyutu")] int batch = 100000,
        [Option("tablock", Description = "TABLOCK (minimal logging, hizli) — default acik")] bool tablock = true,
        [Option("parallel", Description = "Es-zamanli stream sayisi (heap+TABLOCK gerekir). >1 ise --partkey zorunlu")] int parallel = 1,
        [Option("partkey", Description = "Paralel bolme icin sayisal kolon (ABS(kolon)%parallel)")] string? partkey = null,
        [Option("timeout", Description = "Komut + bulk timeout (sn, 0=sinirsiz)")] int timeout = 0,
        [Option("read-only", Description = "Kaynak sorgu salt-okuma muhafizindan gecmeli — VARSAYILAN ACIK; kapatmak: --read-only=false")] bool readOnly = true,
        [Option("param", Description = "Kaynak sorguya adli parametre: ad[:tip]=deger (tekrarlanabilir)")] string[]? param = null,
        [Option("check-constraints", Description = "Hedefte CHECK/FK kisitlarini denetle (SqlBulkCopy varsayilani denetlemez)")] bool checkConstraints = false,
        [Option("fire-triggers", Description = "Hedefte INSERT tetikleyicilerini calistir (varsayilan calismaz)")] bool fireTriggers = false,
        [Option("atomic", Description = "TRUNCATE + yukleme tek hedef transaction'i: hata olursa hedef degismez (paralelle olmaz)")] bool atomic = false,
        [Option("format", Description = "Cikti: table|json (json: stdout'a tek nesne, ilerleme stderr'e)")] string format = "table")
    {
        var json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        if (!json && !string.Equals(format, "table", StringComparison.OrdinalIgnoreCase))
            Kosamadi(json, $"--format yalniz table|json olabilir: '{format}'");

        if (parallel > 1 && string.IsNullOrWhiteSpace(partkey))
            Kosamadi(json, "--parallel > 1 icin --partkey <sayisal_kolon> zorunlu.");
        if (atomic && parallel > 1)
            Kosamadi(json, "--atomic tek transaction ister; --parallel > 1 ile birlikte kullanilamaz.");

        // SqlBulkCopy yalnız SQL Server. Her iki uç da SQL Server olmalı.
        if (ISqlDialect.Detect(from).Provider != DbProvider.SqlServer
            || ISqlDialect.Detect(to).Provider != DbProvider.SqlServer)
            Kosamadi(json, "copy (SqlBulkCopy) yalnız SQL Server → SQL Server destekler. Postgres için ileride COPY protokolü eklenecek.");

        // MUHAFIZ: kullanici sorgusuna (paralel sarmadan ONCE) ve kaynaga hicbir sey gitmeden.
        var saltOkuma = SqlGuard.SaltOkumaAcikMi(readOnly);
        if (!saltOkuma)
            Log(json, "[yellow]UYARI:[/] --read-only=false — kaynak sorgu salt-okuma muhafizindan GECIRILMIYOR.");
        var red = SqlGuard.Denetle(query, readOnly);
        if (red != null) Kosamadi(json, red);

        var opts = SqlBulkCopyOptions.KeepNulls;
        if (tablock) opts |= SqlBulkCopyOptions.TableLock;  // heap'te BU-lock'lar uyumlu → paralele izin verir
        if (checkConstraints) opts |= SqlBulkCopyOptions.CheckConstraints;
        if (fireTriggers) opts |= SqlBulkCopyOptions.FireTriggers;

        var sure = Stopwatch.StartNew();
        try
        {
            var dp = ParamsCommands.Build(param);
            var (kSunucu, kDb) = await KimlikAsync(from);
            var (hSunucu, hDb) = await KimlikAsync(to);
            Log(json, $"[grey]kaynak: {Markup.Escape(kSunucu)} / {Markup.Escape(kDb)}  →  hedef: {Markup.Escape(hSunucu)} / {Markup.Escape(hDb)} · {Markup.Escape(table)}[/]");

            long satir;
            if (atomic)
            {
                await using var dst = new SqlConnection(to);
                await dst.OpenAsync();
                await using var tx = (SqlTransaction)await dst.BeginTransactionAsync();
                if (truncate) await TruncateAsync(dst, tx, table, timeout, json);
                satir = await StreamOneAsync(from, dst, tx, query, dp, table, opts, batch, timeout, "", json);
                await tx.CommitAsync();
            }
            else
            {
                if (truncate)
                {
                    // Truncate bir kez (paralel stream'ler temizlemez)
                    await using var d0 = new SqlConnection(to);
                    await d0.OpenAsync();
                    await TruncateAsync(d0, null, table, timeout, json);
                }

                if (parallel <= 1)
                {
                    await using var dst = new SqlConnection(to);
                    await dst.OpenAsync();
                    satir = await StreamOneAsync(from, dst, null, query, dp, table, opts, batch, timeout, "", json);
                }
                else
                {
                    var tasks = new List<Task<long>>();
                    for (int i = 0; i < parallel; i++)
                    {
                        int idx = i;
                        // Kullanici sorgusunu sar, partition predikati uygula (ic WHERE'den bagimsiz)
                        var pq = $"SELECT * FROM ( {query} ) q WHERE ABS(CAST(q.[{partkey}] AS BIGINT)) % {parallel} = {idx}";
                        tasks.Add(Task.Run(async () =>
                        {
                            await using var dst = new SqlConnection(to);
                            await dst.OpenAsync();
                            return await StreamOneAsync(from, dst, null, pq, dp, table, opts, batch, timeout, $"p{idx}", json);
                        }));
                    }
                    satir = (await Task.WhenAll(tasks)).Sum();
                }
            }
            sure.Stop();

            // Bilgi: hedef tablonun TOPLAM satiri (aktarilan sayi DEGIL — tablo onceki
            // yuklemeleri de tutabilir). Aktarilan sayi `satir`dir (SqlBulkCopy.RowsCopied64).
            long hedefToplam;
            await using (var dc = new SqlConnection(to))
            {
                await dc.OpenAsync();
                hedefToplam = await dc.ExecuteScalarAsync<long>(
                    new CommandDefinition($"SELECT COUNT_BIG(*) FROM {table}", commandTimeout: timeout));
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    durum = "TAMAM",
                    satir,
                    sure_ms = sure.ElapsedMilliseconds,
                    kaynak_sunucu = kSunucu,
                    kaynak_db = kDb,
                    hedef_sunucu = hSunucu,
                    hedef_db = hDb,
                    tablo = table,
                    hedef_toplam = hedefToplam,
                    read_only = saltOkuma,
                    check_constraints = checkConstraints,
                    fire_triggers = fireTriggers,
                    atomic,
                    truncate,
                }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]OK[/] {Markup.Escape(table)} <- aktarilan: [cyan]{satir}[/] satir, {sure.ElapsedMilliseconds} ms (tablo toplami: {hedefToplam})");
                AnsiConsole.MarkupLine($"SONUC satir={satir} sure_ms={sure.ElapsedMilliseconds}");
            }
        }
        catch (CommandExitedException) { throw; }
        catch (Exception ex)
        {
            sure.Stop();
            var no = (ex as SqlException)?.Number ?? (ex.InnerException as SqlException)?.Number;
            var neden = atomic ? "hedef transaction geri alindi (--atomic): hedef degismedi"
                               : "YARIM YUKLEME OLABILIR: tamamlanan batch'ler (ve varsa TRUNCATE) hedefte kaldi";
            Kosamadi(json, $"{(no != null ? $"SQL {no}: " : "")}{ex.Message} — {neden}", no, sure.ElapsedMilliseconds);
        }
    }

    private static async Task<(string Sunucu, string Db)> KimlikAsync(string cs)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        var r = await c.QuerySingleAsync<(string, string)>("SELECT CAST(@@SERVERNAME AS nvarchar(256)), CAST(DB_NAME() AS nvarchar(256))");
        return r;
    }

    private static async Task TruncateAsync(SqlConnection c, SqlTransaction? tx, string table, int timeout, bool json)
    {
        using var tc = c.CreateCommand();
        tc.Transaction = tx;
        tc.CommandText = $"TRUNCATE TABLE {table}";
        tc.CommandTimeout = timeout;
        await tc.ExecuteNonQueryAsync();
        Log(json, $"[grey]TRUNCATE {Markup.Escape(table)}{(tx != null ? " (transaction icinde)" : "")}[/]");
    }

    private static async Task<long> StreamOneAsync(string from, SqlConnection dst, SqlTransaction? tx,
                                                   string query, DynamicParameters? dp, string table,
                                                   SqlBulkCopyOptions opts, int batch, int timeout, string label, bool json)
    {
        await using var src = new SqlConnection(from);
        await src.OpenAsync();
        await using var reader = await src.ExecuteReaderAsync(new CommandDefinition(query, dp, commandTimeout: timeout));

        using var bulk = new SqlBulkCopy(dst, opts, tx)
        {
            DestinationTableName = table,
            BatchSize           = batch,
            BulkCopyTimeout     = timeout,
            NotifyAfter         = batch * 5,
            EnableStreaming     = true
        };
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var col = reader.GetName(i);
            bulk.ColumnMappings.Add(col, col);
        }
        var pfx = string.IsNullOrEmpty(label) ? "" : $"[{label}] ";
        bulk.SqlRowsCopied += (_, e) => Log(json, $"[grey]  {Markup.Escape(pfx)}{e.RowsCopied} satir...[/]");
        await bulk.WriteToServerAsync(reader);
        return bulk.RowsCopied64;
    }

    /// <summary>Ilerleme/bilgi satiri. json kipinde stderr'e (stdout yalniz sonuc nesnesi).</summary>
    private static void Log(bool json, string markup)
    {
        if (json) Console.Error.WriteLine(Markup.Remove(markup));
        else AnsiConsole.MarkupLine(markup);
    }

    /// <summary>KOSAMADI: mesaj + cikis 2. json kipinde stdout'a da HATA nesnesi yazilir.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Kosamadi(bool json, string mesaj, int? hataNo = null, long sureMs = 0)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { durum = "HATA", hata_no = hataNo, hata = mesaj, sure_ms = sureMs }));
            Console.Error.WriteLine($"HATA: {mesaj}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]HATA:[/] {Markup.Escape(mesaj)}");
        }
        throw new CommandExitedException(2);
    }
}
