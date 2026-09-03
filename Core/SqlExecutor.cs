using System.Data;
using System.Data.Common;
using Dapper;
using Spectre.Console;

namespace SqlCli.Core;

// Script + query çalıştırma + tolerant mode + batch parsing (dialect-aware, çok-sağlayıcı).
public static class SqlExecutor
{
    public sealed record ScriptResult(int OkCount, int WarnCount, int FailCount);

    public static async Task<ScriptResult> RunScriptAsync(string connStr, string scriptPath, bool tolerant)
    {
        if (!File.Exists(scriptPath))
        {
            AnsiConsole.MarkupLine($"[red]Hata: Dosya bulunamadı: {Markup.Escape(scriptPath)}[/]");
            return new(0, 0, 1);
        }

        AnsiConsole.MarkupLine($"\n[cyan]Script:[/] {Markup.Escape(Path.GetFileName(scriptPath))}");
        var script = await File.ReadAllTextAsync(scriptPath);
        var dialect = ISqlDialect.Detect(connStr);
        var batches = dialect.SplitBatches(script);

        await using var conn = await ConnectionResolver.OpenAsync(connStr);
        int ok = 0, warn = 0, fail = 0;

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                cmd.CommandType = CommandType.Text;
                await cmd.ExecuteNonQueryAsync();
                ok++;
            }
            catch (DbException ex) when (tolerant && dialect.IsIdempotentWarn(ex))
            {
                AnsiConsole.MarkupLine($"  [yellow][[UYARI {dialect.ErrorCode(ex)}]][/] {Markup.Escape(ex.Message.Split('\n')[0])}");
                warn++;
            }
            catch (DbException ex) when (tolerant)
            {
                AnsiConsole.MarkupLine($"  [red][[HATA {dialect.ErrorCode(ex)}]][/] {Markup.Escape(ex.Message.Split('\n')[0])}");
                fail++;
            }
            catch (DbException ex)
            {
                AnsiConsole.MarkupLine($"\n[red]HATA: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"  Hata Kodu: {dialect.ErrorCode(ex)}, Batch: {ok + warn + fail + 1}");
                var snippet = batch.Trim();
                AnsiConsole.MarkupLine($"  Batch ilk 200 karakter: {Markup.Escape(snippet[..Math.Min(200, snippet.Length)])}");
                throw;
            }
        }

        var color = fail == 0 ? "green" : "yellow";
        AnsiConsole.MarkupLine($"[{color}]Tamamlandı — tamam:{ok} uyarı:{warn} hata:{fail}[/]");
        return new(ok, warn, fail);
    }

    public static async Task<DataTable> RunQueryAsync(string connStr, string sql)
        => await RunQueryAsync(connStr, sql, null, 0, 0, null);

    /// <summary>
    /// Sorguyu adlı parametrelerle, komut zaman aşımıyla ve SINIRLI retry ile koşturur.
    /// </summary>
    /// <param name="parameters">`@ad` parametreleri — string birleştirme yerine (injection +
    /// tarih biçimi tuzağı). null ise parametresiz.</param>
    /// <param name="timeoutSeconds">0 = sağlayıcı varsayılanı.</param>
    /// <param name="retry">Geçici hatada ek deneme sayısı (0 = yok). Her deneme görünür.</param>
    public static async Task<DataTable> RunQueryAsync(
        string connStr, string sql, object? parameters, int timeoutSeconds, int retry,
        Action<string>? bildir)
    {
        bildir ??= m => AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(m)}[/]");
        return await TransientErrors.YenidenDeneAsync(async () =>
        {
            await using var conn = await ConnectionResolver.OpenAsync(connStr);
            var cmd = new CommandDefinition(
                sql, parameters,
                commandTimeout: timeoutSeconds > 0 ? timeoutSeconds : null);
            await using var reader = await conn.ExecuteReaderAsync(cmd);
            var table = new DataTable();
            table.Load(reader);
            return table;
        }, retry, bildir);
    }

    /// <summary>İlk satır/ilk kolon — assert ve sayım için. NULL/satırsız → null.</summary>
    public static async Task<object?> ScalarAsync(
        string connStr, string sql, object? parameters = null, int timeoutSeconds = 0,
        int retry = 0, Action<string>? bildir = null)
    {
        bildir ??= m => AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(m)}[/]");
        return await TransientErrors.YenidenDeneAsync(async () =>
        {
            await using var conn = await ConnectionResolver.OpenAsync(connStr);
            var cmd = new CommandDefinition(
                sql, parameters,
                commandTimeout: timeoutSeconds > 0 ? timeoutSeconds : null);
            var deger = await conn.ExecuteScalarAsync<object?>(cmd);
            return deger is DBNull ? null : deger;
        }, retry, bildir);
    }
}
