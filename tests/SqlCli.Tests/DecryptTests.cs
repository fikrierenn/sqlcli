using System.Text;
using SqlCli.Core;

namespace SqlCli.Tests;

// decrypt komutunun DB'siz saf çekirdeği (Core/DecryptCore.cs).
//
// SQL Server'ın anahtar akışı K'sini, nesne için SABİT bir keystream ile taklit ediyoruz:
// hedef ve kukla AYNI K ile XOR'lanır (gerçekte aynı objid oldukları için). Çözme, canlı DB
// olmadan uçtan uca burada doğrulanır.
public class DecryptTests
{
    private static byte[] Xor(byte[] a, byte[] k)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ k[i]);
        return r;
    }

    private static byte[] SahteKeystream(int uzunluk, int seed)
    {
        var rng = new Random(seed);
        var k = new byte[uzunluk];
        rng.NextBytes(k);
        return k;
    }

    [Fact]
    public void BilinenDuzMetin_XOR_ozgunTanimiGeriGetirir()
    {
        // "Gerçek" (şifreli saklanan) proc tanımı — SQL CREATE biçiminde saklar.
        var ozgun =
            "CREATE PROCEDURE [dbo].[sip_sevkisle] @eID int WITH ENCRYPTION AS\n" +
            " BEGIN\n\tSELECT @eID\n\tRETURN 0\n END  ";
        var duzHedef = Encoding.Unicode.GetBytes(ozgun);
        int storedCharCount = ozgun.Length;

        // Nesneye bağlı (içerikten bağımsız) sabit anahtar akışı.
        var K = SahteKeystream(duzHedef.Length, seed: 1234);
        var sifreliHedef = Xor(duzHedef, K);

        // Komutun yapacağı: aynı SAKLANAN uzunlukta kukla üret, SAKLANAN (CREATE) metnini AYNI K ile şifrele.
        var kukla = DecryptCore.BuildDummyModule("P", "[dbo].[sip_sevkisle]", null, storedCharCount);
        Assert.StartsWith("ALTER", kukla.SubmitSql);          // gönderilen ALTER
        Assert.StartsWith("CREATE", kukla.StoredPlain);       // saklanan CREATE
        Assert.Equal(storedCharCount, kukla.StoredPlain.Length);
        Assert.Equal(storedCharCount - 1, kukla.SubmitSql.Length);

        var duzKukla = DecryptCore.EncodeUcs2(kukla.StoredPlain);
        Assert.Equal(sifreliHedef.Length, duzKukla.Length);   // UCS-2 hizası
        var sifreliKukla = Xor(duzKukla, K);

        var cozulen = DecryptCore.DecodeUcs2(
            DecryptCore.XorRecover(sifreliHedef, sifreliKukla, duzKukla));

        Assert.Equal(ozgun, cozulen);
    }

    [Theory]
    [InlineData("P",  "[dbo].[p]", null)]
    [InlineData("V",  "[dbo].[v]", null)]
    [InlineData("FN", "[dbo].[fn]", null)]
    [InlineData("IF", "[dbo].[tf_inline]", null)]
    [InlineData("TF", "[dbo].[tf_multi]", null)]
    [InlineData("TR", "[dbo].[trg]", "[dbo].[tablo]")]
    public void Kukla_saklananTamUzunluk_gonderilenBirEksik(string tur, string q, string? parent)
    {
        for (int n = 400; n <= 420; n++)   // iskeletten büyük birkaç uzunluk
        {
            var d = DecryptCore.BuildDummyModule(tur, q, parent, n);
            Assert.Equal(n, d.StoredPlain.Length);        // saklanan (CREATE) tam uzunluk
            Assert.Equal(n - 1, d.SubmitSql.Length);      // gönderilen (ALTER) bir eksik
            Assert.StartsWith("CREATE", d.StoredPlain);
            Assert.StartsWith("ALTER", d.SubmitSql);
        }
    }

    [Fact]
    public void Kukla_cokKisaHedefte_hataVerir()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecryptCore.BuildDummyModule("P", "[dbo].[x]", null, 3));
    }

    [Fact]
    public void Kukla_triggerParentYoksa_hataVerir()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecryptCore.BuildDummyModule("TR", "[dbo].[trg]", null, 500));
    }

    [Fact]
    public void Kukla_bilinmeyenTur_hataVerir()
    {
        Assert.Throws<NotSupportedException>(
            () => DecryptCore.BuildDummyModule("XX", "[dbo].[x]", null, 500));
    }

    [Fact]
    public void XorRecover_uzunlukUyusmazsa_hataVerir()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecryptCore.XorRecover(new byte[10], new byte[10], new byte[8]));
    }

    [Theory]
    [InlineData("RDSTEST,1433", null, "admin:RDSTEST")]             // normal port düşer
    [InlineData("RDSTEST,1433", 1434, "admin:RDSTEST,1434")]        // DAC portu verilir
    [InlineData("RDSTEST", null, "admin:RDSTEST")]
    [InlineData("tcp:HOST\\SQLEXPRESS", null, "admin:HOST\\SQLEXPRESS")]  // instance korunur
    [InlineData("admin:HOST", null, "admin:HOST")]                  // iki kez önek eklenmez
    public void DacDataSource_dogruKurulur(string girdi, int? port, string beklenen)
        => Assert.Equal(beklenen, DecryptCore.BuildDacDataSource(girdi, port));
}
