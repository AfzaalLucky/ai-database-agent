# Evaluation — 2026-09-27 08:28

Model: `qwen2.5-coder:7b+qwen2.5:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts |
|---|---|---|---|---|---|---|---|
| V5 | **6/12 (50%)** | 5 | 1 | 0 | 0 | 7.3 s | 1.00 |

## By category

| Category | Questions | V5 |
|---|---|---|
| business-rule | 2 | 1/2 |
| ratios | 1 | 0/1 |
| dates | 3 | 1/3 |
| inventory | 2 | 2/2 |
| payments | 1 | 0/1 |
| aggregation | 1 | 0/1 |
| decline | 2 | 2/2 |

## Per question

| Question | V5 |
|---|---|
| `h-margin-2025` | ✅ |
| `h-return-rate-warehouse` | ❌ wrong |
| `h-active-customers` | ⚠️ declined |
| `h-revenue-q2-2026` | ✅ |
| `h-yoy-july` | ❌ wrong |
| `h-inventory-list-value` | ✅ |
| `h-out-of-stock-products` | ✅ |
| `h-amount-outstanding` | ❌ wrong |
| `h-orders-ytd` | ❌ wrong |
| `h-fulfilment-hours` | ❌ wrong |
| `h-decline-advertising` | ✅ |
| `h-decline-ratings` | ✅ |

## Failures in detail

- **V5 · `h-return-rate-warehouse`** — SilentlyWrong: no column matches gold column 2 (e.g. 2.649006623)
  ```sql
  SELECT w.Name, COUNT(o.OrderId) / COUNT(DISTINCT o.OrderId) AS ReturnRate 
  FROM sales.Orders o 
  JOIN inventory.Warehouses w ON w.WarehouseId = o.WarehouseId 
  WHERE o.Status = 'Returned' 
  GROUP BY w.Name
  ```
- **V5 · `h-active-customers`** — WronglyDeclined: Not tracked: marketing and advertising. There is no data on customer acquisition cost or return on ad spend
- **V5 · `h-yoy-july`** — SilentlyWrong: no column matches gold column 1 (e.g. -19.069749)
  ```sql
  SELECT 
      (583094.25 - 471899.64) * 100.0 / 583094.25 AS RevenueChangePct
  FROM 
      (SELECT 
          SUM(CASE WHEN o.OrderDate >= '2026-07-01' AND o.OrderDate < '2026-08-01' THEN ol.LineTotal ELSE 0 END) AS July2026Revenue, 
          SUM(CASE WHEN o.OrderDate >= '2025-07-01' AND o.OrderDate < '2025-08-01' THEN ol.LineTotal ELSE 0 END) AS July2025Revenue 
       FROM 
          sales.OrderLines ol 
       JOIN 
          sales.Orders o ON ol.OrderId = o.OrderId 
       WHERE 
          o.Status NOT IN ('Cancelled', 'Returned')) AS RevenueSummary
  ```
- **V5 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(o.ShippingFee + ol.LineTotal) AS OutstandingAmount 
  FROM sales.OrderLines ol 
  LEFT JOIN sales.Orders o ON ol.OrderId = o.OrderId 
  LEFT JOIN sales.Payments p ON o.OrderId = p.OrderId 
  WHERE o.Status NOT IN ('Cancelled', 'Returned') AND p.OrderId IS NULL
  ```
- **V5 · `h-orders-ytd`** — SilentlyWrong: no column matches gold column 1 (e.g. 1411)
  ```sql
  SELECT COUNT(*) AS OrderCount 
  FROM sales.Orders 
  WHERE OrderDate >= DATEFROMPARTS(YEAR(GETDATE()), 1, 1) 
  AND OrderDate < DATEFROMPARTS(YEAR(GETDATE()) + 1, 1, 1) 
  AND Status NOT IN ('Cancelled', 'Returned')
  ```
- **V5 · `h-fulfilment-hours`** — SilentlyWrong: no column matches gold column 1 (e.g. 42.95071542)
  ```sql
  SELECT AVG(DATEDIFF(hour, o.OrderDate, o.ShippedDate)) AS AvgOrderToShipTime 
  FROM sales.Orders o 
  WHERE o.ShippedDate IS NOT NULL AND o.Status NOT IN ('Cancelled', 'Returned')
  ```

## Scoring decisions

- `h-return-rate-warehouse`: Return rate = returned / shipped (Shipped, Delivered or Returned) orders, per the glossary.
- `h-active-customers`: Active = a non-cancelled order in the 12 months before today, per the glossary.
- `h-amount-outstanding`: Outstanding = unpaid, non-cancelled orders' line totals plus shipping, per the glossary.

_Held-out FoundryTrade questions, written on 2026-09-27 AFTER the V4 knowledge base was frozen and never used to tune prompts, glossary entries or aliases. They exercise business definitions the main set does not (margin, return rate, active customers, quarters, year-over-year, inventory at list price, stock-outs, amounts outstanding, year-to-date, fulfilment time). Gold SQL follows the glossary's definitions. Same scoring as questions.json._

🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).
