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

    private static byte[] FakeKeystream(int byteLength, int seed)
    {
        var rng = new Random(seed);
        var k = new byte[byteLength];
        rng.NextBytes(k);
        return k;
    }

    [Fact]
    public void KnownPlainText_XOR_recovers_original_definition()
    {
        // "Gerçek" (şifreli saklanan) proc tanımı — SQL CREATE biçiminde saklar.
        var original =
            "CREATE PROCEDURE [dbo].[sip_sevkisle] @eID int WITH ENCRYPTION AS\n" +
            " BEGIN\n\tSELECT @eID\n\tRETURN 0\n END  ";
        var plainTarget = Encoding.Unicode.GetBytes(original);
        int storedCharCount = original.Length;

        // Nesneye bağlı (içerikten bağımsız) sabit anahtar akışı.
        var K = FakeKeystream(plainTarget.Length, seed: 1234);
        var encryptedTarget = Xor(plainTarget, K);

        // Komutun yapacağı: aynı SAKLANAN uzunlukta kukla üret, SAKLANAN (CREATE) metnini AYNI K ile şifrele.
        var dummy = DecryptCore.BuildDummyModule("P", "[dbo].[sip_sevkisle]", null, storedCharCount);
        Assert.StartsWith("ALTER", dummy.SubmitSql);          // gönderilen ALTER
        Assert.StartsWith("CREATE", dummy.StoredPlain);       // saklanan CREATE
        Assert.Equal(storedCharCount, dummy.StoredPlain.Length);
        Assert.Equal(storedCharCount - 1, dummy.SubmitSql.Length);

        var plainDummyText = DecryptCore.EncodeUcs2(dummy.StoredPlain);
        Assert.Equal(encryptedTarget.Length, plainDummyText.Length);   // UCS-2 hizası
        var encryptedDummy = Xor(plainDummyText, K);

        var decrypted = DecryptCore.DecodeUcs2(
            DecryptCore.XorRecover(encryptedTarget, encryptedDummy, plainDummyText));

        Assert.Equal(original, decrypted);
    }

    [Theory]
    [InlineData("P",  "[dbo].[p]", null)]
    [InlineData("V",  "[dbo].[v]", null)]
    [InlineData("FN", "[dbo].[fn]", null)]
    [InlineData("IF", "[dbo].[tf_inline]", null)]
    [InlineData("TF", "[dbo].[tf_multi]", null)]
    [InlineData("TR", "[dbo].[trg]", "[dbo].[tablo]")]
    public void Dummy_storedFullLength_sentOneLess(string kind, string q, string? parent)
    {
        for (int n = 400; n <= 420; n++)   // iskeletten büyük birkaç uzunluk
        {
            var d = DecryptCore.BuildDummyModule(kind, q, parent, n);
            Assert.Equal(n, d.StoredPlain.Length);        // saklanan (CREATE) tam uzunluk
            Assert.Equal(n - 1, d.SubmitSql.Length);      // gönderilen (ALTER) bir eksik
            Assert.StartsWith("CREATE", d.StoredPlain);
            Assert.StartsWith("ALTER", d.SubmitSql);
        }
    }

    [Fact]
    public void Dummy_tooShortTarget_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecryptCore.BuildDummyModule("P", "[dbo].[x]", null, 3));
    }

    [Fact]
    public void Dummy_triggerParentMissing_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DecryptCore.BuildDummyModule("TR", "[dbo].[trg]", null, 500));
    }

    [Fact]
    public void Dummy_unknownKind_throws()
    {
        Assert.Throws<NotSupportedException>(
            () => DecryptCore.BuildDummyModule("XX", "[dbo].[x]", null, 500));
    }

    [Fact]
    public void XorRecover_lengthMismatch_throws()
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
    public void DacDataSource_builtCorrectly(string girdi, int? port, string expected)
        => Assert.Equal(expected, DecryptCore.BuildDacDataSource(girdi, port));

    // ─── DAC yalnız şifreli hedef varsa (22.11.2026: bkmrapor şifresiz dbo.fat_ekle'yi okuyamıyordu) ───

    [Theory]
    [InlineData(new bool[0], false)]                 // hedef yok
    [InlineData(new[] { false }, false)]             // tek şifresiz → DAC yok
    [InlineData(new[] { false, false, false }, false)] // hepsi şifresiz → DAC yok
    [InlineData(new[] { true }, true)]               // tek şifreli → DAC
    [InlineData(new[] { false, true, false }, true)] // karışık → DAC (bir kez)
    public void NeedsDac_onlyWhenAnyTargetEncrypted(bool[] flags, bool expected)
        => Assert.Equal(expected, DecryptCore.NeedsDac(flags));

    [Fact]
    public void DacFailure_remoteAlreadyOn_pointsToOtherSession()
    {
        var msg = DecryptCore.DescribeDacFailure("err 42", 42, DecryptCore.RemoteDacState.AlreadyOn);
        Assert.Contains("BAŞKA bir DAC oturumu", msg);
        Assert.Contains("ayara dokunmadı", msg);
    }

    [Fact]
    public void DacFailure_17810_pointsToOtherSession_evenIfStateUnknown()
    {
        var msg = DecryptCore.DescribeDacFailure("max 1 DAC", DecryptCore.DacAlreadyInUseError,
            DecryptCore.RemoteDacState.Unknown);
        Assert.Contains("BAŞKA bir DAC oturumu", msg);
        Assert.DoesNotContain("sp_configure", msg);   // açma tavsiyesi verilmez
    }

    [Fact]
    public void DacFailure_openedByUs_saysSettingWillBeRestored()
    {
        var msg = DecryptCore.DescribeDacFailure("err", 0, DecryptCore.RemoteDacState.OpenedByUs);
        Assert.Contains("eski haline (0) alınacak", msg);
        Assert.Contains("TEK oturuma", msg);
    }

    [Fact]
    public void DacFailure_offWithNoAutoDac_suggestsAutoDac()
    {
        var msg = DecryptCore.DescribeDacFailure("err 42", 42, DecryptCore.RemoteDacState.Off);
        Assert.Contains("--no-auto-dac", msg);
        Assert.DoesNotContain("BAŞKA bir DAC oturumu", msg);
    }
}
