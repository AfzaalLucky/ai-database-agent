using AiDatabaseAgent.Core.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Infrastructure.SqlServer;

/// <summary>
/// Discovers the queryable schema in two passes:
/// <list type="number">
///   <item>metadata (descriptions, keys, CHECK constraints) read with the API's own identity, which has
///         VIEW DEFINITION;</item>
///   <item>visibility — which columns the agent user may SELECT — and view column lineage, read while
///         impersonating that user.</item>
/// </list>
/// <see cref="SchemaAssembler"/> then combines them, so the model sees exactly what the agent may query.
/// The result is cached for <see cref="SqlServerOptions.SchemaCacheMinutes"/>.
/// </summary>
public sealed class SqlServerSchemaProvider(IOptions<SqlServerOptions> options, TimeProvider time) : ISchemaProvider, IDisposable
{
    private readonly SqlServerOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DatabaseSchema? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<DatabaseSchema> GetSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && time.GetUtcNow() - _cachedAt < TimeSpan.FromMinutes(_options.SchemaCacheMinutes))
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && time.GetUtcNow() - _cachedAt < TimeSpan.FromMinutes(_options.SchemaCacheMinutes))
                return _cached;
            var snapshot = await ReadCatalogAsync(cancellationToken);
            _cached = SchemaAssembler.Assemble(snapshot);
            _cachedAt = time.GetUtcNow();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the next call to re-read the catalog (e.g. after a migration).</summary>
    public void Invalidate() => _cached = null;

    public void Dispose() => _gate.Dispose();

    private async Task<CatalogSnapshot> ReadCatalogAsync(CancellationToken ct)
    {
        var objects = new List<(string Schema, string Name, TableKind Kind, string? Description)>();
        var primaryKeys = new List<(string Schema, string Table, string Column)>();
        var columns = new List<CatalogColumn>();
        var lineage = new Dictionary<ObjectColumn, (string? Schema, string? Table, string? Column)>();
        var foreignKeys = new List<(string Name, string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)>();
        var checks = new List<CatalogCheckConstraint>();
        string databaseName;

        await using (var connection = new SqlConnection(_options.ConnectionString))
        {
            await connection.OpenAsync(ct);
            databaseName = connection.Database;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = MetadataSql;
            await using var r = await cmd.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
                objects.Add((r.GetString(0), r.GetString(1), r.GetString(2) == "V" ? TableKind.View : TableKind.Table, r.IsDBNull(3) ? null : r.GetString(3)));

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                primaryKeys.Add((r.GetString(0), r.GetString(1), r.GetString(2)));

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                columns.Add(new CatalogColumn(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetString(8)));

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                foreignKeys.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                checks.Add(new CatalogCheckConstraint(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        var selectable = new HashSet<ObjectColumn>();
        await using (var session = await ImpersonatedSession.OpenAsync(_options.ConnectionString, _options.AgentUser, ct))
        {
            await using var cmd = session.CreateCommand(VisibilitySql, 30);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                selectable.Add(new ObjectColumn(r.GetString(0), r.GetString(1), r.GetString(2)));

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                lineage[new ObjectColumn(r.GetString(0), r.GetString(1), r.GetString(2))] =
                    (r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
        }

        var withLineage = columns.Select(c => lineage.TryGetValue(new ObjectColumn(c.Schema, c.Table, c.Name), out var src)
            ? c with { SourceSchema = src.Schema, SourceTable = src.Table, SourceColumn = src.Column }
            : c).ToList();

        return new CatalogSnapshot(
            databaseName,
            objects.Select(o => new CatalogObject(o.Schema, o.Name, o.Kind, o.Description,
                primaryKeys.Where(k => k.Schema == o.Schema && k.Table == o.Name).Select(k => k.Column).ToList())).ToList(),
            withLineage,
            foreignKeys.GroupBy(f => (f.Name, f.Schema, f.Table, f.RefSchema, f.RefTable))
                .Select(g => new CatalogForeignKey(g.Key.Name, g.Key.Schema, g.Key.Table, g.Select(x => x.Column).ToList(), g.Key.RefSchema, g.Key.RefTable, g.Select(x => x.RefColumn).ToList()))
                .ToList(),
            checks,
            selectable);
    }

    private const string MetadataSql = """
        SET NOCOUNT ON;

        -- 1. user tables and views
        SELECT s.name, o.name, RTRIM(o.type), CAST(ep.value AS nvarchar(4000))
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0;

        -- 2. primary key columns, in key order
        SELECT s.name, t.name, c.name
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.is_primary_key = 1
        ORDER BY s.name, t.name, ic.key_ordinal;

        -- 3. columns with a T-SQL type declaration
        SELECT s.name, o.name, c.name, c.column_id,
               CASE WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary') THEN CONCAT(ty.name, '(', IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(10))), ')')
                    WHEN ty.name IN ('nvarchar', 'nchar') THEN CONCAT(ty.name, '(', IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(10))), ')')
                    WHEN ty.name IN ('decimal', 'numeric') THEN CONCAT(ty.name, '(', c.precision, ',', c.scale, ')')
                    WHEN ty.name IN ('datetime2', 'time', 'datetimeoffset') THEN CONCAT(ty.name, '(', c.scale, ')')
                    ELSE ty.name END,
               c.is_nullable, c.is_identity, c.is_computed, CAST(ep.value AS nvarchar(4000))
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0;

        -- 4. foreign keys, one row per column pair
        SELECT fk.name, s.name, t.name, pc.name, rs.name, rt.name, rc.name
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables t ON t.object_id = fk.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fk.name, fkc.constraint_column_id;

        -- 5. column-level CHECK constraints
        SELECT s.name, t.name, c.name, cc.definition
        FROM sys.check_constraints cc
        JOIN sys.tables t ON t.object_id = cc.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
        WHERE cc.parent_column_id <> 0 AND cc.is_disabled = 0;
        """;

    // Runs as the agent user: only columns it can actually SELECT.
    private const string VisibilitySql = """
        SET NOCOUNT ON;
        SELECT s.name, o.name, c.name
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
          AND HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), N'OBJECT', N'SELECT', c.name, N'COLUMN') = 1;

        -- View column lineage (which base column each view column exposes). Run here, as the agent user,
        -- because describing a view needs SELECT on it: the API's own identity (VIEW DEFINITION only) gets
        -- no rows. The _for_object variant only describes procedures/triggers; browse mode 1 reports sources.
        SELECT s.name, v.name, d.name, d.source_schema, d.source_table, d.source_column
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        CROSS APPLY sys.dm_exec_describe_first_result_set(N'SELECT * FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(v.name), NULL, 1) d
        WHERE v.is_ms_shipped = 0 AND d.name IS NOT NULL AND d.is_hidden = 0;
        """;
}
