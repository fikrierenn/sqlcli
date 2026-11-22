using System.Text;
using Cocona;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using SqlCli.Core;

namespace SqlCli.Commands;

// ddl — nesne tanımını verir; WITH ENCRYPTION ile şifreli SP / view / function / trigger'ı çözer.
//
// Sadece SQL Server. Hedefler önce NORMAL bağlantıda çözülür; şifresiz nesnenin tanımı
// sys.sql_modules.definition'dan doğrudan okunur (DAC, otomatik DAC, sp_configure YOK).
// Şifreli tanım sys.sysobjvalues.imageval (valclass=1) içindedir ve YALNIZ DAC (Dedicated
// Admin Connection) üzerinden okunur; DAC bu yüzden yalnız en az bir hedef şifreliyse açılır.
// Algoritma: bilinen-düz-metin XOR (bkz. Core/DecryptCore.cs). Hedef tek transaction içinde
// kukla metne ALTER'lanır, şifreli hali okunur ve ROLLBACK ile ÖZGÜN HALİNE geri alınır —
// hedef kalıcı değişmez.
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
        [Option("like", Description = "Toplu: ada göre LIKE deseni (ör. %sevk%). Varsayılan yalnız şifreli nesneler.")] string? like = null,
        [Option("include-plain", Description = "--like ile şifresiz nesneleri de al (onlar için DAC açılmaz)")] bool includePlain = false,
        [Option("out", Description = "Çıktı klasörü — her nesne şema.ad.sql olarak yazılır")] string? outDir = null,
        [Option("dac-port", Description = "DAC TCP portu (ör. 1434). Verilmezse SQL Browser çözer.")] int? dacPort = null,
        [Option("dac-server", Description = "DAC sunucu adını tamamen override et (admin: öneki otomatik eklenir)")] string? dacServer = null,
        [Option("overwrite", Description = "--out ile var olan dosyaların üzerine yaz")] bool overwrite = false,
        [Option("no-auto-dac", Description = "Şifreli nesnede DAC kapalıysa OTOMATİK açma (varsayılan açar). Yalnız dener.")] bool noAutoDac = false,
        [Option("dry-run", Description = "Hedefi ALTER etme; yalnız hedef uzunluğunu ve planı bildir")] bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(like))
            Fail("Nesne adı ya da --like deseni gerekli. Örn: sqlcli ddl --profile erp \"dbo.sip_sevkisle\"");

        var resolved = ConnectionResolver.Resolve(conn, profile)
            ?? Fail<ConnectionResolver.ResolvedConnection>("Bağlantı bilgisi bulunamadı. SQLCLI_CONN env, --conn, --profile veya sqlcli.json gerekli.");

        if (resolved.Dialect.Provider != DbProvider.SqlServer)
            Fail("ddl yalnız SQL Server içindir (WITH ENCRYPTION). Çözülen profil: " + resolved.Dialect.Name);

        Info($"[grey]Profil:[/] {Markup.Escape(resolved.ProfileName ?? "default")}");

        // 1) Hedefler NORMAL bağlantıda çözülür. Şifresiz nesnenin tanımı da buradan okunur;
        //    DAC'a, otomatik DAC'a ya da sp_configure'a hiç gerek yoktur (sysadmin olmayan
        //    kullanıcı da düz tanımı alabilsin diye).
        await using var normal = new SqlConnection(resolved.Effective);
        await normal.OpenAsync();

        List<ObjMeta> targets;
        if (!string.IsNullOrWhiteSpace(like))
        {
            targets = await ListByPatternAsync(normal, like!, includePlain);
            if (targets.Count == 0)
                Fail(includePlain
                    ? $"'{like}' desenine uyan modül (SP/view/function/trigger) yok."
                    : $"'{like}' desenine uyan şifreli nesne yok. (Şifresizler için --include-plain)");
            int encCount = targets.Count(t => t.IsEncrypted);
            Info($"[cyan]{targets.Count}[/] nesne bulundu ([cyan]{encCount}[/] şifreli).");
        }
        else
        {
            var m = await ResolveObjectAsync(normal, name!)
                ?? Fail<ObjMeta>($"Nesne bulunamadı: {name}. (Veritabanı: bağlantıdaki DB)");
            targets = new() { m };
        }

        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir!);

        // 2) DAC yalnız en az bir hedef şifreliyse açılır (karışık listede de yalnız bir kez).
        bool needsDac = DecryptCore.NeedsDac(targets.Select(t => t.IsEncrypted));
        bool dacTempOpened = false;   // true = uzak DAC ayarını BİZ 0→1 yaptık; finally'de 0'a dönmeli
        SqlConnection? dac = null;
        string? dacFailure = null;

        try
        {
            if (needsDac)
            {
                var dacConnStr = BuildDacConnectionString(resolved.Effective, dacPort, dacServer);
                Info($"[grey]DAC:[/] {Markup.Escape(MaskDac(dacConnStr))}");
                dac = new SqlConnection(dacConnStr);
                try
                {
                    await OpenDacAsync(dac, resolved.Effective, autoDac: !noAutoDac, opened => dacTempOpened = opened);
                }
                catch (DacOpenException ex)
                {
                    // Şifresiz hedefler yine verilir; şifreliler hata sayılır ve sonunda exit 2.
                    dacFailure = ex.Message;
                    Info($"[red]{Markup.Escape(ex.Message)}[/]");
                }
            }

            int ok = 0, skipped = 0, error = 0;
            bool multiToStdout = string.IsNullOrWhiteSpace(outDir) && targets.Count > 1;

            foreach (var m in targets)
            {
                try
                {
                    string sql;
                    if (!m.IsEncrypted)
                    {
                        // Şifresiz: tanım normal bağlantıdan, DAC'sız.
                        sql = await ReadPlainDefinitionAsync(normal, m.ObjectId)
                              ?? throw new InvalidOperationException(
                                  "Tanım okunamadı (sys.sql_modules.definition boş) — VIEW DEFINITION yetkisi eksik olabilir.");
                        Info($"  [yellow]○[/] {m.Schema}.{m.Name} şifreli değil — tanım doğrudan alındı (DAC'sız).");
                    }
                    else if (dac is null || dacFailure is not null)
                    {
                        throw new InvalidOperationException("şifreli; DAC açılamadığı için çözülemedi.");
                    }
                    else if (dryRun)
                    {
                        var n = await TargetCharCountAsync(dac, m.ObjectId);
                        Info($"  [grey][[DRY-RUN]][/] {m.Schema}.{m.Name} ({m.Type}) — {n} karakter; ALTER çalıştırılmadı.");
                        skipped++;
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
                            skipped++;
                            continue;
                        }
                        await File.WriteAllTextAsync(path, sql, new UTF8Encoding(false));
                        Info($"    [grey]yazıldı:[/] {Markup.Escape(path)}");
                    }
                    else
                    {
                        if (multiToStdout) Console.Out.WriteLine($"-- ===== {m.Schema}.{m.Name} =====");
                        Console.Out.WriteLine(sql);
                        if (multiToStdout) Console.Out.WriteLine("GO");
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    error++;
                    Info($"  [red]✗ {Markup.Escape(m.Schema)}.{Markup.Escape(m.Name)}: {Markup.Escape(ex.Message)}[/]");
                }
            }

            Info($"\n[grey]Bitti — alındı/çözüldü:{ok} atlandı:{skipped} hata:{error}[/]");
            if (dacFailure is not null) throw new CommandExitedException(2);
            if (error > 0 && ok == 0) throw new CommandExitedException(1);
        }
        finally
        {
            if (dac is not null) await dac.DisposeAsync();
            // Ayar HER ZAMAN özgün değerine döner: yalnız biz 0→1 yaptıysak 0'a alınır.
            if (dacTempOpened) await TryDisableRemoteDacAsync(resolved.Effective);
        }
    }

    // DAC açılamadı — mesaj kullanıcıya gösterilmeye hazırdır.
    private sealed class DacOpenException(string message) : Exception(message);

    // DAC'ı açar. Önce olduğu gibi dener; açılamazsa ve autoDac açıksa uzak DAC ayarı 0 ise
    // geçici 1 yapar (setTempOpened(true) ile çağırana bildirir, geri alma çağıranın finally'sindedir)
    // ve kısa yeniden denemeyle bağlanır. Ayar zaten 1 ise DOKUNMAZ ve yeniden DENEMEZ: en olası neden
    // başka bir DAC oturumudur (DAC tek oturum) — ayarı açık bırakabilecek bir döngüye girilmez.
    private static async Task OpenDacAsync(SqlConnection dac, string normalConnStr, bool autoDac, Action<bool> setTempOpened)
    {
        SqlException firstError;
        try
        {
            await dac.OpenAsync();
            return;
        }
        catch (SqlException ex) { firstError = ex; }

        var firstLine = firstError.Message.Split('\n')[0];

        if (!autoDac)
        {
            var state = await ReadRemoteDacStateAsync(normalConnStr);
            throw new DacOpenException(DecryptCore.DescribeDacFailure(firstLine, firstError.Number, state));
        }

        bool opened;
        try { opened = await EnsureRemoteDacAsync(normalConnStr); }
        catch (Exception openError)
        {
            throw new DacOpenException(
                DecryptCore.DescribeDacFailure(firstLine, firstError.Number, DecryptCore.RemoteDacState.Unknown) +
                $"\n  (otomatik açma da başarısız: {openError.Message})");
        }
        setTempOpened(opened);

        if (!opened)
            // Uzak DAC zaten açıktı ama bağlanamadık → büyük olasılıkla başka bir DAC oturumu.
            throw new DacOpenException(
                DecryptCore.DescribeDacFailure(firstLine, firstError.Number, DecryptCore.RemoteDacState.AlreadyOn));

        // Ayarı biz açtık — DAC dinleyicisi hazır olana dek kısa yeniden denemeyle bağlan.
        try
        {
            await OpenDacWithRetryAsync(dac, enableDac: true);
        }
        catch (SqlException ex2)
        {
            throw new DacOpenException(DecryptCore.DescribeDacFailure(
                ex2.Message.Split('\n')[0], ex2.Number, DecryptCore.RemoteDacState.OpenedByUs));
        }
    }

    // Uzak DAC ayarını yalnız OKUR (yazma yok); hata mesajını netleştirmek için.
    private static async Task<DecryptCore.RemoteDacState> ReadRemoteDacStateAsync(string normalConnStr)
    {
        try
        {
            await using var c = new SqlConnection(ConnectionResolver.ExpandEnv(normalConnStr));
            await c.OpenAsync();
            var cur = Convert.ToInt32(await ScalarAsync(c,
                "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'remote admin connections'"));
            return cur == 1 ? DecryptCore.RemoteDacState.AlreadyOn : DecryptCore.RemoteDacState.Off;
        }
        catch
        {
            return DecryptCore.RemoteDacState.Unknown;
        }
    }

    // ─── Uzak DAC aç/kapat (otomatik DAC) ────────────────────────────────

    // Uzak DAC kapalıysa açar; döndürdüğü true = BİZ açtık, geri alınmalı.
    private static async Task<bool> EnsureRemoteDacAsync(string normalConnStr)
    {
        await using var c = new SqlConnection(ConnectionResolver.ExpandEnv(normalConnStr));
        await c.OpenAsync();
        var cur = Convert.ToInt32(await ScalarAsync(c,
            "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'remote admin connections'"));
        if (cur == 1)
        {
            Info("[grey]remote DAC zaten açık (başka bir oturum açmış olabilir) — dokunulmayacak.[/]");
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
        var attempt = enableDac ? 4 : 1;
        for (int i = 1; ; i++)
        {
            try { await dac.OpenAsync(); return; }
            catch (SqlException) when (i < attempt)
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

    // Şifresiz modülün tanımı — normal bağlantı yeter (VIEW DEFINITION yetkisi gerekir).
    private static async Task<string?> ReadPlainDefinitionAsync(SqlConnection c, int objId)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT definition FROM sys.sql_modules WHERE object_id = @o";
        cmd.Parameters.Add(new SqlParameter("@o", objId));
        var o = await cmd.ExecuteScalarAsync();
        return o is string s ? s : null;
    }

    // ─── Metadata (normal bağlantıda) ────────────────────────────────────

    private static async Task<ObjMeta?> ResolveObjectAsync(SqlConnection c, string name)
    {
        await using var cmd = c.CreateCommand();
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

    // --like: varsayılan yalnız şifreli modüller; includePlain ile şifresizler de (karışık liste).
    private static async Task<List<ObjMeta>> ListByPatternAsync(SqlConnection c, string like, bool includePlain)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT o.object_id, o.type,
                   s.name AS sch, o.name AS nm,
                   OBJECT_SCHEMA_NAME(o.parent_object_id) AS psch,
                   OBJECT_NAME(o.parent_object_id)        AS pnm,
                   CONVERT(int, ISNULL(OBJECTPROPERTYEX(o.object_id, 'IsEncrypted'), 0)) AS is_enc
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P','PC','RF','V','FN','IF','TF','TR')
              AND (@all = 1 OR CONVERT(int, ISNULL(OBJECTPROPERTYEX(o.object_id, 'IsEncrypted'), 0)) = 1)
              AND o.name LIKE @like
            ORDER BY s.name, o.name";
        cmd.Parameters.Add(new SqlParameter("@like", like));
        cmd.Parameters.Add(new SqlParameter("@all", includePlain ? 1 : 0));

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
                IsEncrypted: rd.GetInt32(6) == 1));
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

    // ─── yardımcılar ─────────────────────────────────────────────────────

    private static void Info(string markup) => AnsiConsole.MarkupLine(markup);

    // Hatalarda hiçbir zaman null dönmeyen "yükseltir"ler (çağrı yerinde ?? ile kullanılır).
    private static T Fail<T>(string msg) => throw new CommandExitedException(msg, 1);
    private static void Fail(string msg) => throw new CommandExitedException(msg, 1);
}
