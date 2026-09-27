# Evaluation — 2026-09-27 03:26

Model: `qwen2.5-coder:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts |
|---|---|---|---|---|---|---|---|
| V3 | **6/12 (50%)** | 4 | 1 | 1 | 0 | 2.1 s | 1.42 |
| V4 | **9/12 (75%)** | 2 | 1 | 0 | 0 | 2.0 s | 1.08 |

## By category

| Category | Questions | V3 | V4 |
|---|---|---|---|
| business-rule | 2 | 0/2 | 1/2 |
| ratios | 1 | 0/1 | 1/1 |
| dates | 3 | 3/3 | 3/3 |
| inventory | 2 | 2/2 | 2/2 |
| payments | 1 | 0/1 | 0/1 |
| aggregation | 1 | 0/1 | 0/1 |
| decline | 2 | 1/2 | 2/2 |

## Per question

| Question | V3 | V4 |
|---|---|---|
| `h-margin-2025` | ❌ wrong | ✅ |
| `h-return-rate-warehouse` | ❌ wrong | ✅ |
| `h-active-customers` | ⚠️ declined | ❌ wrong |
| `h-revenue-q2-2026` | ✅ | ✅ |
| `h-yoy-july` | ✅ | ✅ |
| `h-inventory-list-value` | ✅ | ✅ |
| `h-out-of-stock-products` | ✅ | ✅ |
| `h-amount-outstanding` | ❌ wrong | ⚠️ declined |
| `h-orders-ytd` | ✅ | ✅ |
| `h-fulfilment-hours` | ❌ wrong | ❌ wrong |
| `h-decline-advertising` | ✅ | ✅ |
| `h-decline-ratings` | ⚠️ failed | ✅ |

## Failures in detail

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
- **V3 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.WarehouseId, w.Name, 
         CAST(SUM(CASE WHEN o.Status = 'Returned' THEN 1 ELSE 0 END) AS float) / COUNT(o.OrderId) AS ReturnRate
  FROM inventory.Warehouses AS w
  JOIN sales.Orders AS o ON o.WarehouseId = w.WarehouseId
  GROUP BY w.WarehouseId, w.Name
  ```
- **V3 · `h-active-customers`** — WronglyDeclined: The TerminationDate column does not exist in the sales.Customers table
- **V4 · `h-active-customers`** — SilentlyWrong: no column matches gold column 1 (e.g. 555)
  ```sql
  SELECT COUNT(DISTINCT c.CustomerId) AS ActiveCustomers
  FROM sales.Customers AS c
  JOIN sales.Orders AS o ON o.CustomerId = c.CustomerId
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
    AND o.OrderDate >= DATEADD(month, -12, GETDATE())
  ```
- **V3 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(p.Amount) AS OutstandingAmount
  FROM sales.Payments AS p
  JOIN sales.Orders AS o ON o.OrderId = p.OrderId
  WHERE p.IsRefund = 0 AND o.Status NOT IN ('Cancelled', 'Returned')
  ```
- **V4 · `h-amount-outstanding`** — WronglyDeclined: The query requires the 'OrderTotal' column, which does not exist in the sales.Orders table
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
- **V3 · `h-decline-ratings`** — Failed: Failed instead of declining: The model did not return a SQL query.

## Scoring decisions

- `h-return-rate-warehouse`: Return rate = returned / shipped (Shipped, Delivered or Returned) orders, per the glossary.
- `h-active-customers`: Active = a non-cancelled order in the 12 months before today, per the glossary.
- `h-amount-outstanding`: Outstanding = unpaid, non-cancelled orders' line totals plus shipping, per the glossary.

_Held-out FoundryTrade questions, written on 2026-09-27 AFTER the V4 knowledge base was frozen and never used to tune prompts, glossary entries or aliases. They exercise business definitions the main set does not (margin, return rate, active customers, quarters, year-over-year, inventory at list price, stock-outs, amounts outstanding, year-to-date, fulfilment time). Gold SQL follows the glossary's definitions. Same scoring as questions.json._

🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).
