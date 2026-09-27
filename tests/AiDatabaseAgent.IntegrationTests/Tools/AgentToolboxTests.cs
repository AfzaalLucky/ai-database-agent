using AiDatabaseAgent.Core.Tools;
using AiDatabaseAgent.Infrastructure.SqlServer;

namespace AiDatabaseAgent.IntegrationTests.Tools;

/// <summary>The V5 tools against the real FoundryTrade schema and the agent_reader identity.</summary>
[Collection(FoundryTradeCollection.Name)]
public class AgentToolboxTests(FoundryTradeFixture db)
{
    private async Task<AgentToolbox> Toolbox()
    {
        using var provider = new SqlServerSchemaProvider(db.Options, TimeProvider.System);
        return new AgentToolbox(await provider.GetSchemaAsync(), new SqlServerQueryExecutor(db.Options), knowledge: null, maxEstimatedCost: 200, timeoutSeconds: 15);
    }

    [Fact]
    public async Task List_and_describe_show_only_what_the_agent_can_query()
    {
        var tools = await Toolbox();

        var list = tools.ListTables();
        Assert.Contains("hr.EmployeeDirectory (view) — Staff directory", list, StringComparison.Ordinal);
        Assert.DoesNotContain("hr.Employees ", list, StringComparison.Ordinal);

        Assert.Contains("SalesRepId int NULL -> hr.EmployeeDirectory.EmployeeId", tools.DescribeTable("sales.Orders"), StringComparison.Ordinal);
        Assert.StartsWith("No table named 'hr.Employees'", tools.DescribeTable("hr.Employees"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_values_returns_real_values_and_refuses_hidden_columns()
    {
        var tools = await Toolbox();

        Assert.Equal("Cancelled, Delivered, Pending, Returned, Shipped", await tools.SampleValues("sales.Orders", "Status"));
        Assert.Equal("Chicago Hub, Leeds Depot, Rotterdam Central, Singapore East", await tools.SampleValues("[inventory].[Warehouses]", "Name"));
        Assert.StartsWith("No column 'Salary' on hr.EmployeeDirectory", await tools.SampleValues("hr.EmployeeDirectory", "Salary"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_query_previews_rows_and_applies_the_v3_gate()
    {
        var tools = await Toolbox();

        var preview = await tools.RunQuery("SELECT OrderId FROM sales.Orders ORDER BY OrderId");
        Assert.StartsWith("OrderId\n1\n2\n", preview, StringComparison.Ordinal);
        Assert.EndsWith($"(first {AgentToolbox.PreviewRows} rows; more exist)", preview, StringComparison.Ordinal);

        Assert.StartsWith("Rejected:", await tools.RunQuery("DELETE FROM sales.Payments"), StringComparison.Ordinal);
        Assert.StartsWith("Rejected:", await tools.RunQuery("SELECT name FROM master.sys.sql_logins"), StringComparison.Ordinal);
        Assert.Contains("It exists on inventory.Stock (alias s)", await tools.RunQuery("SELECT p.Name FROM catalog.Products p JOIN inventory.Stock s ON s.ProductId = p.ProductId WHERE s.QuantityOnHand < p.ReorderLevel"), StringComparison.Ordinal);
        Assert.All(tools.Calls, c => Assert.Equal("run_query", c.Name));
        Assert.Equal([true, false, false, false], tools.Calls.Select(c => c.Succeeded));
    }
}
