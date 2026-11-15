using Npgsql;

namespace SqlCli.Core;

// PostgreSQL → PostgreSQL ikili COPY (SqlBulkCopy karşılığı).
//   kaynak: COPY (<sorgu>) TO STDOUT (FORMAT BINARY)  →  hedef: COPY tablo (kolonlar) FROM STDIN (FORMAT BINARY)
// Ham akış borulanır; satırlar istemciye tip dönüşümüyle çıkmaz. Sonuç olarak kolon TİPLERİ kaynak ile hedefte
// BİREBİR aynı olmalı (int4 → int8 ikili COPY'de hata verir); kolonlar ada göre eşlenir.
// Hedefte yükleme tek transaction'dır: COPY zaten tek ifadedir, TRUNCATE da aynı transaction'a girer →
// hata olursa hedef önceki haline döner (SQL Server'daki --atomic davranışı PG'de her zaman geçerli).
public static class PostgresCopy
{
    public sealed record CopyResult(long Rows, string SourceServer, string SourceDb, string TargetServer, string TargetDb, long TargetTotal);

    public static async Task<CopyResult> RunAsync(string from, string to, string query, string table,
                                             bool truncate, int timeoutSeconds, bool readOnlyMode)
    {
        await using var src = new NpgsqlConnection(ConnectionResolver.ExpandEnv(from));
        await using var dst = new NpgsqlConnection(ConnectionResolver.ExpandEnv(to));
        await src.OpenAsync();
        await dst.OpenAsync();

        var trimmedQuery = query.Trim().TrimEnd(';', ' ', '\r', '\n', '\t');
        if (SqlGuard.StripCommentsAndText(trimmedQuery).Contains(';'))
            throw new ArgumentException("copy kaynağı tek bir SELECT olmalı (`;` ile ayrılmış birden fazla ifade yok).");

        // Kaynakta salt-okuma transaction'ı: muhafıza ek olarak sunucu tarafında da yazma imkânsız.
        await using var srcTx = await src.BeginTransactionAsync();
        if (readOnlyMode) await ExecAsync(src, "SET TRANSACTION READ ONLY", timeoutSeconds);

        // Kolon adları: kaynağın şeklini 0 satırlık sorguyla öğren → hedefte ada göre eşle.
        var columns = new List<string>();
        using (var probe = src.CreateCommand())
        {
            probe.CommandText = $"SELECT * FROM ( {trimmedQuery} ) q LIMIT 0";
            if (timeoutSeconds > 0) probe.CommandTimeout = timeoutSeconds;
            await using var r = await probe.ExecuteReaderAsync();
            for (int i = 0; i < r.FieldCount; i++) columns.Add("\"" + r.GetName(i).Replace("\"", "\"\"") + "\"");
        }
        if (columns.Count == 0) throw new InvalidOperationException("kaynak sorgu kolon döndürmüyor.");

        await using var dstTx = await dst.BeginTransactionAsync();
        try
        {
            if (truncate) await ExecAsync(dst, $"TRUNCATE TABLE {table}", timeoutSeconds);
            var before = await CountAsync(dst, table, timeoutSeconds);

            await using (var reading = await src.BeginRawBinaryCopyAsync($"COPY ({trimmedQuery}) TO STDOUT (FORMAT BINARY)"))
            await using (var writing = await dst.BeginRawBinaryCopyAsync(
                             $"COPY {table} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)"))
            {
                await reading.CopyToAsync(writing);
            }

            var after = await CountAsync(dst, table, timeoutSeconds);
            await dstTx.CommitAsync();
            await srcTx.RollbackAsync();
            return new CopyResult(after - before, src.Host ?? "?", src.Database ?? "?", dst.Host ?? "?", dst.Database ?? "?", after);
        }
        catch
        {
            // Rollback en iyi çabayla: bağlantı COPY hatasıyla zaten koptuysa orijinal hata ezilmesin.
            try { await dstTx.RollbackAsync(); } catch { }
            throw;
        }
    }

    private static async Task ExecAsync(NpgsqlConnection c, string sql, int timeoutSeconds)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, string table, int timeoutSeconds)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
