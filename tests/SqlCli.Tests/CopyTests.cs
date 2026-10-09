using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlCli.Tests;

/// <summary>
/// `sqlcli copy` UCTAN UCA: gercek sqlcli.dll sureci, gercek SQL Server.
///
/// Sunucu: SQLCLI_TEST_SQL (yoksa yerel `localhost`, Windows kimligi). Iki deneme veritabani
/// kurar: sqlcli_test_kaynak / sqlcli_test_hedef. Sunucuya ulasilamazsa testler KIRMIZI olur,
/// sessizce atlanmaz — "kosamadim" ile "gecti" ayni sey degildir.
/// </summary>
public sealed class SqlFixture : IDisposable
{
    public string Sunucu { get; } = Environment.GetEnvironmentVariable("SQLCLI_TEST_SQL")
        ?? "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    public string Kaynak => Sunucu + ";Database=sqlcli_test_kaynak";
    public string Hedef => Sunucu + ";Database=sqlcli_test_hedef";

    public SqlFixture()
    {
        Exec(Sunucu + ";Database=master", """
            IF DB_ID('sqlcli_test_kaynak') IS NULL CREATE DATABASE sqlcli_test_kaynak;
            IF DB_ID('sqlcli_test_hedef') IS NULL CREATE DATABASE sqlcli_test_hedef;
            """);
        Exec(Kaynak, """
            DROP TABLE IF EXISTS dbo.Kaynak;
            CREATE TABLE dbo.Kaynak (Id int NOT NULL, Deger int NOT NULL);
            INSERT dbo.Kaynak VALUES (1, 10), (2, 20), (3, -5);   -- 3: hedef CK'sini ciger
            """);
    }

    /// <summary>Her test temiz hedef: CK (Deger &gt;= 0) + INSERT tetikleyicisi + gunluk tablosu.</summary>
    public void HedefSifirla(int onYukluSatir = 0)
    {
        Exec(Hedef, """
            DROP TABLE IF EXISTS dbo.Hedef;
            DROP TABLE IF EXISTS dbo.TetikGunluk;
            CREATE TABLE dbo.TetikGunluk (Adet int NOT NULL);
            CREATE TABLE dbo.Hedef (Id int NOT NULL, Deger int NOT NULL CONSTRAINT CK_Hedef_Deger CHECK (Deger >= 0));
            """);
        Exec(Hedef, "CREATE TRIGGER dbo.trg_Hedef_Ekle ON dbo.Hedef AFTER INSERT AS INSERT dbo.TetikGunluk SELECT COUNT(*) FROM inserted;");
        for (int i = 0; i < onYukluSatir; i++)
            Exec(Hedef, $"ALTER TABLE dbo.Hedef NOCHECK CONSTRAINT ALL; DISABLE TRIGGER dbo.trg_Hedef_Ekle ON dbo.Hedef; INSERT dbo.Hedef VALUES ({100 + i}, 1); ENABLE TRIGGER dbo.trg_Hedef_Ekle ON dbo.Hedef; ALTER TABLE dbo.Hedef CHECK CONSTRAINT ALL;");
    }

    public static void Exec(string cs, string sql)
    {
        using var c = new SqlConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long Sayi(string cs, string sql)
    {
        using var c = new SqlConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose() { }
}

[CollectionDefinition("sql", DisableParallelization = true)]
public class SqlKoleksiyon : ICollectionFixture<SqlFixture> { }

[Collection("sql")]
public class CopyTests(SqlFixture db)
{
    private record Sonuc(int Kod, string Stdout, string Stderr)
    {
        public JsonElement Json => JsonDocument.Parse(Stdout).RootElement;   // stdout'un TAMAMI JSON olmali
    }

    private static Sonuc Calistir(IEnumerable<string> args, Dictionary<string, string?>? env = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "sqlcli.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                                                   WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SQLCLI_SKIP_VERSION_CHECK"] = "1";   // test derlemesi kurulu araçtan bagimsiz
        psi.Environment["SQLCLI_READONLY"] = null;
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(); throw new TimeoutException("sqlcli 120 sn'de bitmedi"); }
        return new Sonuc(p.ExitCode, o.Result, e.Result);
    }

    private string[] Copy(string query, params string[] ek) =>
        ["copy", "--from", db.Kaynak, "--to", db.Hedef, "--query", query, "--table", "dbo.Hedef", "--format", "json", .. ek];

    // ---- 1. --read-only (varsayilan acik) ----------------------------------------------

    [Fact]
    public void ReadOnly_varsayilan_acik_UPDATE_kaynaga_gitmeden_red()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("UPDATE dbo.Kaynak SET Deger = -1 OUTPUT inserted.Id, inserted.Deger"));
        Assert.Equal(2, r.Kod);
        Assert.Equal("HATA", r.Json.GetProperty("durum").GetString());
        Assert.Contains("SALT-OKUMA", r.Json.GetProperty("hata").GetString());
        Assert.Equal(0, SqlFixture.Sayi(db.Kaynak, "SELECT COUNT(*) FROM dbo.Kaynak WHERE Deger = -1"));  // kaynak degismedi
    }

    [Fact]
    public void ReadOnly_yorumdaki_UPDATE_kabul()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("-- UPDATE dbo.Kaynak SET Deger = -1\nSELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0"));
        Assert.Equal(0, r.Kod);
        Assert.Equal(2, r.Json.GetProperty("satir").GetInt64());
    }

    [Fact]
    public void ReadOnly_ortam_degiskeni_bayrakla_kapatilamaz()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("DELETE FROM dbo.Kaynak OUTPUT deleted.Id, deleted.Deger", "--read-only=false"),
                         new() { ["SQLCLI_READONLY"] = "1" });
        Assert.Equal(2, r.Kod);
        Assert.Equal(3, SqlFixture.Sayi(db.Kaynak, "SELECT COUNT(*) FROM dbo.Kaynak"));
    }

    [Fact]
    public void ReadOnly_acikca_kapatilinca_uyarir_ve_calisir()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0", "--read-only=false"));
        Assert.Equal(0, r.Kod);
        Assert.Contains("--read-only=false", r.Stderr);
        Assert.False(r.Json.GetProperty("read_only").GetBoolean());
    }

    // ---- 2. --check-constraints / --fire-triggers ------------------------------------------

    [Fact]
    public void Varsayilan_kisit_denetlenmez_tetikleyici_calismaz()   // belgelenen SqlBulkCopy varsayilani
    {
        db.HedefSifirla();
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak"));
        Assert.Equal(0, r.Kod);
        Assert.Equal(1, SqlFixture.Sayi(db.Hedef, "SELECT COUNT(*) FROM dbo.Hedef WHERE Deger < 0"));    // CK cignendi
        Assert.Equal(0, SqlFixture.Sayi(db.Hedef, "SELECT COUNT(*) FROM dbo.TetikGunluk"));
    }

    [Fact]
    public void CheckConstraints_CK_ihlali_547_ile_hata()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak", "--check-constraints"));
        Assert.Equal(2, r.Kod);
        Assert.Equal(547, r.Json.GetProperty("hata_no").GetInt32());
    }

    [Fact]
    public void FireTriggers_tetikleyici_calisir()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0", "--fire-triggers", "--check-constraints"));
        Assert.Equal(0, r.Kod);
        Assert.Equal(2, SqlFixture.Sayi(db.Hedef, "SELECT ISNULL(SUM(Adet),0) FROM dbo.TetikGunluk"));
    }

    // ---- 3 + 4. makine okunur sonuc + sunucu/db kimligi -------------------------------------

    [Fact]
    public void Json_satir_aktarilan_sayidir_tablo_toplami_degil()
    {
        db.HedefSifirla(onYukluSatir: 5);
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0"));
        Assert.Equal(0, r.Kod);
        var j = r.Json;
        Assert.Equal("TAMAM", j.GetProperty("durum").GetString());
        Assert.Equal(2, j.GetProperty("satir").GetInt64());
        Assert.Equal(7, j.GetProperty("hedef_toplam").GetInt64());
        Assert.True(j.GetProperty("sure_ms").GetInt64() >= 0);
        Assert.True(j.GetProperty("read_only").GetBoolean());
    }

    [Fact]
    public void Json_kaynak_ve_hedef_sunucu_db()
    {
        db.HedefSifirla();
        var sunucu = Ad(db.Kaynak);
        var j = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0")).Json;
        Assert.Equal(sunucu, j.GetProperty("kaynak_sunucu").GetString());
        Assert.Equal("sqlcli_test_kaynak", j.GetProperty("kaynak_db").GetString());
        Assert.Equal(sunucu, j.GetProperty("hedef_sunucu").GetString());
        Assert.Equal("sqlcli_test_hedef", j.GetProperty("hedef_db").GetString());

        static string Ad(string cs)
        {
            using var c = new SqlConnection(cs); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT @@SERVERNAME";
            return (string)cmd.ExecuteScalar()!;
        }
    }

    [Fact]
    public void Param_kaynak_sorguya_baglanir()
    {
        db.HedefSifirla();
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= @Esik", "--param", "Esik=15"));
        Assert.Equal(0, r.Kod);
        Assert.Equal(1, r.Json.GetProperty("satir").GetInt64());
    }

    [Fact]
    public void Tablo_kipi_SONUC_satiri()
    {
        db.HedefSifirla();
        var args = Copy("SELECT Id, Deger FROM dbo.Kaynak WHERE Deger >= 0").ToList();
        args[^1] = "table";
        var r = Calistir(args);
        Assert.Equal(0, r.Kod);
        Assert.Matches(@"SONUC satir=2 sure_ms=\d+", r.Stdout);
    }

    // ---- 5. hata sozlesmesi + yarim yukleme ----------------------------------------------

    [Fact]
    public void Truncate_atomic_degil_hata_yarim_yukleme_birakir()
    {
        db.HedefSifirla(onYukluSatir: 5);
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak ORDER BY Id", "--check-constraints", "--truncate", "--batch", "1"));
        Assert.Equal(2, r.Kod);
        Assert.Contains("YARIM", r.Json.GetProperty("hata").GetString());
        Assert.Equal(2, SqlFixture.Sayi(db.Hedef, "SELECT COUNT(*) FROM dbo.Hedef"));   // 5 silindi, 2 batch kaldi
    }

    [Fact]
    public void Atomic_hata_hedefi_degistirmez()
    {
        db.HedefSifirla(onYukluSatir: 5);
        var r = Calistir(Copy("SELECT Id, Deger FROM dbo.Kaynak ORDER BY Id", "--check-constraints", "--truncate", "--batch", "1", "--atomic"));
        Assert.Equal(2, r.Kod);
        Assert.Equal(5, SqlFixture.Sayi(db.Hedef, "SELECT COUNT(*) FROM dbo.Hedef"));   // truncate dahil geri alindi
    }

    [Fact]
    public void Baglanti_hatasi_sifirdan_farkli()
    {
        db.HedefSifirla();
        var r = Calistir(["copy", "--from", db.Sunucu + ";Database=yok_boyle_db", "--to", db.Hedef, "--query", "SELECT 1 AS Id, 1 AS Deger",
                          "--table", "dbo.Hedef", "--format", "json"]);
        Assert.Equal(2, r.Kod);
        Assert.Equal("HATA", r.Json.GetProperty("durum").GetString());
    }
}
