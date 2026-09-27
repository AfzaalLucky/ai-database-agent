using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.Data.SqlClient;

namespace AiDatabaseAgent.IntegrationTests.SqlServer;

[Collection(FoundryTradeCollection.Name)]
public class SqlServerSchemaProviderTests(FoundryTradeFixture db)
{
    private async Task<DatabaseSchema> Schema()
    {
        using var provider = new SqlServerSchemaProvider(db.Options, TimeProvider.System);
        return await provider.GetSchemaAsync();
    }

    [Fact]
    public async Task Sees_the_business_tables_but_not_the_confidential_ones()
    {
        var schema = await Schema();

        Assert.Equal("FoundryTrade", schema.DatabaseName);
        foreach (var name in new[] { "sales.Orders", "sales.OrderLines", "sales.Customers", "sales.Payments", "catalog.Products", "catalog.Categories", "catalog.Suppliers", "inventory.Stock", "inventory.Warehouses", "hr.Departments", "hr.EmployeeDirectory" })
            Assert.Contains(schema.Tables, t => t.QualifiedName == name);
        Assert.Null(schema.Find("hr", "Employees"));
        Assert.DoesNotContain(schema.Tables.SelectMany(t => t.Columns), c => c.Name == "Salary");
        Assert.Equal(11, schema.Tables.Count);
    }

    [Fact]
    public async Task Carries_descriptions_types_keys_and_allowed_values()
    {
        var schema = await Schema();
        var orders = schema.Find("sales", "Orders")!;

        Assert.StartsWith("Sales orders (header)", orders.Description);
        Assert.Equal(["OrderId"], orders.PrimaryKey);
        Assert.Equal(["Cancelled", "Delivered", "Pending", "Returned", "Shipped"], orders.Column("Status")!.AllowedValues);
        Assert.Equal("datetime2(0)", orders.Column("OrderDate")!.SqlType);
        Assert.True(schema.Find("sales", "OrderLines")!.Column("LineTotal")!.IsComputed);
        Assert.Equal("decimal(10,2)", schema.Find("catalog", "Products")!.Column("ListPrice")!.SqlType);
        Assert.Contains(orders.ForeignKeys, k => k.Columns[0] == "CustomerId" && k.ReferencedQualifiedName == "sales.Customers");
    }

    [Fact]
    public async Task Joins_through_the_hidden_employee_table_point_at_the_directory_view()
    {
        var schema = await Schema();

        Assert.Contains(schema.Find("sales", "Orders")!.ForeignKeys, k => k.Columns[0] == "SalesRepId" && k.ReferencedQualifiedName == "hr.EmployeeDirectory");
        var directory = schema.Find("hr", "EmployeeDirectory")!;
        Assert.Equal(["EmployeeId"], directory.PrimaryKey);
        Assert.Equal("Work email address.", directory.Column("Email")!.Description);
        Assert.Contains(directory.ForeignKeys, k => k.Columns[0] == "ManagerId" && k.ReferencedQualifiedName == "hr.EmployeeDirectory");
        Assert.Contains(directory.ForeignKeys, k => k.Columns[0] == "DepartmentId" && k.ReferencedQualifiedName == "hr.Departments");
    }
}

[Collection(FoundryTradeCollection.Name)]
public class SqlServerQueryExecutorTests(FoundryTradeFixture db)
{
    private SqlServerQueryExecutor Executor => new(db.Options);

    private Task<QueryResult> Run(string sql, int maxRows = 500, int timeout = 15) =>
        Executor.ExecuteAsync(sql, new QueryLimits(maxRows, timeout));

    [Fact]
    public async Task Runs_as_the_agent_user_and_returns_typed_values()
    {
        var r = await Run("SELECT USER_NAME() AS [User], COUNT(*) AS Orders, SUM(LineTotal) AS Revenue FROM sales.OrderLines l JOIN sales.Orders o ON o.OrderId = l.OrderId WHERE o.OrderId <= 10");

        Assert.Equal(["User", "Orders", "Revenue"], r.Columns.Select(c => c.Name));
        Assert.Equal("agent_reader", r.Rows[0][0]);
        Assert.IsType<int>(r.Rows[0][1]);
        Assert.IsType<decimal>(r.Rows[0][2]);
    }

    [Fact]
    public async Task Stops_at_the_row_cap()
    {
        var r = await Run("SELECT OrderId FROM sales.Orders ORDER BY OrderId", maxRows: 25);

        Assert.Equal(25, r.Rows.Count);
        Assert.True(r.Truncated);
    }

    [Theory]
    [InlineData("UPDATE sales.Orders SET Status = 'Pending' WHERE OrderId = 1", 229)]
    [InlineData("DELETE FROM sales.Payments WHERE PaymentId = 1", 229)]
    [InlineData("INSERT sales.Customers (FirstName) VALUES ('x')", 229)]
    [InlineData("SELECT Salary FROM hr.Employees", 229)]
    [InlineData("SELECT COUNT(*) FROM hr.Employees", 229)]
    [InlineData("CREATE TABLE sales.Evil (Id int)", 262)]
    [InlineData("DROP TABLE sales.Payments", 3701)]
    [InlineData("REVERT", 15591)]
    public async Task The_database_refuses_anything_beyond_reading_granted_objects(string sql, int errorNumber)
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Run(sql));

        Assert.Equal(errorNumber, ex.ErrorNumber);
        await AssertDataUntouched();
    }

    [Fact]
    public async Task Times_out_long_queries()
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Run("WAITFOR DELAY '00:00:10'; SELECT 1", timeout: 1));

        Assert.Equal(-2, ex.ErrorNumber);
    }

    [Fact]
    public async Task Pooled_connections_come_back_with_the_original_identity()
    {
        // Exhaust a few sessions, including failing ones, then check a plain pooled connection.
        for (var i = 0; i < 5; i++)
        {
            await Run("SELECT 1");
            await Assert.ThrowsAsync<QueryExecutionException>(() => Run("SELECT Salary FROM hr.Employees"));
        }

        await using var plain = new SqlConnection(db.ConnectionString);
        await plain.OpenAsync();
        await using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT USER_NAME()";
        Assert.NotEqual("agent_reader", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Estimates_cost_without_executing()
    {
        var estimate = await Executor.EstimateAsync("SELECT c.Country, COUNT(*) FROM sales.Orders o JOIN sales.Customers c ON c.CustomerId = o.CustomerId GROUP BY c.Country");

        Assert.InRange(estimate.EstimatedCost, 0.001, 10);
        Assert.InRange(estimate.EstimatedRows, 1, 1000);

        // A permission problem is reported at compile time too.
        await Assert.ThrowsAsync<QueryExecutionException>(() => Executor.EstimateAsync("SELECT Salary FROM hr.Employees"));
        // And the session was switched back out of SHOWPLAN mode: a normal query runs.
        Assert.Equal(1, (await Run("SELECT 1")).Rows[0][0]);
    }

    private async Task AssertDataUntouched()
    {
        var r = await Run("SELECT (SELECT COUNT(*) FROM sales.Orders), (SELECT COUNT(*) FROM sales.Payments), (SELECT Status FROM sales.Orders WHERE OrderId = 1), OBJECT_ID('sales.Evil')");
        Assert.Equal(6000, r.Rows[0][0]);
        Assert.Equal(5831, r.Rows[0][1]);
        Assert.NotNull(r.Rows[0][2]);
        Assert.Null(r.Rows[0][3]);
    }
}
