using SqlCli.Core;

namespace SqlCli.Tests;

// MCP satır tavanı: sunucuya maxRows+1 satır sorulmalı ki okuyucu fazladan satırı görüp "truncated" diyebilsin.
public class RowCapTests
{
    [Fact]
    public void SqlServer_AddsTopOfMaxRowsPlusOne()
    {
        var sql = McpServer.CappedSql(new SqlServerDialect(), "SELECT a FROM t", 200);
        Assert.Contains("TOP 201", sql);
    }

    [Fact]
    public void Postgres_AddsLimitOfMaxRowsPlusOne()
    {
        var sql = McpServer.CappedSql(new PostgresDialect(), "SELECT a FROM t", 5);
        Assert.EndsWith("LIMIT 6", sql);
    }

    [Fact]
    public void LeavesUserOwnTopUntouched()
    {
        var sql = "SELECT TOP 3 a FROM t";
        Assert.Equal(sql, McpServer.CappedSql(new SqlServerDialect(), sql, 200));
    }
}
