using AiDatabaseAgent.Core.Schema;

namespace AiDatabaseAgent.Core.Tests.Schema;

public class CheckConstraintParserTests
{
    [Theory]
    [InlineData("([Status]='Returned' OR [Status]='Cancelled' OR [Status]='Delivered')", "Status", "Cancelled|Delivered|Returned")]
    [InlineData("([Segment]='Enterprise')", "Segment", "Enterprise")]
    [InlineData("([Name]=N'O''Brien' OR [Name]=N'Smith')", "Name", "O'Brien|Smith")]
    public void Reads_in_lists(string definition, string column, string expected) =>
        Assert.Equal(expected.Split('|'), CheckConstraintParser.TryParseAllowedValues(definition, column));

    [Theory]
    [InlineData("([Salary]>(0))", "Salary")]
    [InlineData("([Quantity]>(0) OR [Quantity]=(1))", "Quantity")]
    [InlineData("([Status]='A' OR [Other]='B')", "Status")]
    [InlineData("([DiscountPct]>=(0) AND [DiscountPct]<=(50))", "DiscountPct")]
    public void Ignores_anything_else(string definition, string column) =>
        Assert.Null(CheckConstraintParser.TryParseAllowedValues(definition, column));
}

public class SchemaAssemblerTests
{
    // A miniature of FoundryTrade: hr.Employees is hidden, hr.EmployeeDirectory exposes it without Salary.
    private static CatalogSnapshot Snapshot(IEnumerable<ObjectColumn>? extraSelectable = null)
    {
        var columns = new List<CatalogColumn>
        {
            new("hr", "Departments", "DepartmentId", 1, "int", false, true, false, null),
            new("hr", "Departments", "Name", 2, "nvarchar(60)", false, false, false, "Department name."),
            new("hr", "Employees", "EmployeeId", 1, "int", false, true, false, null),
            new("hr", "Employees", "Email", 2, "nvarchar(120)", false, false, false, "Work email address."),
            new("hr", "Employees", "DepartmentId", 3, "int", false, false, false, null),
            new("hr", "Employees", "ManagerId", 4, "int", true, false, false, "Direct manager."),
            new("hr", "Employees", "Salary", 5, "decimal(12,2)", false, false, false, "Confidential."),
            new("hr", "EmployeeDirectory", "EmployeeId", 1, "int", false, false, false, null, "hr", "Employees", "EmployeeId"),
            new("hr", "EmployeeDirectory", "Email", 2, "nvarchar(120)", false, false, false, null, "hr", "Employees", "Email"),
            new("hr", "EmployeeDirectory", "DepartmentId", 3, "int", false, false, false, null, "hr", "Employees", "DepartmentId"),
            new("hr", "EmployeeDirectory", "ManagerId", 4, "int", true, false, false, null, "hr", "Employees", "ManagerId"),
            new("sales", "Orders", "OrderId", 1, "int", false, true, false, null),
            new("sales", "Orders", "SalesRepId", 2, "int", true, false, false, null),
            new("sales", "Orders", "Status", 3, "varchar(10)", false, false, false, null),
        };
        var selectable = columns
            .Where(c => c.Table != "Employees")
            .Select(c => new ObjectColumn(c.Schema, c.Table, c.Name))
            .Concat(extraSelectable ?? [])
            .ToHashSet();

        return new CatalogSnapshot(
            "Mini",
            [
                new("hr", "Departments", TableKind.Table, "Departments.", ["DepartmentId"]),
                new("hr", "Employees", TableKind.Table, "Staff.", ["EmployeeId"]),
                new("hr", "EmployeeDirectory", TableKind.View, "Directory.", []),
                new("sales", "Orders", TableKind.Table, "Orders.", ["OrderId"]),
            ],
            columns,
            [
                new("FK_Employees_Departments", "hr", "Employees", ["DepartmentId"], "hr", "Departments", ["DepartmentId"]),
                new("FK_Employees_Manager", "hr", "Employees", ["ManagerId"], "hr", "Employees", ["EmployeeId"]),
                new("FK_Orders_SalesRep", "sales", "Orders", ["SalesRepId"], "hr", "Employees", ["EmployeeId"]),
            ],
            [new("sales", "Orders", "Status", "([Status]='Shipped' OR [Status]='Pending')")],
            selectable);
    }

    [Fact]
    public void Hides_objects_and_columns_the_agent_cannot_select()
    {
        var schema = SchemaAssembler.Assemble(Snapshot());

        Assert.Null(schema.Find("hr", "Employees"));
        var directory = Assert.IsType<SchemaTable>(schema.Find("hr", "EmployeeDirectory"));
        Assert.DoesNotContain(directory.Columns, c => c.Name == "Salary");
        Assert.Equal(["EmployeeId", "Email", "DepartmentId", "ManagerId"], directory.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Only_selectable_columns_of_a_visible_table_are_kept()
    {
        var snapshot = Snapshot();
        var partial = snapshot with { Selectable = snapshot.Selectable.Where(s => !(s.Table == "Orders" && s.Column == "Status")).ToHashSet() };

        var orders = SchemaAssembler.Assemble(partial).Find("sales", "Orders")!;

        Assert.DoesNotContain(orders.Columns, c => c.Name == "Status");
    }

    [Fact]
    public void View_columns_inherit_base_descriptions_and_primary_key()
    {
        var directory = SchemaAssembler.Assemble(Snapshot()).Find("hr", "EmployeeDirectory")!;

        Assert.Equal("Work email address.", directory.Column("Email")!.Description);
        Assert.Equal(["EmployeeId"], directory.PrimaryKey);
    }

    [Fact]
    public void Foreign_keys_to_a_hidden_table_are_redirected_to_the_view_exposing_it()
    {
        var schema = SchemaAssembler.Assemble(Snapshot());

        var rep = Assert.Single(schema.Find("sales", "Orders")!.ForeignKeys);
        Assert.Equal(["SalesRepId"], rep.Columns);
        Assert.Equal("hr.EmployeeDirectory", rep.ReferencedQualifiedName);
        Assert.Equal(["EmployeeId"], rep.ReferencedColumns);

        var directoryKeys = schema.Find("hr", "EmployeeDirectory")!.ForeignKeys;
        Assert.Contains(directoryKeys, k => k.Columns[0] == "DepartmentId" && k.ReferencedQualifiedName == "hr.Departments");
        Assert.Contains(directoryKeys, k => k.Columns[0] == "ManagerId" && k.ReferencedQualifiedName == "hr.EmployeeDirectory");
    }

    [Fact]
    public void Enum_check_constraints_become_allowed_values()
    {
        var status = SchemaAssembler.Assemble(Snapshot()).Find("sales", "Orders")!.Column("Status")!;

        Assert.Equal(["Pending", "Shipped"], status.AllowedValues);
    }

    [Fact]
    public void A_key_whose_target_is_not_visible_anywhere_is_dropped()
    {
        var snapshot = Snapshot();
        var noDirectory = snapshot with { Selectable = snapshot.Selectable.Where(s => s.Table != "EmployeeDirectory").ToHashSet() };

        var orders = SchemaAssembler.Assemble(noDirectory).Find("sales", "Orders")!;

        Assert.Empty(orders.ForeignKeys);
    }
}
