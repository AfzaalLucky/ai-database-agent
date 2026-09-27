using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Prompting;
using AiDatabaseAgent.Core.Schema;
using Microsoft.Extensions.AI;

namespace AiDatabaseAgent.Core.Tests.Prompting;

public class ModelResponseParserTests
{
    [Fact]
    public void Reads_a_fenced_block_and_the_sentence_before_it()
    {
        var p = ModelResponseParser.Parse("Counts orders per country.\n```sql\nSELECT c.Country, COUNT(*) AS Orders\nFROM sales.Orders o;\n```");

        Assert.Equal("SELECT c.Country, COUNT(*) AS Orders\nFROM sales.Orders o", p.Sql);
        Assert.Equal("Counts orders per country.", p.Explanation);
        Assert.Null(p.CannotAnswerReason);
    }

    [Theory]
    [InlineData("```\nSELECT 1\n```")]
    [InlineData("```tsql\nSELECT 1\n```")]
    [InlineData("SELECT 1;")]
    [InlineData("Sure! Here you go:\nSELECT 1")]
    public void Tolerates_missing_fences_and_tags(string reply) =>
        Assert.Equal("SELECT 1", ModelResponseParser.Parse(reply).Sql);

    [Fact]
    public void Keeps_common_table_expressions()
    {
        var p = ModelResponseParser.Parse("WITH x AS (SELECT 1 AS n)\nSELECT n FROM x");

        Assert.StartsWith("WITH x AS", p.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognises_a_refusal()
    {
        var p = ModelResponseParser.Parse("CANNOT_ANSWER: salaries are not available.");

        Assert.Null(p.Sql);
        Assert.Equal("salaries are not available", p.CannotAnswerReason);
    }

    [Fact]
    public void Returns_nothing_for_prose_without_sql() =>
        Assert.Equal(new ParsedResponse(null, null, null), ModelResponseParser.Parse("I am not sure what you mean."));
}

public class PromptBuilderTests
{
    private static readonly DatabaseSchema Schema = new("Shop",
    [
        new SchemaTable("sales", "Customers", TableKind.Table, "Customers.",
            [new("CustomerId", "int", false, true, false, null, []), new("Country", "nvarchar(60)", false, false, false, "Country name.", [])],
            ["CustomerId"], []),
        new SchemaTable("sales", "Orders", TableKind.Table, "Orders. Exclude Cancelled\nfor revenue.",
            [
                new("OrderId", "int", false, true, false, null, []),
                new("CustomerId", "int", false, false, false, null, []),
                new("Status", "varchar(10)", false, false, false, "Order status.", ["Cancelled", "Shipped"]),
                new("ShippedDate", "datetime2(0)", true, false, false, null, []),
            ],
            ["OrderId"],
            [new ForeignKey("FK_Orders_Customers", ["CustomerId"], "sales", "Customers", ["CustomerId"])]),
        new SchemaTable("inventory", "Stock", TableKind.Table, null,
            [new("WarehouseId", "int", false, false, false, null, []), new("ProductId", "int", false, false, false, null, [])],
            ["WarehouseId", "ProductId"], []),
    ]);

    [Fact]
    public void V1_lists_names_only()
    {
        var messages = BasicPromptBuilder.Build(Schema, "How many orders?");

        var user = messages[^1].Text;
        Assert.Contains("- sales.Orders(OrderId, CustomerId, Status, ShippedDate)", user, StringComparison.Ordinal);
        Assert.DoesNotContain("varchar", user, StringComparison.Ordinal);
        Assert.EndsWith("Question: How many orders?", user, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_renders_types_keys_joins_descriptions_and_values()
    {
        var ddl = SchemaAwarePromptBuilder.RenderSchema(Schema);

        Assert.Contains("-- Orders. Exclude Cancelled for revenue.\nTABLE sales.Orders (", ddl, StringComparison.Ordinal);
        Assert.Contains("  OrderId int PRIMARY KEY,\n", ddl, StringComparison.Ordinal);
        Assert.Contains("  CustomerId int NOT NULL -> sales.Customers.CustomerId,\n", ddl, StringComparison.Ordinal);
        Assert.Contains("  Status varchar(10) NOT NULL, -- Order status. | values: 'Cancelled', 'Shipped'\n", ddl, StringComparison.Ordinal);
        Assert.Contains("  ShippedDate datetime2(0) NULL\n)", ddl, StringComparison.Ordinal);
        Assert.Contains("  PRIMARY KEY (WarehouseId, ProductId)\n", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_adds_rules_notes_date_and_examples_as_prior_turns()
    {
        var messages = SchemaAwarePromptBuilder.Build(Schema, "Orders by country?",
            ["Data ends on 2026-08-31."],
            [new FewShotExample("How many customers?", "SELECT COUNT(*) FROM sales.Customers")],
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        var system = messages[0].Text;
        Assert.Contains("never LIMIT", system, StringComparison.Ordinal);
        Assert.Contains("CANNOT_ANSWER", system, StringComparison.Ordinal);
        Assert.Contains("- Today's date is 2026-09-27.", system, StringComparison.Ordinal);
        Assert.Contains("- Data ends on 2026-08-31.", system, StringComparison.Ordinal);
        Assert.Contains("```sql\nSELECT COUNT(*) FROM sales.Customers\n```", messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("Orders by country?", messages[^1].Text);
    }
}
