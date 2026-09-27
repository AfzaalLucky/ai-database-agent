# Evaluation — 2026-09-27 08:34

Model: `qwen2.5-coder:7b+qwen2.5:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts |
|---|---|---|---|---|---|---|---|
| V5 | **6/12 (50%)** | 3 | 1 | 2 | 0 | 21.0 s | 1.08 |

## By category

| Category | Questions | V5 |
|---|---|---|
| business-rule | 2 | 1/2 |
| ratios | 1 | 0/1 |
| dates | 3 | 2/3 |
| inventory | 2 | 1/2 |
| payments | 1 | 0/1 |
| aggregation | 1 | 0/1 |
| decline | 2 | 2/2 |

## Per question

| Question | V5 |
|---|---|
| `h-margin-2025` | ✅ |
| `h-return-rate-warehouse` | ⚠️ failed |
| `h-active-customers` | ⚠️ declined |
| `h-revenue-q2-2026` | ✅ |
| `h-yoy-july` | ✅ |
| `h-inventory-list-value` | ✅ |
| `h-out-of-stock-products` | ⚠️ failed |
| `h-amount-outstanding` | ❌ wrong |
| `h-orders-ytd` | ❌ wrong |
| `h-fulfilment-hours` | ❌ wrong |
| `h-decline-advertising` | ✅ |
| `h-decline-ratings` | ✅ |

## Failures in detail

- **V5 · `h-return-rate-warehouse`** — Failed: The request was canceled due to the configured HttpClient.Timeout of 180 seconds elapsing.
- **V5 · `h-active-customers`** — WronglyDeclined: Not tracked: marketing and advertising data such as customer acquisition cost or return on ad spend is not available in the database. Therefore, we cannot determine the number of active customers based on the provided information
- **V5 · `h-out-of-stock-products`** — Failed: Failed: The model did not return a SQL query.
- **V5 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(l.LineTotal + o.ShippingFee) AS OutstandingAmount 
  FROM sales.OrderLines l 
  LEFT JOIN sales.Orders o ON l.OrderId = o.OrderId 
  LEFT JOIN sales.Payments p ON o.OrderId = p.OrderId 
  WHERE p.OrderId IS NULL AND o.Status NOT IN ('Cancelled', 'Returned')
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
