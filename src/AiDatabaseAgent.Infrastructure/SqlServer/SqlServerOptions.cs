namespace AiDatabaseAgent.Infrastructure.SqlServer;

public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    /// <summary>
    /// Connection for the API's own identity. It needs CONNECT, VIEW DEFINITION and IMPERSONATE on
    /// <see cref="AgentUser"/> — nothing more. Generated SQL never runs with these permissions.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Database user (created WITHOUT LOGIN) that every agent query is executed as.</summary>
    public string AgentUser { get; set; } = "agent_reader";

    /// <summary>How long discovered schema metadata is reused before being read again.</summary>
    public int SchemaCacheMinutes { get; set; } = 10;
}
