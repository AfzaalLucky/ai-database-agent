/* FoundryTrade — 05: read-only sanity checks (row counts, integrity = 0, determinism fingerprint, agent_reader boundary). Safe to run any time. */
SET NOCOUNT ON; USE FoundryTrade;
SELECT 'rows' k, (SELECT COUNT(*) FROM hr.Employees) emp, (SELECT COUNT(*) FROM catalog.Products) prod, (SELECT COUNT(*) FROM sales.Customers) cust, (SELECT COUNT(*) FROM sales.Orders) ord, (SELECT COUNT(*) FROM sales.OrderLines) lines, (SELECT COUNT(*) FROM sales.Payments) pay, (SELECT COUNT(*) FROM inventory.Stock) stock;
SELECT Status, COUNT(*) n FROM sales.Orders GROUP BY Status ORDER BY n DESC;
SELECT YEAR(OrderDate) y, COUNT(*) orders, CAST(SUM(l.LineTotal) AS decimal(14,2)) revenue FROM sales.Orders o JOIN sales.OrderLines l ON l.OrderId=o.OrderId WHERE o.Status NOT IN ('Cancelled','Returned') GROUP BY YEAR(OrderDate) ORDER BY y;
SELECT Segment, COUNT(*) FROM sales.Customers GROUP BY Segment;
-- integrity checks (all should be 0)
SELECT 'orders before customer' chk, COUNT(*) FROM sales.Orders o JOIN sales.Customers c ON c.CustomerId=o.CustomerId WHERE o.OrderDate < c.CreatedAt
UNION ALL SELECT 'lines before launch', COUNT(*) FROM sales.OrderLines l JOIN sales.Orders o ON o.OrderId=l.OrderId JOIN catalog.Products p ON p.ProductId=l.ProductId WHERE p.LaunchedOn > CAST(o.OrderDate AS date)
UNION ALL SELECT 'shipped after cutoff', COUNT(*) FROM sales.Orders WHERE ShippedDate > '2026-08-31T23:59:59'
UNION ALL SELECT 'orders without lines', COUNT(*) FROM sales.Orders o WHERE NOT EXISTS (SELECT 1 FROM sales.OrderLines l WHERE l.OrderId=o.OrderId)
UNION ALL SELECT 'dup product in order', COUNT(*) FROM (SELECT OrderId, ProductId FROM sales.OrderLines GROUP BY OrderId, ProductId HAVING COUNT(*)>1) d
UNION ALL SELECT 'rep hired after order', COUNT(*) FROM sales.Orders o JOIN hr.Employees e ON e.EmployeeId=o.SalesRepId WHERE e.HireDate > CAST(o.OrderDate AS date)
UNION ALL SELECT 'cols without description', COUNT(*) FROM sys.tables t WHERE NOT EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.major_id=t.object_id AND ep.minor_id=0 AND ep.name='MS_Description');
SELECT 'fingerprint', CHECKSUM_AGG(CHECKSUM(OrderId, CustomerId, OrderDate, Status)) FROM sales.Orders;
SELECT 'lines fp', CHECKSUM_AGG(CHECKSUM(OrderLineId, ProductId, Quantity, UnitPrice, DiscountPct)) FROM sales.OrderLines;
-- security boundary
DECLARE @c varbinary(8000);
EXECUTE AS USER = 'agent_reader' WITH COOKIE INTO @c;
SELECT 'as agent', USER_NAME() u, (SELECT COUNT(*) FROM sales.Orders) visible_orders, (SELECT COUNT(*) FROM hr.EmployeeDirectory) emp_rows;
BEGIN TRY SELECT TOP 1 Salary FROM hr.Employees; END TRY BEGIN CATCH SELECT 'salary blocked', ERROR_NUMBER(), LEFT(ERROR_MESSAGE(),90); END CATCH;
BEGIN TRY SELECT COUNT(*) FROM hr.Employees; END TRY BEGIN CATCH SELECT 'base table blocked', ERROR_NUMBER(); END CATCH;
BEGIN TRY UPDATE sales.Orders SET Status='Pending' WHERE OrderId=1; END TRY BEGIN CATCH SELECT 'update blocked', ERROR_NUMBER(), LEFT(ERROR_MESSAGE(),80); END CATCH;
BEGIN TRY DELETE FROM sales.Payments WHERE 1=0; END TRY BEGIN CATCH SELECT 'delete blocked', ERROR_NUMBER(); END CATCH;
BEGIN TRY EXEC('CREATE TABLE sales.X(i int)'); END TRY BEGIN CATCH SELECT 'ddl blocked', ERROR_NUMBER(); END CATCH;
BEGIN TRY REVERT; END TRY BEGIN CATCH SELECT 'plain revert blocked', ERROR_NUMBER(); END CATCH;
BEGIN TRY SELECT TOP 1 name FROM master.sys.sql_logins; END TRY BEGIN CATCH SELECT 'master', ERROR_NUMBER(); END CATCH;
REVERT WITH COOKIE = @c;
SELECT 'after revert', USER_NAME();
