# Evaluation — 2026-09-27 03:24

Model: `qwen2.5-coder:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts |
|---|---|---|---|---|---|---|---|
| V1 | **4/12 (33%)** | 6 | 0 | 2 | 0 | 1.5 s | 1.00 |
| V2 | **6/12 (50%)** | 3 | 0 | 3 | 0 | 1.6 s | 1.00 |
| V3 | **6/12 (50%)** | 4 | 1 | 1 | 0 | 1.7 s | 1.42 |
| V4 | **9/12 (75%)** | 2 | 1 | 0 | 0 | 2.0 s | 1.08 |

## By category

| Category | Questions | V1 | V2 | V3 | V4 |
|---|---|---|---|---|---|
| business-rule | 2 | 1/2 | 0/2 | 0/2 | 1/2 |
| ratios | 1 | 0/1 | 0/1 | 0/1 | 1/1 |
| dates | 3 | 1/3 | 3/3 | 3/3 | 3/3 |
| inventory | 2 | 2/2 | 2/2 | 2/2 | 2/2 |
| payments | 1 | 0/1 | 0/1 | 0/1 | 0/1 |
| aggregation | 1 | 0/1 | 0/1 | 0/1 | 0/1 |
| decline | 2 | 0/2 | 1/2 | 1/2 | 2/2 |

## Per question

| Question | V1 | V2 | V3 | V4 |
|---|---|---|---|---|
| `h-margin-2025` | ✅ | ⚠️ failed | ❌ wrong | ✅ |
| `h-return-rate-warehouse` | ❌ wrong | ❌ wrong | ❌ wrong | ✅ |
| `h-active-customers` | ⚠️ failed | ⚠️ failed | ⚠️ declined | ❌ wrong |
| `h-revenue-q2-2026` | ❌ wrong | ✅ | ✅ | ✅ |
| `h-yoy-july` | ❌ wrong | ✅ | ✅ | ✅ |
| `h-inventory-list-value` | ✅ | ✅ | ✅ | ✅ |
| `h-out-of-stock-products` | ✅ | ✅ | ✅ | ✅ |
| `h-amount-outstanding` | ❌ wrong | ❌ wrong | ❌ wrong | ⚠️ declined |
| `h-orders-ytd` | ✅ | ✅ | ✅ | ✅ |
| `h-fulfilment-hours` | ❌ wrong | ❌ wrong | ❌ wrong | ❌ wrong |
| `h-decline-advertising` | ❌ wrong | ✅ | ✅ | ✅ |
| `h-decline-ratings` | ⚠️ failed | ⚠️ failed | ⚠️ failed | ✅ |

## Failures in detail

- **V2 · `h-margin-2025`** — Failed: Failed: Invalid column name 'OrderDate'.
  ```sql
  SELECT 
    CAST(SUM((ol.UnitPrice - p.UnitCost) * ol.Quantity) AS decimal(14, 2)) * 100.0 / 
    SUM(ol.UnitPrice * ol.Quantity) AS GrossMarginPct
  FROM sales.OrderLines AS ol
  JOIN catalog.Products AS p ON p.ProductId = ol.ProductId
  WHERE ol.OrderDate >= '2025-01-01' AND ol.OrderDate < '2026-01-01'
  ```
- **V3 · `h-margin-2025`** — SilentlyWrong: no column matches gold column 1 (e.g. 31.552043)
  ```sql
  SELECT 
    CAST(SUM((ol.UnitPrice - p.UnitCost) * ol.Quantity) AS decimal(14, 2)) * 100.0 / 
    SUM(ol.UnitPrice * ol.Quantity) AS GrossMarginPct
  FROM sales.OrderLines AS ol
  JOIN catalog.Products AS p ON p.ProductId = ol.ProductId
  JOIN sales.Orders AS o ON o.OrderId = ol.OrderId
  WHERE o.OrderDate >= '2025-01-01' AND o.OrderDate < '2026-01-01'
  ```
- **V1 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT 
      w.Name AS WarehouseName,
      SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) * 1.0 / COUNT(o.OrderId) AS ReturnRate
  FROM 
      inventory.Warehouses w
  JOIN 
      sales.Orders o ON w.WarehouseId = o.WarehouseId
  GROUP BY 
      w.Name
  ```
- **V2 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.WarehouseId, w.Name, 
         CAST(SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) AS float) / COUNT(o.OrderId) AS ReturnRate
  FROM inventory.Warehouses AS w
  JOIN sales.Orders AS o ON o.WarehouseId = w.WarehouseId
  GROUP BY w.WarehouseId, w.Name
  ```
- **V3 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.WarehouseId, w.Name, 
         CAST(SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) AS float) / COUNT(o.OrderId) AS ReturnRate
  FROM inventory.Warehouses AS w
  JOIN sales.Orders AS o ON o.WarehouseId = w.WarehouseId
  GROUP BY w.WarehouseId, w.Name
  ```
- **V1 · `h-active-customers`** — Failed: Failed: Invalid column name 'TerminationDate'.
  ```sql
  SELECT COUNT(CustomerId) AS ActiveCustomerCount
  FROM sales.Customers
  WHERE TerminationDate IS NULL
  ```
- **V2 · `h-active-customers`** — Failed: Failed: Invalid column name 'TerminationDate'.
  ```sql
  SELECT COUNT(*) AS ActiveCustomers
  FROM sales.Customers
  WHERE TerminationDate IS NULL
  ```
- **V3 · `h-active-customers`** — WronglyDeclined: The 'TerminationDate' column does not exist in the 'sales.Customers' table
- **V4 · `h-active-customers`** — SilentlyWrong: no column matches gold column 1 (e.g. 555)
  ```sql
  SELECT COUNT(DISTINCT c.CustomerId) AS ActiveCustomers
  FROM sales.Customers AS c
  JOIN sales.Orders AS o ON o.CustomerId = c.CustomerId
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
    AND o.OrderDate >= DATEADD(month, -12, GETDATE())
  ```
- **V1 · `h-revenue-q2-2026`** — SilentlyWrong: no column matches gold column 1 (e.g. 1827590.46)
  ```sql
  SELECT SUM(sales.OrderLines.UnitPrice * sales.OrderLines.Quantity * (1 - sales.OrderLines.DiscountPct)) AS Revenue
  FROM sales.OrderLines
  JOIN sales.Orders ON sales.OrderLines.OrderId = sales.Orders.OrderId
  WHERE sales.Orders.OrderDate BETWEEN '2026-04-01' AND '2026-06-30'
  ```
- **V1 · `h-yoy-july`** — SilentlyWrong: no column matches gold column 1 (e.g. -19.069749)
  ```sql
  SELECT 
      ((SUM(CASE WHEN YEAR(sales.Orders.OrderDate) = 2026 AND MONTH(sales.Orders.OrderDate) = 7 THEN sales.OrderLines.LineTotal ELSE 0 END) -
        SUM(CASE WHEN YEAR(sales.Orders.OrderDate) = 2025 AND MONTH(sales.Orders.OrderDate) = 7 THEN sales.OrderLines.LineTotal ELSE 0 END)) /
       SUM(CASE WHEN YEAR(sales.Orders.OrderDate) = 2025 AND MONTH(sales.Orders.OrderDate) = 7 THEN sales.OrderLines.LineTotal ELSE 0 END)) * 100 AS RevenueChangePercentage
  FROM 
      sales.OrderLines
  JOIN 
      sales.Orders ON sales.OrderLines.OrderId = sales.Orders.OrderId
  ```
- **V1 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(Amount) AS OutstandingAmount
  FROM sales.Payments
  WHERE OrderId IN (
      SELECT OrderId
      FROM sales.Orders
      WHERE Status != 'Paid'
  )
  ```
- **V2 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(p.Amount) AS OutstandingAmount
  FROM sales.Payments AS p
  JOIN sales.Orders AS o ON o.OrderId = p.OrderId
  WHERE p.IsRefund = 0 AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V3 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(p.Amount) AS OutstandingAmount
  FROM sales.Payments AS p
  JOIN sales.Orders AS o ON o.OrderId = p.OrderId
  WHERE p.IsRefund = 0 AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V4 · `h-amount-outstanding`** — WronglyDeclined: The query requires the 'OrderTotal' column, which does not exist in the sales.Orders table
- **V1 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(HOUR, orders.OrderDate, orders.ShippedDate)) AS AverageHoursBetweenOrderAndShip
  FROM sales.Orders orders
  ```
- **V2 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(day, o.OrderDate, o.ShippedDate)) AS AverageDaysToShip
  FROM sales.Orders AS o
  WHERE o.ShippedDate IS NOT NULL
  ```
- **V3 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(day, o.OrderDate, o.ShippedDate)) AS AverageDaysToShip
  FROM sales.Orders AS o
  WHERE o.ShippedDate IS NOT NULL
  ```
- **V4 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(hour, o.OrderDate, o.ShippedDate)) AS AverageShippingTime
  FROM sales.Orders AS o
  WHERE o.ShippedDate IS NOT NULL
  ```
- **V1 · `h-decline-advertising`** — SilentlyWrong: answered a question it should have declined
  ```sql
  SELECT SUM(Amount) AS TotalAdvertisingSpending
  FROM sales.Payments
  WHERE Method = 'Advertising' AND YEAR(PaidAt) = 2025
  ```
- **V1 · `h-decline-ratings`** — Failed: Failed instead of declining: Incorrect syntax near 'LIMIT'.
  ```sql
  SELECT p.ProductId, p.Name, AVG(r.Rating) AS AverageRating
  FROM catalog.Products p
  JOIN sales.OrderLines ol ON p.ProductId = ol.ProductId
  JOIN sales.Orders o ON ol.OrderId = o.OrderId
  JOIN sales.Payments pa ON o.OrderId = pa.OrderId
  LEFT JOIN sales.Reviews r ON p.ProductId = r.ProductId
  GROUP BY p.ProductId, p.Name
  ORDER BY AverageRating DESC
  LIMIT 1
  ```
- **V2 · `h-decline-ratings`** — Failed: Failed instead of declining: The model did not return a SQL query.
- **V3 · `h-decline-ratings`** — Failed: Failed instead of declining: The model did not return a SQL query.

## Scoring decisions

- `h-return-rate-warehouse`: Return rate = returned / shipped (Shipped, Delivered or Returned) orders, per the glossary.
- `h-active-customers`: Active = a non-cancelled order in the 12 months before today, per the glossary.
- `h-amount-outstanding`: Outstanding = unpaid, non-cancelled orders' line totals plus shipping, per the glossary.

_Held-out FoundryTrade questions, written on 2026-09-27 AFTER the V4 knowledge base was frozen and never used to tune prompts, glossary entries or aliases. They exercise business definitions the main set does not (margin, return rate, active customers, quarters, year-over-year, inventory at list price, stock-outs, amounts outstanding, year-to-date, fulfilment time). Gold SQL follows the glossary's definitions. Same scoring as questions.json._

🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).
