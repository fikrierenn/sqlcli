using Cocona;
using Microsoft.Data.SqlClient;
using Spectre.Console;

namespace SqlCli.Commands;

// Cross-server ETL: kaynak SELECT sonucunu hedef tabloya bulk-copy eder (linked server gerekmez).
//   sqlcli copy --from "<kaynak>" --to "<hedef>" --query "SELECT ..." --table dbo.Hedef --truncate
// Hizli yukleme: hedef heap (clustered index yok) + SIMPLE recovery + TABLOCK + --parallel.
//   Paralel: heap'e TABLOCK ile cok sayida BU-lock UYUMLU stream yazilabilir.
//   --parallel N --partkey <sayisal_kolon> => kaynak ABS(kolon)%N ile bolunur, N es-zamanli stream.
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
        [Option("timeout", Description = "Komut + bulk timeout (sn, 0=sinirsiz)")] int timeout = 0)
    {
        if (parallel > 1 && string.IsNullOrWhiteSpace(partkey))
            throw new CommandExitedException("--parallel > 1 icin --partkey <sayisal_kolon> zorunlu.", 1);

        // SqlBulkCopy yalnız SQL Server. Her iki uç da SQL Server olmalı.
        if (SqlCli.Core.ISqlDialect.Detect(from).Provider != SqlCli.Core.DbProvider.SqlServer
            || SqlCli.Core.ISqlDialect.Detect(to).Provider != SqlCli.Core.DbProvider.SqlServer)
            throw new CommandExitedException("copy (SqlBulkCopy) yalnız SQL Server → SQL Server destekler. Postgres için ileride COPY protokolü eklenecek.", 1);

        // Truncate bir kez (paralel stream'ler temizlemez)
        if (truncate)
        {
            await using var d0 = new SqlConnection(to);
            await d0.OpenAsync();
            using var tc = d0.CreateCommand();
            tc.CommandText = $"TRUNCATE TABLE {table}";
            tc.CommandTimeout = timeout;
            await tc.ExecuteNonQueryAsync();
            AnsiConsole.MarkupLine($"[grey]TRUNCATE {Markup.Escape(table)}[/]");
        }

        var opts = SqlBulkCopyOptions.KeepNulls;
        if (tablock) opts |= SqlBulkCopyOptions.TableLock;  // heap'te BU-lock'lar uyumlu → paralele izin verir

        if (parallel <= 1)
        {
            await StreamOneAsync(from, to, query, table, opts, batch, timeout, "");
        }
        else
        {
            var tasks = new List<Task>();
            for (int i = 0; i < parallel; i++)
            {
                int idx = i;
                // Kullanici sorgusunu sar, partition predikati uygula (ic WHERE'den bagimsiz)
                var pq = $"SELECT * FROM ( {query} ) q WHERE ABS(CAST(q.[{partkey}] AS BIGINT)) % {parallel} = {idx}";
                tasks.Add(StreamOneAsync(from, to, pq, table, opts, batch, timeout, $"p{idx}"));
            }
            await Task.WhenAll(tasks);
        }

        // Kesin sayim hedeften
        await using var dst = new SqlConnection(to);
        await dst.OpenAsync();
        using var cc = dst.CreateCommand();
        cc.CommandText = $"SELECT COUNT_BIG(*) FROM {table}";
        cc.CommandTimeout = timeout;
        var n = await cc.ExecuteScalarAsync();
        AnsiConsole.MarkupLine($"[green]OK[/] {Markup.Escape(table)} <- toplam satir: [cyan]{n}[/]");
    }

    private static async Task StreamOneAsync(string from, string to, string query, string table,
                                             SqlBulkCopyOptions opts, int batch, int timeout, string label)
    {
        await using var src = new SqlConnection(from);
        await src.OpenAsync();
        await using var dst = new SqlConnection(to);
        await dst.OpenAsync();

        using var cmd = src.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = timeout;
        await using var reader = await cmd.ExecuteReaderAsync();

        using var bulk = new SqlBulkCopy(dst, opts, null)
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
        bulk.SqlRowsCopied += (_, e) => AnsiConsole.MarkupLine($"[grey]  {pfx}{e.RowsCopied} satir...[/]");
        await bulk.WriteToServerAsync(reader);
    }
}
