using SqlCli.Core;

namespace SqlCli.Tests;

// Salt-okuma muhafizi (izin listesi). copy v2.4'ten beri kaynak sorguyu bundan gecirir.
public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT Id FROM dbo.irsHrk")]
    [InlineData("WITH a AS (SELECT 1 AS x) SELECT x FROM a")]
    [InlineData("-- UPDATE dbo.irsHrk SET x = 1\nSELECT 1")]           // yorumdaki yazma sayilmaz
    [InlineData("/* DELETE FROM t */ SELECT 1")]
    [InlineData("SELECT 'UPDATE t SET x=1; DROP TABLE t' AS metin")]    // metindeki yazma sayilmaz
    [InlineData("SELECT 1;")]                                            // sondaki tek ; serbest
    public void Read_accepted(string sql) => Assert.Null(SqlGuard.Check(sql, flag: true));

    [Theory]
    [InlineData("UPDATE dbo.irsHrk SET x = 1", "UPDATE")]
    [InlineData("DELETE FROM dbo.irsHrk", "DELETE")]
    [InlineData("SELECT * INTO dbo.Yeni FROM dbo.irsHrk", "INTO")]
    [InlineData("SELECT 1; DELETE FROM dbo.irsHrk", "ayrı ifade")]
    [InlineData("EXEC dbo.usp_Yaz", "EXEC")]
    [InlineData("SELECT * FROM OPENQUERY(L, 'SELECT 1')", "OPENQUERY")]
    [InlineData("WITH a AS (SELECT 1 AS x) UPDATE t SET y = 1", "UPDATE")]
    [InlineData("DECLARE @x int; SELECT @x", "")]
    public void Write_rejected(string sql, string expected)
    {
        var red = SqlGuard.Check(sql, flag: true);
        Assert.NotNull(red);
        Assert.Contains(expected, red);
    }

    [Fact]
    public void Does_not_check_when_disabled()
    {
        var eski = Environment.GetEnvironmentVariable("SQLCLI_READONLY");
        try
        {
            Environment.SetEnvironmentVariable("SQLCLI_READONLY", null);
            Assert.Null(SqlGuard.Check("UPDATE t SET x=1", flag: false));
        }
        finally { Environment.SetEnvironmentVariable("SQLCLI_READONLY", eski); }
    }
}
