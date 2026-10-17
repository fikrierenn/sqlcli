using System.Text;
using Cocona;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// decrypt — WITH ENCRYPTION ile şifreli SP / view / function / trigger tanımını çözer.
//
// Sadece SQL Server. Şifreli tanım sys.sysobjvalues.imageval (valclass=1) içindedir ve
// YALNIZ DAC (Dedicated Admin Connection) üzerinden okunur. Algoritma: bilinen-düz-metin
// XOR (bkz. Core/DecryptCore.cs). Hedef tek transaction içinde kukla metne ALTER'lanır,
// şifreli hali okunur ve ROLLBACK ile ÖZGÜN HALİNE geri alınır — hedef kalıcı değişmez.
public class DecryptCommands
{
    private sealed record ObjMeta(
        int ObjectId, string Type, string Schema, string Name,
        string? ParentSchema, string? ParentName, bool IsEncrypted)
    {
        public string Qualified => $"[{Schema}].[{Name}]";
        public string? ParentQualified =>
            ParentSchema is not null && ParentName is not null ? $"[{ParentSchema}].[{ParentName}]" : null;
        public string FileBase => $"{Schema}.{Name}";
    }

    [Command("ddl", Description = "Nesne tanımını gösterir: şifreli (WITH ENCRYPTION) ise DAC ile çözer, değilse düz tanımı verir")]
    public async Task Ddl(
        [Argument(Description = "Nesne adı (şema.ad veya ad). --like verilirse boş bırakılabilir.")] string? name = null,
        [Option("conn", Description = "Bağlantı string'i (override)")] string? conn = null,
        [Option("profile", Description = "sqlcli.json > Profiles içinden profil adı")] string? profile = null,
        [Option("like", Description = "Toplu: ada göre LIKE deseni (ör. %sevk%).")] string? like = null,
        [Option("out", Description = "Çıktı klasörü — her nesne şema.ad.sql olarak yazılır")] string? outDir = null,
        [Option("dac-port", Description = "DAC TCP portu (ör. 1434). Verilmezse SQL Browser çözer.")] int? dacPort = null,
        [Option("dac-server", Description = "DAC sunucu adını tamamen override et (admin: öneki otomatik eklenir)")] string? dacServer = null,
        [Option("overwrite", Description = "--out ile var olan dosyaların üzerine yaz")] bool overwrite = false,
        [Option("no-auto-dac", Description = "Şifreli nesnede DAC kapalıysa OTOMATİK açma (varsayılan açar). Yalnız dener.")] bool noAutoDac = false,
        [Option("dry-run", Description = "Hedefi ALTER etme; yalnız hedef uzunluğunu ve planı bildir")] bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(like))
            Fail("Nesne adı ya da --like deseni gerekli. Örn: sqlcli decrypt --profile erp \"dbo.sip_sevkisle\"");

        var resolved = ConnectionResolver.Resolve(conn, profile)
            ?? Fail<ConnectionResolver.ResolvedConnection>("Bağlantı bilgisi bulunamadı. SQLCLI_CONN env, --conn, --profile veya sqlcli.json gerekli.");

        if (resolved.Dialect.Provider != DbProvider.SqlServer)
            Fail("decrypt yalnız SQL Server içindir (WITH ENCRYPTION). Çözülen profil: " + resolved.Dialect.Name);

        var dacConnStr = BuildDacConnectionString(resolved.Effective, dacPort, dacServer);

        Info($"[grey]Profil:[/] {Markup.Escape(resolved.ProfileName ?? "default")}  " +
             $"[grey]DAC:[/] {Markup.Escape(MaskDac(dacConnStr))}");

        // DAC bağlantısı. Varsayılan: önce olduğu gibi dene; açılamazsa (muhtemelen kapalı)
        // OTOMATİK olarak uzak DAC'ı aç, yeniden dene ve iş bitince (finally) ESKİ HALİNE al.
        // --no-auto-dac ile bu otomatik açma kapatılır (yalnız dener).
        bool autoDac = !noAutoDac;
        bool dacGeciciAcildi = false;

        try
        {
        await using var dac = new SqlConnection(dacConnStr);
        try
        {
            await dac.OpenAsync();
        }
        catch (SqlException ilkHata)
        {
            // DAC açılamadı. Otomatik açma kapalıysa ya da set'i açmak derde deva olmazsa: net hata.
            bool acabildik = false;
            if (autoDac)
            {
                try { dacGeciciAcildi = await EnsureRemoteDacAsync(resolved.Effective); }
                catch (Exception acHata)
                {
                    throw new CommandExitedException(
                        DacHata(ilkHata) + $"\n  (otomatik açma da başarısız: {acHata.Message})", 2);
                }
                acabildik = dacGeciciAcildi;
            }

            if (!acabildik)
                // Ya auto kapalı, ya da uzak DAC zaten AÇIKtı (başka bir sorun var: firewall/yetki).
                throw new CommandExitedException(DacHata(ilkHata), 2);

            // Setti biz açtık — DAC dinleyicisi hazır olana dek yeniden deneyerek aç.
            try
            {
                await OpenDacWithRetryAsync(dac, enableDac: true);
            }
            catch (SqlException ex2)
            {
                throw new CommandExitedException(DacHata(ex2), 2);
            }
        }

        // Hedef listesi
        List<ObjMeta> targets;
        if (!string.IsNullOrWhiteSpace(like))
        {
            targets = await ListEncryptedAsync(dac, like!);
            if (targets.Count == 0)
                Fail($"'{like}' desenine uyan şifreli nesne yok.");
            Info($"[cyan]{targets.Count}[/] şifreli nesne bulundu.");
        }
        else
        {
            var m = await ResolveObjectAsync(dac, name!)
                ?? Fail<ObjMeta>($"Nesne bulunamadı: {name}. (Veritabanı: bağlantıdaki DB)");
            targets = new() { m };
        }

        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir!);

        int ok = 0, atlanan = 0, hata = 0;
        bool stdoutCok = string.IsNullOrWhiteSpace(outDir) && targets.Count > 1;

        foreach (var m in targets)
        {
            try
            {
                string sql;
                if (!m.IsEncrypted)
                {
                    // Şifreli değilse doğrudan tanımı ver (DAC'a gerek yok ama zaten açık).
                    sql = await ReadPlainDefinitionAsync(dac, m.ObjectId)
                          ?? throw new InvalidOperationException("Tanım okunamadı (modül tanımı boş).");
                    Info($"  [yellow]○[/] {m.Schema}.{m.Name} şifreli değil — tanım doğrudan alındı.");
                }
                else if (dryRun)
                {
                    var n = await TargetCharCountAsync(dac, m.ObjectId);
                    Info($"  [grey][[DRY-RUN]][/] {m.Schema}.{m.Name} ({m.Type}) — {n} karakter; ALTER çalıştırılmadı.");
                    atlanan++;
                    continue;
                }
                else
                {
                    sql = await DecryptEncryptedAsync(dac, m);
                    Info($"  [green]✓[/] {m.Schema}.{m.Name} çözüldü ({sql.Length} karakter).");
                }

                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    var path = Path.Combine(outDir!, m.FileBase + ".sql");
                    if (File.Exists(path) && !overwrite)
                    {
                        Info($"    [yellow]var, atlandı[/] (--overwrite ile yaz): {Markup.Escape(path)}");
                        atlanan++;
                        continue;
                    }
                    await File.WriteAllTextAsync(path, sql, new UTF8Encoding(false));
                    Info($"    [grey]yazıldı:[/] {Markup.Escape(path)}");
                }
                else
                {
                    if (stdoutCok) Console.Out.WriteLine($"-- ===== {m.Schema}.{m.Name} =====");
                    Console.Out.WriteLine(sql);
                    if (stdoutCok) Console.Out.WriteLine("GO");
                }
                ok++;
            }
            catch (Exception ex)
            {
                hata++;
                Info($"  [red]✗ {Markup.Escape(m.Schema)}.{Markup.Escape(m.Name)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        Info($"\n[grey]Bitti — çözüldü:{ok} atlandı:{atlanan} hata:{hata}[/]");
        if (hata > 0 && ok == 0) throw new CommandExitedException(1);
        }
        finally
        {
            if (dacGeciciAcildi) await TryDisableRemoteDacAsync(resolved.Effective);
        }
    }

    // ─── Uzak DAC aç/kapat (--enable-dac) ────────────────────────────────

    // Uzak DAC kapalıysa açar; döndürdüğü true = BİZ açtık, geri alınmalı.
    private static async Task<bool> EnsureRemoteDacAsync(string normalConnStr)
    {
        await using var c = new SqlConnection(ConnectionResolver.ExpandEnv(normalConnStr));
        await c.OpenAsync();
        var cur = Convert.ToInt32(await ScalarAsync(c,
            "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'remote admin connections'"));
        if (cur == 1)
        {
            Info("[grey]remote DAC zaten açık — dokunulmayacak.[/]");
            return false;
        }
        await ExecAsync(c, "EXEC sp_configure 'remote admin connections', 1; RECONFIGURE;");
        Info("[yellow]remote DAC geçici açıldı[/] [grey](iş bitince eski haline alınacak).[/]");
        return true;
    }

    private static async Task TryDisableRemoteDacAsync(string normalConnStr)
    {
        try
        {
            await using var c = new SqlConnection(ConnectionResolver.ExpandEnv(normalConnStr));
            await c.OpenAsync();
            await ExecAsync(c, "EXEC sp_configure 'remote admin connections', 0; RECONFIGURE;");
            Info("[grey]remote DAC eski haline (0) alındı.[/]");
        }
        catch (Exception ex)
        {
            Info($"[red]UYARI:[/] remote DAC geri alınamadı: {Markup.Escape(ex.Message)} — elle kapatın: " +
                 "EXEC sp_configure 'remote admin connections', 0; RECONFIGURE;");
        }
    }

    // RECONFIGURE sonrası DAC dinleyicisi hazır olana dek kısa yeniden deneme.
    private static async Task OpenDacWithRetryAsync(SqlConnection dac, bool enableDac)
    {
        var deneme = enableDac ? 4 : 1;
        for (int i = 1; ; i++)
        {
            try { await dac.OpenAsync(); return; }
            catch (SqlException) when (i < deneme)
            {
                if (dac.State != System.Data.ConnectionState.Closed) { try { await dac.CloseAsync(); } catch { } }
                await Task.Delay(1000);
            }
        }
    }

    private static async Task<object?> ScalarAsync(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ─── Çözme çekirdeği (DB I/O) ────────────────────────────────────────

    private static async Task<string> DecryptEncryptedAsync(SqlConnection dac, ObjMeta m)
    {
        await using var tran = (SqlTransaction)await dac.BeginTransactionAsync();
        try
        {
            var cipherTarget = await ReadImageValAsync(dac, m.ObjectId, tran);
            if (cipherTarget.Length == 0)
                throw new InvalidOperationException(
                    "sys.sysobjvalues içinde şifreli tanım yok (valclass=1 boş). Nesne gerçekten WITH ENCRYPTION mı?");
            if (cipherTarget.Length % 2 != 0)
                throw new InvalidOperationException($"Şifreli blob tek sayıda bayt ({cipherTarget.Length}) — UCS-2 beklenirdi.");

            int storedCharCount = cipherTarget.Length / 2;
            var dummy = DecryptCore.BuildDummyModule(m.Type, m.Qualified, m.ParentQualified, storedCharCount);
            // XOR, SQL'in SAKLADIĞI (CREATE) metni ister; gönderilen ise ALTER'dır.
            var plainDummy = DecryptCore.EncodeUcs2(dummy.StoredPlain);

            // Hedefi kukla metne ALTER et (transaction içinde) — sonunda ROLLBACK ile geri alınır.
            await using (var cmd = dac.CreateCommand())
            {
                cmd.Transaction = tran;
                cmd.CommandText = dummy.SubmitSql;
                await cmd.ExecuteNonQueryAsync();
            }

            var cipherDummy = await ReadImageValAsync(dac, m.ObjectId, tran);

            // HER ZAMAN geri al — hedef kalıcı olarak asla değişmesin.
            await tran.RollbackAsync();

            var plain = DecryptCore.XorRecover(cipherTarget, cipherDummy, plainDummy);
            return DecryptCore.DecodeUcs2(plain);
        }
        catch
        {
            try { await tran.RollbackAsync(); } catch { /* zaten kapanmış olabilir */ }
            throw;
        }
    }

    private static async Task<byte[]> ReadImageValAsync(SqlConnection dac, int objId, SqlTransaction? tran)
    {
        await using var cmd = dac.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText =
            "SELECT imageval FROM sys.sysobjvalues WHERE valclass = 1 AND objid = @o ORDER BY subobjid, valnum";
        cmd.Parameters.Add(new SqlParameter("@o", objId));

        using var ms = new MemoryStream();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            if (await rd.IsDBNullAsync(0)) continue;
            var bytes = (byte[])rd.GetValue(0);
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static async Task<int> TargetCharCountAsync(SqlConnection dac, int objId)
    {
        var b = await ReadImageValAsync(dac, objId, null);
        return b.Length / 2;
    }

    private static async Task<string?> ReadPlainDefinitionAsync(SqlConnection dac, int objId)
    {
        await using var cmd = dac.CreateCommand();
        cmd.CommandText = "SELECT definition FROM sys.sql_modules WHERE object_id = @o";
        cmd.Parameters.Add(new SqlParameter("@o", objId));
        var o = await cmd.ExecuteScalarAsync();
        return o is string s ? s : null;
    }

    // ─── Metadata ────────────────────────────────────────────────────────

    private static async Task<ObjMeta?> ResolveObjectAsync(SqlConnection dac, string name)
    {
        await using var cmd = dac.CreateCommand();
        cmd.CommandText = @"
            SELECT o.object_id, o.type,
                   s.name AS sch, o.name AS nm,
                   OBJECT_SCHEMA_NAME(o.parent_object_id) AS psch,
                   OBJECT_NAME(o.parent_object_id)        AS pnm,
                   CONVERT(int, ISNULL(OBJECTPROPERTYEX(o.object_id, 'IsEncrypted'), 0)) AS is_enc
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.object_id = OBJECT_ID(@n)";
        cmd.Parameters.Add(new SqlParameter("@n", name));

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return new ObjMeta(
            ObjectId: rd.GetInt32(0),
            Type: rd.GetString(1).Trim(),
            Schema: rd.GetString(2),
            Name: rd.GetString(3),
            ParentSchema: rd.IsDBNull(4) ? null : rd.GetString(4),
            ParentName: rd.IsDBNull(5) ? null : rd.GetString(5),
            IsEncrypted: rd.GetInt32(6) == 1);
    }

    private static async Task<List<ObjMeta>> ListEncryptedAsync(SqlConnection dac, string like)
    {
        await using var cmd = dac.CreateCommand();
        cmd.CommandText = @"
            SELECT o.object_id, o.type,
                   s.name AS sch, o.name AS nm,
                   OBJECT_SCHEMA_NAME(o.parent_object_id) AS psch,
                   OBJECT_NAME(o.parent_object_id)        AS pnm
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P','PC','RF','V','FN','IF','TF','TR')
              AND CONVERT(int, ISNULL(OBJECTPROPERTYEX(o.object_id, 'IsEncrypted'), 0)) = 1
              AND o.name LIKE @like
            ORDER BY s.name, o.name";
        cmd.Parameters.Add(new SqlParameter("@like", like));

        var list = new List<ObjMeta>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new ObjMeta(
                ObjectId: rd.GetInt32(0),
                Type: rd.GetString(1).Trim(),
                Schema: rd.GetString(2),
                Name: rd.GetString(3),
                ParentSchema: rd.IsDBNull(4) ? null : rd.GetString(4),
                ParentName: rd.IsDBNull(5) ? null : rd.GetString(5),
                IsEncrypted: true));
        return list;
    }

    // ─── DAC bağlantı string'i ───────────────────────────────────────────

    private static string BuildDacConnectionString(string baseConnStr, int? dacPort, string? dacServer)
    {
        var b = new SqlConnectionStringBuilder(baseConnStr);
        b.DataSource = string.IsNullOrWhiteSpace(dacServer)
            ? DecryptCore.BuildDacDataSource(b.DataSource, dacPort)
            : (dacServer!.StartsWith("admin:", StringComparison.OrdinalIgnoreCase) ? dacServer! : "admin:" + dacServer);

        // DAC tek, havuzlanmamış, MARS'sız bir oturum olmalı.
        b.Pooling = false;
        b.MultipleActiveResultSets = false;
        if (b.ConnectTimeout == 0 || b.ConnectTimeout > 30) b.ConnectTimeout = 15;
        return b.ConnectionString;
    }

    private static string MaskDac(string connStr) =>
        System.Text.RegularExpressions.Regex.Replace(
            connStr, @"Password=[^;]+", "Password=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string DacHata(SqlException ex) =>
        "DAC (Dedicated Admin Connection) açılamadı: " + ex.Message.Split('\n')[0] + "\n" +
        "  • Uzak DAC kapalı olabilir. Sunucuda: EXEC sp_configure 'remote admin connections', 1; RECONFIGURE;\n" +
        "  • DAC yalnız TEK oturuma izin verir — başka bir DAC açıksa kapatın.\n" +
        "  • Port SQL Browser (UDP 1434) ile çözülür; kapalıysa --dac-port ile verin (ör. --dac-port 1434).\n" +
        "  • Yönetici (sysadmin) yetkisi gerekir.";

    // ─── yardımcılar ─────────────────────────────────────────────────────

    private static void Info(string markup) => AnsiConsole.MarkupLine(markup);

    // Hatalarda hiçbir zaman null dönmeyen "yükseltir"ler (çağrı yerinde ?? ile kullanılır).
    private static T Fail<T>(string msg) => throw new CommandExitedException(msg, 1);
    private static void Fail(string msg) => throw new CommandExitedException(msg, 1);
}
