/*
  Operator queries over the audit log (AgentOps.audit.QueryLog).
  Run with: sqlcmd -S localhost -E -C -d AgentOps -i database/ops/audit-report.sql
*/
SET NOCOUNT ON;
DECLARE @since datetime2(3) = DATEADD(DAY, -7, SYSUTCDATETIME());

-- 1. Volume and outcome mix per version.
SELECT Version,
       COUNT(*)                                                         AS Questions,
       SUM(CASE WHEN Outcome = 'Answered' THEN 1 ELSE 0 END)            AS Answered,
       SUM(CASE WHEN Outcome = 'NotAnswerable' THEN 1 ELSE 0 END)       AS Declined,
       SUM(CASE WHEN Outcome IN ('Rejected', 'Failed') THEN 1 ELSE 0 END) AS RejectedOrFailed,
       SUM(CASE WHEN Outcome = 'Error' THEN 1 ELSE 0 END)               AS Errors,
       -- V5's harness self-check is always a second model turn, so for V5 this counts self-checks too.
       SUM(CASE WHEN Attempts > 1 THEN 1 ELSE 0 END)                    AS MoreThanOneAttempt
FROM audit.QueryLog
WHERE AskedAtUtc >= @since
GROUP BY Version
ORDER BY Version;

-- 2. Latency percentiles and token use per version (answered questions).
SELECT DISTINCT Version,
       PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY TotalMs) OVER (PARTITION BY Version) AS P50TotalMs,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY TotalMs) OVER (PARTITION BY Version) AS P95TotalMs,
       PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY ModelMs) OVER (PARTITION BY Version) AS P50ModelMs,
       AVG(CAST(InputTokens AS float))  OVER (PARTITION BY Version)                      AS AvgInputTokens,
       AVG(CAST(OutputTokens AS float)) OVER (PARTITION BY Version)                      AS AvgOutputTokens
FROM audit.QueryLog
WHERE AskedAtUtc >= @since AND Outcome = 'Answered';

-- 3. Everything that did not end in an answer, newest first: the review queue.
SELECT TOP (50) AskedAtUtc, Version, Outcome, Attempts, Question, Message, [Sql], TraceId
FROM audit.QueryLog
WHERE AskedAtUtc >= @since AND Outcome <> 'Answered'
ORDER BY AskedAtUtc DESC;

-- 4. Slowest answers.
SELECT TOP (10) AskedAtUtc, Version, TotalMs, ModelMs, QueryMs, Attempts, Question, TraceId
FROM audit.QueryLog
WHERE AskedAtUtc >= @since
ORDER BY TotalMs DESC;
