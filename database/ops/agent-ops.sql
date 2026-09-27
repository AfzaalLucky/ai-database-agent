/*
  AgentOps — the agent's own operational database (audit log). Idempotent: safe to run on every setup.

  Deliberately NOT inside FoundryTrade: FoundryTrade stands in for a customer's business database, which the
  agent only ever reads (as agent_reader). What the agent records about its own work lives here, with its own
  permissions, and survives FoundryTrade being dropped and re-seeded by setup.ps1.

  Production grants for the API's login (see docs/deployment.md):
      CREATE USER [agent_api] FOR LOGIN [agent_api];
      GRANT INSERT ON audit.QueryLog TO [agent_api];
  Nothing else: the API never reads the log back; operators query it directly (database/ops/audit-report.sql).
*/
SET NOCOUNT ON;

IF DB_ID(N'AgentOps') IS NULL
    CREATE DATABASE AgentOps;
GO

USE AgentOps;
GO

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA audit AUTHORIZATION dbo');
GO

IF OBJECT_ID(N'audit.QueryLog', N'U') IS NULL
BEGIN
    CREATE TABLE audit.QueryLog
    (
        QueryLogId    bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_QueryLog PRIMARY KEY,
        AskedAtUtc    datetime2(3)   NOT NULL,
        TraceId       char(32)       NULL,     -- W3C trace id: find the full trace in the tracing backend
        Version       varchar(4)     NOT NULL, -- V1..V5
        Model         nvarchar(100)  NOT NULL,
        Question      nvarchar(1000) NOT NULL,
        Outcome       varchar(20)    NOT NULL, -- Answered, NotAnswerable, Rejected, Failed, Error
        [Sql]         nvarchar(max)  NULL,     -- the query that was run (or last rejected)
        Message       nvarchar(1000) NULL,     -- refusal reason or error
        Attempts      smallint       NOT NULL, -- model round trips; > 1 means a repair happened
        ToolCalls     smallint       NOT NULL, -- V5
        RowsReturned  int            NULL,
        Truncated     bit            NULL,
        EstimatedCost float          NULL,
        TotalMs       int            NOT NULL,
        ModelMs       int            NOT NULL,
        QueryMs       int            NULL,
        InputTokens   int            NULL,
        OutputTokens  int            NULL,
        KnowledgeIds  nvarchar(400)  NULL,     -- V4+: glossary entries retrieved
        CONSTRAINT CK_QueryLog_Outcome CHECK (Outcome IN ('Answered', 'NotAnswerable', 'Rejected', 'Failed', 'Error'))
    );

    CREATE INDEX IX_QueryLog_AskedAtUtc ON audit.QueryLog (AskedAtUtc) INCLUDE (Version, Outcome, TotalMs, Attempts);
END
GO

PRINT 'AgentOps is ready.';
