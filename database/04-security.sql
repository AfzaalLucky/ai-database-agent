/*
  FoundryTrade sample database — 04: least-privilege identity for agent-generated queries.

  The agent never runs model-generated SQL with the application's own permissions. Each query runs
  inside `EXECUTE AS USER = 'agent_reader' WITH COOKIE ... REVERT WITH COOKIE`, so even a query that
  slipped past application-level validation could only read what this role can read:

    * SELECT on the sales, catalog and inventory schemas;
    * SELECT on hr.Departments and on hr.EmployeeDirectory, a view of hr.Employees WITHOUT Salary.
      The base table itself is DENIED. (A column-level DENY on Salary was tried first: it makes a
      plain `SELECT COUNT(*) FROM hr.Employees` fail with error 230, which breaks ordinary questions.
      A view keeps the sensitive column out of both the permissions and the schema the model sees.)
    * SHOWPLAN, so the agent can ask for an estimated plan (cost check) before executing;
    * nothing else: no INSERT/UPDATE/DELETE/EXECUTE/DDL, no other schemas.

  agent_reader is a user WITHOUT LOGIN: nobody can connect as it. In production, the API's own
  login needs only CONNECT, VIEW DEFINITION (to read the schema) and IMPERSONATE on this user.
*/
SET NOCOUNT ON;
USE FoundryTrade;
GO

CREATE VIEW hr.EmployeeDirectory
AS
SELECT EmployeeId, FirstName, LastName, Email, JobTitle, DepartmentId, ManagerId, HireDate, TerminationDate
FROM hr.Employees;
GO
-- The description must not name hr.Employees: it is shown to the model, and the hidden table should not be advertised.
EXEC sys.sp_addextendedproperty N'MS_Description',
    N'Staff directory without confidential pay data. ManagerId is the EmployeeId of the direct manager (NULL for the CEO); TerminationDate NULL means currently employed. Sales reps are employees in the Sales department.',
    N'SCHEMA', N'hr', N'VIEW', N'EmployeeDirectory';
GO

CREATE ROLE agent_readers AUTHORIZATION dbo;
GO

GRANT SELECT ON SCHEMA::sales TO agent_readers;
GRANT SELECT ON SCHEMA::catalog TO agent_readers;
GRANT SELECT ON SCHEMA::inventory TO agent_readers;
GRANT SELECT ON hr.Departments TO agent_readers;
GRANT SELECT ON hr.EmployeeDirectory TO agent_readers;
DENY SELECT ON hr.Employees TO agent_readers;
GRANT SHOWPLAN TO agent_readers;
GO

CREATE USER agent_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA = sales;
ALTER ROLE agent_readers ADD MEMBER agent_reader;
GO
