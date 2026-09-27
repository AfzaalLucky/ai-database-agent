using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Validation;

namespace AiDatabaseAgent.Core.Tests.Validation;

/// <summary>
/// Regression for a live failure: the model wrote p.ReorderLevel (Products) instead of s.ReorderLevel (Stock).
/// Told only "no such column on Products", it concluded the data didn't exist and declined. The feedback
/// must point at the table that has the column.
/// </summary>
public class WrongTableHintTests
{
    private static SchemaColumn Col(string name) => new(name, "int", false, false, false, null, []);

    private static readonly DatabaseSchema Schema = new("FoundryTrade",
    [
        new SchemaTable("catalog", "Products", TableKind.Table, null, [Col("ProductId"), Col("Sku")], ["ProductId"], []),
        new SchemaTable("inventory", "Stock", TableKind.Table, null, [Col("WarehouseId"), Col("ProductId"), Col("QuantityOnHand"), Col("ReorderLevel")], [], []),
        new SchemaTable("inventory", "Warehouses", TableKind.Table, null, [Col("WarehouseId"), Col("Name")], ["WarehouseId"], []),
        new SchemaTable("hr", "EmployeeDirectory", TableKind.View, null, [Col("EmployeeId")], ["EmployeeId"], []),
    ]);

    [Fact]
    public void Points_to_the_table_in_the_query_that_has_the_column()
    {
        var issue = Assert.Single(SqlValidator.Validate(
            "SELECT p.Sku FROM catalog.Products AS p JOIN inventory.Stock AS s ON s.ProductId = p.ProductId WHERE s.QuantityOnHand < p.ReorderLevel",
            Schema).Issues);

        Assert.EndsWith("It exists on inventory.Stock (alias s), which is already in the query.", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Otherwise_points_anywhere_in_the_schema()
    {
        var issue = Assert.Single(SqlValidator.Validate("SELECT p.ReorderLevel FROM catalog.Products p", Schema).Issues);

        Assert.EndsWith("It exists on inventory.Stock.", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adds_nothing_when_the_column_exists_nowhere()
    {
        var issue = Assert.Single(SqlValidator.Validate("SELECT e.Salary FROM hr.EmployeeDirectory e", Schema).Issues);

        Assert.EndsWith("Its columns are: EmployeeId.", issue.Message, StringComparison.Ordinal);
    }
}
