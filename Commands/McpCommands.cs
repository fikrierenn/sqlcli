using Cocona;

namespace SqlCli.Commands;

/// <summary>
/// `sqlcli mcp` yalnız --help listesinde görünsün diye burada tanımlıdır. Gerçek çalıştırma
/// Program.cs'te Cocona'dan ÖNCE yakalanır (stdout yalnız JSON-RPC olmalı; banner/Cocona araya girmemeli).
/// Bu gövdeye normalde hiç girilmez.
/// </summary>
public class McpCommands
{
    [Command("mcp", Description = "MCP sunucusu (stdio): Claude Desktop/Code gibi istemcilere SALT-OKUMA araçları sunar. " +
        "Kullanım: sqlcli mcp [--profile <ad>] [--conn <bağlantı>] [--config <klasör>]")]
    public int Mcp(
        [Option(Description = "Bağlantı profili (sqlcli.json > Profiles)")] string? profile = null,
        [Option(Description = "Doğrudan bağlantı dizesi (config'e yazmayın; ortam değişkeni tercih edin)")] string? conn = null,
        [Option(Description = "sqlcli.json ve .env'nin bulunduğu klasör (istemci rastgele dizinde başlatırsa)")] string? config = null)
    {
        Console.Error.WriteLine("Bu komut bir MCP istemcisi tarafından başlatılmak içindir: sqlcli mcp --profile zirve");
        return 2;
    }
}
