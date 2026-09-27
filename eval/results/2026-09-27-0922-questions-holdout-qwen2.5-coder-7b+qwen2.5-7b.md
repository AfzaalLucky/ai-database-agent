# Evaluation — 2026-09-27 09:22

Model: `qwen2.5-coder:7b+qwen2.5:7b` · 12 questions (2 should be declined) · scored by execution accuracy against gold SQL.

| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts | Avg tool calls |
|---|---|---|---|---|---|---|---|---|
| V5 | **9/12 (75%)** | 3 | 0 | 0 | 0 | 3.5 s | 2.25 | 0.92 |

## By category

| Category | Questions | V5 |
|---|---|---|
| business-rule | 2 | 1/2 |
| ratios | 1 | 1/1 |
| dates | 3 | 3/3 |
| inventory | 2 | 2/2 |
| payments | 1 | 0/1 |
| aggregation | 1 | 0/1 |
| decline | 2 | 2/2 |

## Per question

| Question | V5 |
|---|---|
| `h-margin-2025` | ✅ |
| `h-return-rate-warehouse` | ✅ |
| `h-active-customers` | ❌ wrong |
| `h-revenue-q2-2026` | ✅ |
| `h-yoy-july` | ✅ |
| `h-inventory-list-value` | ✅ |
| `h-out-of-stock-products` | ✅ |
| `h-amount-outstanding` | ❌ wrong |
| `h-orders-ytd` | ✅ |
| `h-fulfilment-hours` | ❌ wrong |
| `h-decline-advertising` | ✅ |
| `h-decline-ratings` | ✅ |

## Failures in detail

- **V5 · `h-active-customers`** — SilentlyWrong: expected 1 row(s), got 500
  ```sql
  SELECT COUNT(*) AS ActiveCustomers
  FROM sales.Customers AS c
  WHERE EXISTS (
      SELECT 1
      FROM sales.Orders AS o
      WHERE o.CustomerId = c.CustomerId
        AND o.Status NOT IN ('Cancelled', 'Returned')
        AND o.OrderDate >= DATEADD(YEAR, -1, GETDATE())
  )
  GROUP BY c.CustomerId
  ```
- **V5 · `h-amount-outstanding`** — SilentlyWrong: no column matches gold column 1 (e.g. 439664.38)
  ```sql
  SELECT SUM(l.LineTotal + o.ShippingFee) AS OutstandingAmount
  FROM sales.OrderLines AS l
  JOIN sales.Orders AS o ON l.OrderId = o.OrderId
  WHERE o.Status NOT IN ('Cancelled', 'Returned')
    AND l.OrderId NOT IN (SELECT OrderId FROM sales.Payments WHERE IsRefund = 0)
  ```
- **V5 · `h-fulfilment-hours`** — SilentlyWrong: expected 1 row(s), got 5
  ```sql
  SELECT TOP (5) p.Sku, p.Name, SUM(s.QuantityOnHand) AS UnitsOnHand
  FROM catalog.Products AS p
  JOIN inventory.Stock AS s ON s.ProductId = p.ProductId
  WHERE p.IsDiscontinued = 0
  GROUP BY p.Sku, p.Name
  ORDER BY UnitsOnHand ASC
  ```

## Scoring decisions

- `h-return-rate-warehouse`: Return rate = returned / shipped (Shipped, Delivered or Returned) orders, per the glossary.
- `h-active-customers`: Active = a non-cancelled order in the 12 months before today, per the glossary.
- `h-amount-outstanding`: Outstanding = unpaid, non-cancelled orders' line totals plus shipping, per the glossary.

_Held-out FoundryTrade questions, written on 2026-09-27 AFTER the V4 knowledge base was frozen and never used to tune prompts, glossary entries or aliases. They exercise business definitions the main set does not (margin, return rate, active customers, quarters, year-over-year, inventory at list price, stock-outs, amounts outstanding, year-to-date, fulfilment time). Gold SQL follows the glossary's definitions. Same scoring as questions.json._

🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).
