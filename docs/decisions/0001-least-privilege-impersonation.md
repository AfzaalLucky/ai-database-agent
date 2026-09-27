# 0001 — Run generated SQL as a login-less, least-privilege user via impersonation

- Status: accepted
- Date: 2026-09-26

## Context

Model-generated SQL is untrusted input. Application-level checks can be wrong or bypassed, so the database
itself must refuse anything beyond reading the data the agent is meant to see. Development uses Windows
authentication (no stored secrets), and the API also needs broader metadata access for schema discovery.

Options considered:

1. **A separate SQL login with a password** for query execution — standard, but adds a secret to store and
   rotate, and doesn't work with Windows-authentication-only servers.
2. **Application-level validation only** — a single layer; one parser gap away from a write.
3. **A user WITHOUT LOGIN, impersonated per query** with `EXECUTE AS USER … WITH COOKIE`.

## Decision

Option 3. `database/04-security.sql` creates role `agent_readers` (SELECT on `sales`, `catalog`, `inventory`,
`hr.Departments`, `hr.EmployeeDirectory`; DENY on `hr.Employees`; SHOWPLAN) and user `agent_reader WITHOUT LOGIN`.
`ImpersonatedSession` opens a connection, runs

```sql
DECLARE @cookie varbinary(8000);
EXECUTE AS USER = N'agent_reader' WITH COOKIE INTO @cookie;
SELECT @cookie;
```

runs the generated SQL as a separate batch, and in `DisposeAsync` runs `REVERT WITH COOKIE = 0x…`. If the
revert fails for any reason the connection pool is cleared, so an impersonated connection is never reused.

## Consequences

- Nobody can connect as `agent_reader`; it exists only to be impersonated.
- Generated SQL can't `REVERT` out of the context: it doesn't have the cookie (verified: error 15591).
- **Both statements must be ad hoc batches.** Sending them as parameterised commands runs them inside
  `sp_executesql`, and SQL Server rejects `WITH COOKIE` there with error **15590**. The user name therefore
  comes from configuration, validated as a simple identifier, and is embedded; the cookie is embedded as a
  binary literal. Found by the integration tests on the first run.
- In production the API's own login needs only `CONNECT`, `VIEW DEFINITION` and `IMPERSONATE ON USER::agent_reader`
  (verified with a stand-in user, see [setup](../setup.md#production-database-identity)).
- Impersonation is database-scoped, but `master` still admits guest reads — so cross-database names are also
  refused by the V3 validator ([0004](0004-scriptdom-allow-list-validation.md)).
- Integration tests assert: writes, DDL, `DROP`, salary access and `REVERT` fail with the expected error
  numbers; data is untouched; pooled connections come back with the original identity.
