using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.IntegrationTests;

/// <summary>
/// Points at a FoundryTrade database built by <c>database/setup.ps1</c>. Override the connection with
/// the <c>FOUNDRYTRADE_CONNECTION</c> environment variable (e.g. for a Docker SQL Server in CI).
/// </summary>
public sealed class FoundryTradeFixture
{
    public const string DefaultConnection =
        "Server=localhost;Database=FoundryTrade;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=AiDatabaseAgent.Tests";

    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("FOUNDRYTRADE_CONNECTION") ?? DefaultConnection;

    public IOptions<SqlServerOptions> Options => Microsoft.Extensions.Options.Options.Create(new SqlServerOptions { ConnectionString = ConnectionString });
}

[CollectionDefinition(Name)]
public sealed class FoundryTradeCollection : ICollectionFixture<FoundryTradeFixture>
{
    public const string Name = "FoundryTrade";
}
