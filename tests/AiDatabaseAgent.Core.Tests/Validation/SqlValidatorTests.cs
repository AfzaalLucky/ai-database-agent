using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Validation;

namespace AiDatabaseAgent.Core.Tests.Validation;

public class SqlValidatorTests
{
    private static SchemaColumn Col(string name) => new(name, "int", false, false, false, null, []);

    private static readonly DatabaseSchema Schema = new("FoundryTrade",
    [
        new SchemaTable("sales", "Customers", TableKind.Table, null, [Col("CustomerId"), Col("Country"), Col("Segment")], ["CustomerId"], []),
        new SchemaTable("sales", "Orders", TableKind.Table, null, [Col("OrderId"), Col("CustomerId"), Col("SalesRepId"), Col("OrderDate"), Col("Status")], ["OrderId"], []),
        new SchemaTable("sales", "OrderLines", TableKind.Table, null, [Col("OrderLineId"), Col("OrderId"), Col("ProductId"), Col("LineTotal")], ["OrderLineId"], []),
        new SchemaTable("catalog", "Products", TableKind.Table, null, [Col("ProductId"), Col("Name"), Col("ListPrice")], ["ProductId"], []),
        new SchemaTable("hr", "EmployeeDirectory", TableKind.View, null, [Col("EmployeeId"), Col("FirstName"), Col("DepartmentId")], ["EmployeeId"], []),
        new SchemaTable("hr", "Departments", TableKind.Table, null, [Col("DepartmentId"), Col("Name")], ["DepartmentId"], []),
    ]);

    private static ValidationResult V(string sql) => SqlValidator.Validate(sql, Schema);

    [Theory]
    [InlineData("SELECT COUNT(*) FROM sales.Orders")]
    [InlineData("SELECT TOP (5) c.Country, SUM(l.LineTotal) AS Revenue FROM sales.Orders AS o JOIN sales.Customers AS c ON c.CustomerId = o.CustomerId JOIN sales.OrderLines l ON l.OrderId = o.OrderId WHERE o.Status NOT IN ('Cancelled') GROUP BY c.Country ORDER BY Revenue DESC")]
    [InlineData("WITH monthly AS (SELECT DATEFROMPARTS(YEAR(o.OrderDate), MONTH(o.OrderDate), 1) AS Month, COUNT(*) AS Orders FROM sales.Orders o GROUP BY DATEFROMPARTS(YEAR(o.OrderDate), MONTH(o.OrderDate), 1)) SELECT m.Month, m.Orders FROM monthly m ORDER BY Month")]
    [InlineData("WITH x (Id, Total) AS (SELECT OrderId, SUM(LineTotal) FROM sales.OrderLines GROUP BY OrderId) SELECT Id, Total FROM x WHERE Total > 100")]
    [InlineData("SELECT t.CustomerId, t.Orders FROM (SELECT CustomerId, COUNT(*) AS Orders FROM sales.Orders GROUP BY CustomerId) AS t WHERE t.Orders > 10")]
    [InlineData("SELECT DATEPART(month, OrderDate) AS M, DATEDIFF(day, OrderDate, GETDATE()) AS Age FROM sales.Orders")]
    [InlineData("SELECT c.CustomerId FROM sales.Customers c WHERE EXISTS (SELECT 1 FROM sales.Orders o WHERE o.CustomerId = c.CustomerId)")]
    [InlineData("SELECT OrderId, ROW_NUMBER() OVER (PARTITION BY CustomerId ORDER BY OrderDate DESC) AS rn FROM sales.Orders")]
    [InlineData("SELECT Country FROM sales.Customers UNION SELECT Name FROM catalog.Products")]
    [InlineData("SELECT Orders.OrderId FROM sales.Orders")]
    [InlineData("SELECT OrderId FROM Orders")]
    [InlineData("SELECT o.OrderId FROM FoundryTrade.sales.Orders AS o")]
    [InlineData("SELECT o.OrderId FROM sales.Orders AS o WITH (NOLOCK)")]
    [InlineData("SELECT e.FirstName, d.Name FROM hr.EmployeeDirectory e JOIN hr.Departments d ON d.DepartmentId = e.DepartmentId")]
    [InlineData("SELECT 1 -- ; DROP TABLE sales.Orders")]
    [InlineData("SELECT OrderId FROM sales.Orders;")]
    public void Accepts_ordinary_read_queries(string sql)
    {
        var result = V(sql);
        Assert.True(result.IsValid, string.Join(" | ", result.Issues.Select(i => i.Message)));
    }

    [Theory]
    [InlineData("SELEC * FROM sales.Orders", IssueKind.Syntax)]
    [InlineData("SELECT 1; SELECT 2", IssueKind.NotReadOnly)]
    [InlineData("SELECT 1; DROP TABLE sales.Orders", IssueKind.NotReadOnly)]
    [InlineData("UPDATE sales.Orders SET Status = 'Pending'", IssueKind.NotReadOnly)]
    [InlineData("DELETE FROM sales.Orders", IssueKind.NotReadOnly)]
    [InlineData("INSERT sales.Customers (Country) VALUES ('x')", IssueKind.NotReadOnly)]
    [InlineData("EXEC sp_who", IssueKind.NotReadOnly)]
    [InlineData("WAITFOR DELAY '00:01:00'", IssueKind.NotReadOnly)]
    [InlineData("SELECT * INTO sales.Copy FROM sales.Orders", IssueKind.NotReadOnly)]
    [InlineData("SELECT name FROM master.sys.sql_logins", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM [otherdb].sales.Orders", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM remote.FoundryTrade.sales.Orders", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes;', 'SELECT 1')", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'SELECT 1')", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", IssueKind.Forbidden)]
    [InlineData("SELECT @@VERSION", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM sales.Orders WHERE OrderId = @id", IssueKind.Forbidden)]
    [InlineData("SELECT dbo.Secret(1)", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM dbo.fnSecrets()", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM #tmp", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM sales.Orders WITH (TABLOCKX)", IssueKind.Forbidden)]
    [InlineData("SELECT * FROM hr.Employees", IssueKind.UnknownTable)]
    [InlineData("SELECT * FROM sys.objects", IssueKind.UnknownTable)]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES", IssueKind.UnknownTable)]
    [InlineData("SELECT x.OrderId FROM sales.Orders o", IssueKind.UnknownTable)]
    [InlineData("SELECT AVG(e.Salary) FROM hr.EmployeeDirectory e", IssueKind.UnknownColumn)]
    [InlineData("SELECT Phone FROM sales.Customers", IssueKind.UnknownColumn)]
    [InlineData("SELECT t.Missing FROM (SELECT OrderId FROM sales.Orders) t", IssueKind.UnknownColumn)]
    public void Refuses(string sql, IssueKind kind)
    {
        var result = V(sql);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Kind == kind);
    }

    [Fact]
    public void Unknown_column_feedback_names_the_table_and_its_real_columns()
    {
        var issue = Assert.Single(V("SELECT d.Name, AVG(e.Salary) FROM hr.EmployeeDirectory e JOIN hr.Departments d ON d.DepartmentId = e.DepartmentId GROUP BY d.Name").Issues);

        Assert.Equal("Column 'Salary' does not exist on hr.EmployeeDirectory. Its columns are: EmployeeId, FirstName, DepartmentId.", issue.Message);
    }

    [Fact]
    public void Security_findings_are_flagged_as_such()
    {
        Assert.True(V("DELETE FROM sales.Orders").IsSecurityViolation);
        Assert.True(V("SELECT * FROM master.sys.sql_logins").IsSecurityViolation);
        Assert.False(V("SELECT Phone FROM sales.Customers").IsSecurityViolation);
    }
}
