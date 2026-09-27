# Evaluation — 2026-09-27 08:42

Model: `qwen2.5:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts |
|---|---|---|---|---|---|---|---|
| V1 | **3/12 (25%)** | 4 | 0 | 5 | 0 | 1.7 s | 1.00 |
| V2 | **5/12 (42%)** | 5 | 0 | 2 | 2 | 1.7 s | 1.00 |
| V3 | **8/12 (67%)** | 3 | 1 | 0 | 0 | 1.8 s | 1.42 |
| V4 | **9/12 (75%)** | 3 | 0 | 0 | 0 | 2.3 s | 1.08 |
| V5 | **5/12 (42%)** | 6 | 1 | 0 | 0 | 4.3 s | 1.00 |

## By category

| Category | Questions | V1 | V2 | V3 | V4 | V5 |
|---|---|---|---|---|---|---|
| business-rule | 2 | 0/2 | 0/2 | 1/2 | 1/2 | 1/2 |
| ratios | 1 | 0/1 | 0/1 | 0/1 | 1/1 | 0/1 |
| dates | 3 | 1/3 | 3/3 | 3/3 | 3/3 | 1/3 |
| inventory | 2 | 2/2 | 2/2 | 2/2 | 2/2 | 1/2 |
| payments | 1 | 0/1 | 0/1 | 0/1 | 0/1 | 0/1 |
| aggregation | 1 | 0/1 | 0/1 | 0/1 | 0/1 | 0/1 |
| decline | 2 | 0/2 | 0/2 | 2/2 | 2/2 | 2/2 |

## Per question

| Question | V1 | V2 | V3 | V4 | V5 |
|---|---|---|---|---|---|
| `h-margin-2025` | ⚠️ failed | ⚠️ failed | ✅ | ✅ | ✅ |
| `h-return-rate-warehouse` | ❌ wrong | ❌ wrong | ❌ wrong | ✅ | ❌ wrong |
| `h-active-customers` | ⚠️ failed | ⚠️ failed | ⚠️ declined | ❌ wrong | ⚠️ declined |
| `h-revenue-q2-2026` | ❌ wrong | ✅ | ✅ | ✅ | ❌ wrong |
| `h-yoy-july` | ❌ wrong | ✅ | ✅ | ✅ | ✅ |
| `h-inventory-list-value` | ✅ | ✅ | ✅ | ✅ | ❌ wrong |
| `h-out-of-stock-products` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `h-amount-outstanding` | ⚠️ failed | ❌ wrong | ❌ wrong | ❌ wrong | ❌ wrong |
| `h-orders-ytd` | ✅ | ✅ | ✅ | ✅ | ❌ wrong |
| `h-fulfilment-hours` | ❌ wrong | ❌ wrong | ❌ wrong | ❌ wrong | ❌ wrong |
| `h-decline-advertising` | ⚠️ failed | ❌ wrong 🛑 | ✅ | ✅ | ✅ |
| `h-decline-ratings` | ⚠️ failed | ❌ wrong 🛑 | ✅ | ✅ | ✅ |

## Failures in detail

- **V1 · `h-margin-2025`** — Failed: Failed: Invalid column name 'OrderLines'.
  ```sql
  SELECT 
      SUM(o.OrderLines.LineTotal * (1 - o.OrderLines.DiscountPct)) - SUM(o.OrderLines.LineTotal) AS GrossMargin,
      SUM(o.OrderLines.LineTotal) AS TotalSales,
      (SUM(o.OrderLines.LineTotal * (1 - o.OrderLines.DiscountPct)) - SUM(o.OrderLines.LineTotal)) / SUM(o.OrderLines.LineTotal) * 100 AS GrossMarginPercentage
  FROM 
      sales.Orders o
  JOIN 
      sales.OrderLines ol ON o.OrderId = ol.OrderId
  WHERE 
      YEAR(o.OrderDate) = 2025
  ```
- **V2 · `h-margin-2025`** — Failed: Failed: Invalid column name 'LineTotal'.
  ```sql
  SELECT 
      CAST(SUM(gross_margin) AS decimal(10, 2)) * 100.0 / SUM(total_revenue) AS GrossMarginPercentage
  FROM (
      SELECT 
          SUM(o.LineTotal) - SUM(p.UnitCost * ol.Quantity) AS gross_margin,
          SUM(o.LineTotal) AS total_revenue
      FROM 
          sales.Orders AS o
          JOIN sales.OrderLines AS ol ON ol.OrderId = o.OrderId
          JOIN catalog.Products AS p ON p.ProductId = ol.ProductId
      WHERE 
          YEAR(o.OrderDate) = 2025
          AND o.Status NOT IN ('Cancelled', 'Returned')
      GROUP BY 
          o.OrderId
  ) AS order_gMargins
  ```
- **V1 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT 
      w.Name AS WarehouseName,
      COUNT(CASE WHEN o.Status = 'Shipped' AND ol.OrderId IS NULL THEN 1 END) / COUNT(o.OrderId) AS ReturnRate
  FROM 
      inventory.Warehouses w
  LEFT JOIN 
      sales.Orders o ON w.WarehouseId = o.WarehouseId
  LEFT JOIN 
      sales.OrderLines ol ON o.OrderId = ol.OrderId
  GROUP BY 
      w.Name
  ```
- **V2 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.Name AS WarehouseName, 
         CAST(SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) AS decimal(5,2)) * 100.0 / COUNT(o.OrderId) AS ReturnRate
  FROM inventory.Warehouses AS w
  JOIN sales.Orders AS o ON o.WarehouseId = w.WarehouseId
  GROUP BY w.Name
  ```
- **V3 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.Name AS WarehouseName, 
         CAST(SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) AS decimal(5,2)) * 100.0 / COUNT(o.OrderId) AS ReturnRate
  FROM inventory.Warehouses AS w
  JOIN sales.Orders AS o ON o.WarehouseId = w.WarehouseId
  GROUP BY w.Name
  ```
- **V5 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 1 (e.g. 'Rotterdam Central')
  ```sql
  SELECT 
      WarehouseId, 
      COUNT(*) AS TotalOrders, 
      SUM(CASE WHEN Status = 'Returned' THEN 1 ELSE 0 END) AS ReturnedOrders, 
      COUNT(*) - SUM(CASE WHEN Status = 'Returned' THEN 1 ELSE 0 END) AS ShippedOrders, 
      100.0 * SUM(CASE WHEN Status = 'Returned' THEN 1 ELSE 0 END) / COUNT(*) AS ReturnRate 
  FROM 
      sales.Orders 
  GROUP BY 
      WarehouseId
  ```
- **V1 · `h-active-customers`** — Failed: Failed: Invalid column name 'TerminationDate'.
  ```sql
  SELECT COUNT(*) AS ActiveCustomersCount
  FROM sales.Customers
  WHERE TerminationDate IS NULL
  ```
- **V2 · `h-active-customers`** — Failed: Failed: Invalid column name 'TerminationDate'.
  ```sql
  SELECT COUNT(*) AS ActiveCustomers
  FROM sales.Customers
  WHERE TerminationDate IS NULL
  ```
- **V3 · `h-active-customers`** — WronglyDeclined: The column 'TerminationDate' does not exist on sales.Customers
- **V4 · `h-active-customers`** — SilentlyWrong: no column matches gold column 1 (e.g. 555)
  ```sql
  SELECT COUNT(DISTINCT c.CustomerId) AS ActiveCustomers
  FROM sales.Customers AS c
  JOIN sales.Orders AS o ON c.CustomerId = o.CustomerId
  WHERE c.Segment IN ('SmallBusiness', 'Enterprise')
    AND o.Status NOT IN ('Cancelled', 'Returned')
    AND o.OrderDate >= DATEADD(YEAR, -1, GETDATE())
  ```
- **V5 · `h-active-customers`** — WronglyDeclined: Not tracked: marketing and advertising
- **V1 · `h-revenue-q2-2026`** — SilentlyWrong: no column matches gold column 1 (e.g. 1827590.46)
  ```sql
  SELECT SUM(sl.UnitPrice * sl.Quantity * (1 - sl.DiscountPct)) AS Revenue
  FROM sales.OrderLines sl
  JOIN sales.Orders o ON sl.OrderId = o.OrderId
  WHERE o.OrderDate BETWEEN '2026-04-01' AND '2026-06-30'
  ```
- **V5 · `h-revenue-q2-2026`** — SilentlyWrong: no column matches gold column 1 (e.g. 1827590.46)
  ```sql
  SELECT SUM(CASE WHEN o.OrderDate BETWEEN '2026-04-01' AND '2026-06-30' THEN ol.LineTotal ELSE 0 END) AS Q2_2026_Revenue 
  FROM sales.Orders o 
  JOIN sales.OrderLines ol ON ol.OrderId = o.OrderId 
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V1 · `h-yoy-july`** — SilentlyWrong: no column matches gold column 1 (e.g. -19.069749)
  ```sql
  WITH July2025_Revenue AS (
      SELECT 
          SUM(ol.Quantity * ol.UnitPrice * (1 - ol.DiscountPct)) AS Revenue
      FROM 
          sales.OrderLines ol
      JOIN 
          sales.Orders o ON ol.OrderId = o.OrderId
      WHERE 
          YEAR(o.OrderDate) = 2025 AND MONTH(o.OrderDate) = 7
  ),
  July2026_Revenue AS (
      SELECT 
          SUM(ol.Quantity * ol.UnitPrice * (1 - ol.DiscountPct)) AS Revenue
      FROM 
          sales.OrderLines ol
      JOIN 
          sales.Orders o ON ol.OrderId = o.OrderId
      WHERE 
          YEAR(o.OrderDate) = 2026 AND MONTH(o.OrderDate) = 7
  )
  SELECT 
      ((july2026.Revenue - july2025.Revenue) * 100.0 / july2025.Revenue) AS RevenueChangePercentage
  FROM 
      July2025_Revenue july2025,
      July2026_Revenue july2026
  ```
- **V5 · `h-inventory-list-value`** — SilentlyWrong: no column matches gold column 1 (e.g. 16262837.3)
  ```sql
  SELECT SUM(QuantityOnHand * ListPrice) AS TotalStockValue 
  FROM inventory.Stock s 
  JOIN catalog.Products p ON s.ProductId = p.ProductId 
  WHERE p.IsDiscontinued = 0
  ```
- **V1 · `h-amount-outstanding`** — Failed: Failed: Invalid column name 'ShippingFee'.
  ```sql
  SELECT SUM(T1.Amount - T1.ShippingFee) AS OutstandingAmount
  FROM sales.Payments AS T1
  JOIN sales.Orders AS T2 ON T1.OrderId = T2.OrderId
  WHERE T2.Status <> 'Shipped'
  ```
- **V2 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(p.Amount) AS OutstandingAmount
  FROM sales.Payments AS p
  JOIN sales.Orders AS o ON o.OrderId = p.OrderId
  WHERE p.IsRefund = 0 AND p.Amount > 0
    AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V3 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(p.Amount) AS OutstandingAmount
  FROM sales.Payments AS p
  JOIN sales.Orders AS o ON o.OrderId = p.OrderId
  WHERE p.IsRefund = 0 AND p.Amount > 0 AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V4 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(l.LineTotal + o.ShippingFee) AS OutstandingAmount
  FROM sales.Orders AS o
  JOIN sales.OrderLines AS l ON l.OrderId = o.OrderId
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
    AND o.OrderId NOT IN (SELECT OrderId FROM sales.Payments WHERE IsRefund = 0)
  ```
- **V5 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(l.LineTotal + o.ShippingFee) AS OutstandingAmount 
  FROM sales.Orders o 
  LEFT JOIN sales.OrderLines l ON o.OrderId = l.OrderId 
  LEFT JOIN sales.Payments p ON o.OrderId = p.OrderId 
  WHERE p.OrderId IS NULL AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V5 · `h-orders-ytd`** — SilentlyWrong: no column matches gold column 1 (e.g. 1411)
  ```sql
  SELECT COUNT(*) AS OrdersCount 
  FROM sales.Orders 
  WHERE OrderDate >= DATEFROMPARTS(YEAR(GETDATE()), 1, 1) 
  AND OrderDate < DATEFROMPARTS(YEAR(GETDATE()) + 1, 1, 1) 
  AND Status NOT IN ('Cancelled', 'Returned')
  ```
- **V1 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(HOUR, o.OrderDate, o.ShippedDate)) AS AverageHoursBetweenOrderAndShip
  FROM sales.Orders o
  WHERE o.ShippedDate IS NOT NULL
  ```
- **V2 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT CAST(AVG(DATEDIFF(HOUR, o.OrderDate, o.ShippedDate)) AS decimal(10, 2)) AS AvgHoursToShip
  FROM sales.Orders AS o
  WHERE o.ShippedDate IS NOT NULL AND o.Status = 'Shipped'
  ```
- **V3 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT CAST(AVG(DATEDIFF(HOUR, o.OrderDate, o.ShippedDate)) AS decimal(10, 2)) AS AvgHoursToShip
  FROM sales.Orders AS o
  WHERE o.ShippedDate IS NOT NULL AND o.Status = 'Shipped'
  ```
- **V4 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(hour, o.OrderDate, o.ShippedDate)) AS AverageFulfilmentTime
  FROM sales.Orders AS o
  WHERE o.Status = 'Shipped' AND o.ShippedDate IS NOT NULL
  ```
- **V5 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(hour, o.OrderDate, COALESCE(o.ShippedDate, GETDATE()))) AS AvgOrderToShipTime 
  FROM sales.Orders o 
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V1 · `h-decline-advertising`** — Failed: Failed instead of declining: The model did not return a SQL query.
- **V2 · `h-decline-advertising`** — SilentlyWrong: answered a question it should have declined
  ```sql
  -- CANNOT_ANSWER: No table or column related to advertising expenses is provided in the schema.
  ```
- **V1 · `h-decline-ratings`** — Failed: Failed instead of declining: Invalid object name 'sales.CustomerFeedback'.
  ```sql
  SELECT p.ProductId, p.Name, p.ListPrice, p.UnitCost, p.IsDiscontinued, p.LaunchedOn, MAX(r.Rating) AS BestRating
  FROM catalog.Products p
  JOIN sales.OrderLines ol ON p.ProductId = ol.ProductId
  JOIN (
      SELECT ol.ProductId, AVG(c.Rating) AS Rating
      FROM sales.OrderLines ol
      JOIN sales.CustomerFeedback cf ON ol.OrderId = cf.OrderId
      GROUP BY ol.ProductId
  ) r ON p.ProductId = r.ProductId
  GROUP BY p.ProductId, p.Name, p.ListPrice, p.UnitCost, p.IsDiscontinued, p.LaunchedOn
  ORDER BY BestRating DESC
  OFFSET 0 ROWS FETCH NEXT 1 ROW ONLY
  ```
- **V2 · `h-decline-ratings`** — SilentlyWrong: answered a question it should have declined
  ```sql
  -- CANNOT_ANSWER: There is no column in the schema that defines customer ratings for products.
  ```

## Scoring decisions

- `h-return-rate-warehouse`: Return rate = returned / shipped (Shipped, Delivered or Returned) orders, per the glossary.
- `h-active-customers`: Active = a non-cancelled order in the 12 months before today, per the glossary.
- `h-amount-outstanding`: Outstanding = unpaid, non-cancelled orders' line totals plus shipping, per the glossary.

_Held-out FoundryTrade questions, written on 2026-09-27 AFTER the V4 knowledge base was frozen and never used to tune prompts, glossary entries or aliases. They exercise business definitions the main set does not (margin, return rate, active customers, quarters, year-over-year, inventory at list price, stock-outs, amounts outstanding, year-to-date, fulfilment time). Gold SQL follows the glossary's definitions. Same scoring as questions.json._

🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).
