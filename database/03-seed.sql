/*
  FoundryTrade sample database — 03: deterministic seed data.

  Every "random" choice comes from seed.R(n, salt): a SHA-256 hash of the row number and a salt,
  scaled to [0, 1). Re-running the scripts always produces exactly the same rows, so documented
  example questions keep the same answers. Data covers orders from 2023-01-01 to the data cut-off
  2026-08-31 (orders near the cut-off are still Pending/Shipped).

  All email addresses use reserved example domains (RFC 2606) — no real people or companies.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
USE FoundryTrade;
GO

CREATE SCHEMA seed AUTHORIZATION dbo;
GO

-- Deterministic pseudo-random number in [0, 1) for (row, salt).
CREATE FUNCTION seed.R (@n int, @salt int)
RETURNS float
WITH SCHEMABINDING
AS
BEGIN
    RETURN (CAST(CAST(HASHBYTES('SHA2_256', CONCAT(@n, ':', @salt)) AS binary(6)) AS bigint) % 1000000) / 1000000.0;
END;
GO

-- Pick an integer in [0, @count).
CREATE FUNCTION seed.Pick (@n int, @salt int, @count int)
RETURNS int
WITH SCHEMABINDING
AS
BEGIN
    RETURN CAST(FLOOR(seed.R(@n, @salt) * @count) AS int);
END;
GO

CREATE TABLE seed.Numbers (N int NOT NULL PRIMARY KEY);
INSERT seed.Numbers (N)
SELECT TOP (40000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
FROM sys.all_objects a CROSS JOIN sys.all_objects b;

CREATE TABLE seed.FirstNames (I int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
INSERT seed.FirstNames (I, Name)
SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v.Name
FROM (VALUES (N'Olivia'), (N'Liam'), (N'Amara'), (N'Noah'), (N'Sofia'), (N'Mateo'), (N'Chloe'), (N'Arjun'), (N'Isabella'), (N'Lucas'),
             (N'Mia'), (N'Ethan'), (N'Zara'), (N'Hiroshi'), (N'Emma'), (N'Omar'), (N'Freya'), (N'Daniel'), (N'Priya'), (N'Jonas'),
             (N'Grace'), (N'Samuel'), (N'Aisha'), (N'Leo'), (N'Hannah'), (N'Diego'), (N'Nora'), (N'Kwame'), (N'Elena'), (N'Ben'),
             (N'Yuki'), (N'Max'), (N'Fatima'), (N'Oscar'), (N'Lena'), (N'Ravi'), (N'Chiara'), (N'Tom'), (N'Ingrid'), (N'Kofi')) v (Name);

CREATE TABLE seed.LastNames (I int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
INSERT seed.LastNames (I, Name)
SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v.Name
FROM (VALUES (N'Smith'), (N'Okafor'), (N'Müller'), (N'Garcia'), (N'Nguyen'), (N'Patel'), (N'Johansson'), (N'Rossi'), (N'Kim'), (N'Brown'),
             (N'Dubois'), (N'Silva'), (N'Tanaka'), (N'Walker'), (N'Kowalski'), (N'Hassan'), (N'de Vries'), (N'Murphy'), (N'Chen'), (N'Andersen'),
             (N'Hartley'), (N'Moreau'), (N'Fischer'), (N'Singh'), (N'Lopez'), (N'Mensah'), (N'Novak'), (N'Clarke'), (N'Bianchi'), (N'Sato'),
             (N'Evans'), (N'Jansen'), (N'Costa'), (N'Wright'), (N'Schmidt'), (N'Reyes'), (N'Kaur'), (N'O''Brien'), (N'Lindqvist'), (N'Adeyemi'),
             (N'Baker'), (N'Park'), (N'Fernandes'), (N'Hughes'), (N'Weber'), (N'Ali'), (N'Martin'), (N'Byrne'), (N'Ito'), (N'Taylor')) v (Name);

CREATE TABLE seed.Places (I int NOT NULL PRIMARY KEY, City nvarchar(60) NOT NULL, Country nvarchar(60) NOT NULL, Weight int NOT NULL, WarehouseName nvarchar(60) NOT NULL);
INSERT seed.Places (I, City, Country, Weight, WarehouseName)
SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v.City, v.Country, v.Weight, v.Wh
FROM (VALUES (N'Chicago', N'United States', 9, N'Chicago Hub'), (N'Austin', N'United States', 7, N'Chicago Hub'), (N'Denver', N'United States', 5, N'Chicago Hub'),
             (N'Boston', N'United States', 6, N'Chicago Hub'), (N'Seattle', N'United States', 5, N'Chicago Hub'), (N'Toronto', N'Canada', 5, N'Chicago Hub'),
             (N'Vancouver', N'Canada', 3, N'Chicago Hub'), (N'Leeds', N'United Kingdom', 5, N'Leeds Depot'), (N'Manchester', N'United Kingdom', 6, N'Leeds Depot'),
             (N'London', N'United Kingdom', 8, N'Leeds Depot'), (N'Dublin', N'Ireland', 4, N'Leeds Depot'), (N'Rotterdam', N'Netherlands', 4, N'Rotterdam Central'),
             (N'Amsterdam', N'Netherlands', 5, N'Rotterdam Central'), (N'Berlin', N'Germany', 6, N'Rotterdam Central'), (N'Munich', N'Germany', 5, N'Rotterdam Central'),
             (N'Lyon', N'France', 4, N'Rotterdam Central'), (N'Paris', N'France', 5, N'Rotterdam Central'), (N'Madrid', N'Spain', 4, N'Rotterdam Central'),
             (N'Stockholm', N'Sweden', 3, N'Rotterdam Central'), (N'Singapore', N'Singapore', 4, N'Singapore East'), (N'Sydney', N'Australia', 5, N'Singapore East'),
             (N'Melbourne', N'Australia', 4, N'Singapore East'), (N'Auckland', N'New Zealand', 2, N'Singapore East')) v (City, Country, Weight, Wh);

-- Expand weights so a uniform pick over seed.PlaceSlots follows them.
CREATE TABLE seed.PlaceSlots (Slot int NOT NULL PRIMARY KEY, PlaceI int NOT NULL);
INSERT seed.PlaceSlots (Slot, PlaceI)
SELECT ROW_NUMBER() OVER (ORDER BY p.I, n.N) - 1, p.I
FROM seed.Places p JOIN seed.Numbers n ON n.N <= p.Weight;
GO

/* ------------------------------------------------------------------ hr */

INSERT hr.Departments (Name)
VALUES (N'Executive'), (N'Sales'), (N'Customer Success'), (N'Warehouse Operations'), (N'Purchasing'), (N'Finance'), (N'Marketing'), (N'Engineering');

-- (department, head title, staff title, staff count, head salary, staff salary min, staff salary max)
CREATE TABLE seed.Roles (DepartmentId int, HeadTitle nvarchar(80), StaffTitle nvarchar(80), StaffCount int, HeadSalary decimal(12, 2), StaffMin int, StaffMax int);
INSERT seed.Roles
VALUES (2, N'VP of Sales', N'Account Executive', 12, 168000, 62000, 98000),
       (3, N'Head of Customer Success', N'Customer Success Specialist', 5, 128000, 48000, 66000),
       (4, N'Director of Warehouse Operations', N'Warehouse Associate', 14, 132000, 36000, 52000),
       (5, N'Purchasing Manager', N'Buyer', 3, 118000, 54000, 72000),
       (6, N'Chief Financial Officer', N'Financial Analyst', 3, 205000, 64000, 92000),
       (7, N'Head of Marketing', N'Marketing Specialist', 3, 136000, 52000, 78000),
       (8, N'Head of Engineering', N'Software Engineer', 4, 172000, 88000, 142000);

INSERT hr.Employees (FirstName, LastName, Email, JobTitle, DepartmentId, ManagerId, HireDate, TerminationDate, Salary)
VALUES (N'Margaret', N'Holloway', N'margaret.holloway@foundrytrade.example', N'Chief Executive Officer', 1, NULL, '2014-03-03', NULL, 265000);

-- Department heads (EmployeeId 2-8, in department order) report to the CEO.
INSERT hr.Employees (FirstName, LastName, Email, JobTitle, DepartmentId, ManagerId, HireDate, TerminationDate, Salary)
SELECT f.Name, l.Name,
       LOWER(CONCAT(f.Name, '.', REPLACE(REPLACE(l.Name, ' ', ''), '''', ''), '@foundrytrade.example')),
       r.HeadTitle, r.DepartmentId, 1,
       DATEADD(day, seed.Pick(r.DepartmentId, 11, 1500), '2014-06-01'), NULL, r.HeadSalary
FROM seed.Roles r
JOIN seed.FirstNames f ON f.I = (r.DepartmentId * 7) % 40
JOIN seed.LastNames l ON l.I = (r.DepartmentId * 11) % 50
ORDER BY r.DepartmentId;

-- Staff report to their department head.
INSERT hr.Employees (FirstName, LastName, Email, JobTitle, DepartmentId, ManagerId, HireDate, TerminationDate, Salary)
SELECT f.Name, l.Name,
       LOWER(CONCAT(f.Name, '.', REPLACE(REPLACE(l.Name, ' ', ''), '''', ''), x.K, '@foundrytrade.example')),
       r.StaffTitle, r.DepartmentId, h.EmployeeId, x.Hire,
       CASE WHEN r.DepartmentId = 4 AND x.K IN (3, 7, 11) THEN DATEADD(day, 200 + seed.Pick(x.K, 14, 500), x.Hire) END,
       ROUND(r.StaffMin + seed.R(r.DepartmentId * 100 + x.K, 15) * (r.StaffMax - r.StaffMin), -2)
FROM seed.Roles r
JOIN hr.Employees h ON h.DepartmentId = r.DepartmentId AND h.ManagerId = 1
CROSS APPLY (
    SELECT n.N AS K,
           -- Sales reps all start before 2023 so every order date has reps available.
           CASE WHEN r.DepartmentId = 2 THEN DATEADD(day, seed.Pick(r.DepartmentId * 100 + n.N, 12, 2000), '2017-01-01')
                ELSE DATEADD(day, seed.Pick(r.DepartmentId * 100 + n.N, 12, 3300), '2016-01-01') END AS Hire
    FROM seed.Numbers n WHERE n.N <= r.StaffCount
) x
JOIN seed.FirstNames f ON f.I = seed.Pick(r.DepartmentId * 100 + x.K, 16, 40)
JOIN seed.LastNames l ON l.I = seed.Pick(r.DepartmentId * 100 + x.K, 17, 50)
ORDER BY r.DepartmentId, x.K;
GO

/* ------------------------------------------------------------------ catalog */

INSERT catalog.Categories (Name, ParentCategoryId)
VALUES (N'Power Tools', NULL), (N'Hand Tools', NULL), (N'Fasteners & Hardware', NULL), (N'Safety Equipment', NULL), (N'Workshop Storage', NULL);

-- (subcategory, parent, product nouns, min price, max price, cost ratio)
CREATE TABLE seed.Subcategories (I int PRIMARY KEY, Name nvarchar(60), ParentId int, Noun nvarchar(40), MinPrice int, MaxPrice int, CostRatio decimal(4, 2));
INSERT seed.Subcategories
VALUES (0, N'Drills & Drivers', 1, N'Drill Driver', 69, 329, 0.58), (1, N'Saws', 1, N'Circular Saw', 89, 499, 0.60), (2, N'Sanders & Grinders', 1, N'Angle Grinder', 49, 279, 0.57),
       (3, N'Wrenches & Sockets', 2, N'Socket Set', 19, 189, 0.45), (4, N'Hammers & Mallets', 2, N'Claw Hammer', 12, 69, 0.42), (5, N'Measuring & Layout', 2, N'Laser Measure', 9, 149, 0.48),
       (6, N'Screws & Bolts', 3, N'Wood Screw Pack', 4, 39, 0.35), (7, N'Anchors & Fixings', 3, N'Wall Anchor Kit', 6, 45, 0.36), (8, N'Hinges & Brackets', 3, N'Steel Bracket Set', 7, 59, 0.40),
       (9, N'Eye & Face Protection', 4, N'Safety Glasses', 8, 79, 0.44), (10, N'Hand Protection', 4, N'Work Gloves', 6, 49, 0.41), (11, N'Hearing Protection', 4, N'Ear Defenders', 11, 89, 0.46),
       (12, N'Tool Chests', 5, N'Rolling Tool Chest', 119, 899, 0.62), (13, N'Shelving', 5, N'Garage Shelving Unit', 59, 399, 0.60), (14, N'Organisers', 5, N'Parts Organiser', 12, 89, 0.43);

INSERT catalog.Categories (Name, ParentCategoryId)
SELECT Name, ParentId FROM seed.Subcategories ORDER BY I;

INSERT catalog.Suppliers (Name, Country, ContactEmail, LeadTimeDays)
SELECT v.Name, v.Country, LOWER(CONCAT('sales@', REPLACE(REPLACE(v.Name, ' ', ''), '&', ''), '.example')), v.Lead
FROM (VALUES (N'Ironclad Manufacturing', N'United States', 14), (N'Nordic Toolworks', N'Sweden', 21), (N'Kaizen Precision', N'Japan', 35),
             (N'Rhine Industrial', N'Germany', 18), (N'Pacific Fastener Co', N'Taiwan', 42), (N'Midland Hardware Supply', N'United Kingdom', 10),
             (N'Summit Safety Gear', N'Canada', 16), (N'Atlas Storage Systems', N'Poland', 25), (N'Delta Forge', N'India', 45),
             (N'Harbour Tools', N'Australia', 30), (N'Castellan Instruments', N'Italy', 22), (N'Bluestone Abrasives', N'Netherlands', 12)) v (Name, Country, Lead);

-- 10 products per subcategory = 150 products. Products 1-130 launched before 2023; 131-150 launched in 2024-2025.
CREATE TABLE seed.Variants (I int PRIMARY KEY, Word nvarchar(20));
INSERT seed.Variants VALUES (0, N'Pro'), (1, N'Compact'), (2, N'Heavy-Duty'), (3, N'Lite'), (4, N'Max'), (5, N'Precision'), (6, N'Classic'), (7, N'Industrial'), (8, N'Trade'), (9, N'Essential');

WITH p AS (
    SELECT ROW_NUMBER() OVER (ORDER BY v.I, s.I) AS Seq, s.I AS SubI, s.Name AS SubName, s.Noun, s.MinPrice, s.MaxPrice, s.CostRatio, v.Word
    FROM seed.Subcategories s CROSS JOIN seed.Variants v
)
INSERT catalog.Products (Sku, Name, CategoryId, SupplierId, ListPrice, UnitCost, IsDiscontinued, LaunchedOn)
SELECT CONCAT(UPPER(LEFT(REPLACE(p.SubName, ' ', ''), 2)), '-', RIGHT(CONCAT('000', p.Seq), 4)),
       CONCAT(p.Word, ' ', p.Noun, CASE WHEN p.SubI < 3 THEN CONCAT(' ', 12 + 6 * seed.Pick(p.Seq, 21, 3), 'V') ELSE '' END),
       c.CategoryId,
       1 + (p.SubI * 5 + seed.Pick(p.Seq, 22, 3)) % 12,
       x.Price,
       ROUND(x.Price * (p.CostRatio + seed.R(p.Seq, 24) * 0.08), 2),
       CASE WHEN p.Seq IN (7, 23, 41, 58, 77, 96, 112, 125) THEN 1 ELSE 0 END,
       CASE WHEN p.Seq <= 130 THEN DATEADD(day, seed.Pick(p.Seq, 25, 1400), '2019-01-01')
            ELSE DATEADD(day, seed.Pick(p.Seq, 25, 600), '2024-01-15') END
FROM p
JOIN catalog.Categories c ON c.Name = p.SubName
CROSS APPLY (SELECT CAST(FLOOR(p.MinPrice + seed.R(p.Seq, 23) * (p.MaxPrice - p.MinPrice)) + 0.99 AS decimal(10, 2)) AS Price) x
ORDER BY p.Seq;
GO

/* ------------------------------------------------------------------ inventory */

INSERT inventory.Warehouses (Name, City, Country)
VALUES (N'Chicago Hub', N'Chicago', N'United States'), (N'Leeds Depot', N'Leeds', N'United Kingdom'),
       (N'Rotterdam Central', N'Rotterdam', N'Netherlands'), (N'Singapore East', N'Singapore', N'Singapore');

INSERT inventory.Stock (WarehouseId, ProductId, QuantityOnHand, ReorderLevel)
SELECT w.WarehouseId, p.ProductId,
       CASE WHEN p.IsDiscontinued = 1 THEN seed.Pick(p.ProductId * 10 + w.WarehouseId, 31, 15)
            ELSE seed.Pick(p.ProductId * 10 + w.WarehouseId, 31, 420) END,
       20 + 10 * seed.Pick(p.ProductId * 10 + w.WarehouseId, 32, 7)
FROM inventory.Warehouses w CROSS JOIN catalog.Products p;
GO

/* ------------------------------------------------------------------ sales: customers */

-- 600 customers created evenly from 2022-06-01 to 2026-07-31 (CustomerId order = creation order),
-- so an order can only pick a customer that already existed on the order date.
DECLARE @CustomerCount int = 600;
DECLARE @CustStart datetime2(0) = '2022-06-01', @CustDays int = DATEDIFF(day, '2022-06-01', '2026-07-31');

INSERT sales.Customers (CompanyName, FirstName, LastName, Email, Segment, City, Country, CreatedAt)
SELECT CASE WHEN x.Segment = 'Consumer' THEN NULL
            ELSE CONCAT(l2.Name, ' ', CHOOSE(1 + seed.Pick(n.N, 45, 8), N'Fabrication', N'Builders', N'Joinery', N'Engineering', N'Construction', N'Workshop', N'Renovations', N'Metalworks'),
                        CASE WHEN x.Segment = 'Enterprise' THEN N' Group' ELSE N'' END) END,
       f.Name, l.Name,
       LOWER(CONCAT(f.Name, '.', REPLACE(REPLACE(l.Name, ' ', ''), '''', ''), n.N, '@', CHOOSE(1 + seed.Pick(n.N, 46, 3), 'example.com', 'example.net', 'example.org'))),
       x.Segment, pl.City, pl.Country,
       DATEADD(minute, seed.Pick(n.N, 47, 1440), DATEADD(day, (n.N - 1) * @CustDays / @CustomerCount, @CustStart))
FROM seed.Numbers n
CROSS APPLY (SELECT CASE WHEN seed.R(n.N, 41) < 0.62 THEN 'Consumer' WHEN seed.R(n.N, 41) < 0.91 THEN 'SmallBusiness' ELSE 'Enterprise' END AS Segment) x
JOIN seed.FirstNames f ON f.I = seed.Pick(n.N, 42, 40)
JOIN seed.LastNames l ON l.I = seed.Pick(n.N, 43, 50)
JOIN seed.LastNames l2 ON l2.I = seed.Pick(n.N, 44, 50)
JOIN seed.PlaceSlots ps ON ps.Slot = seed.Pick(n.N, 48, (SELECT COUNT(*) FROM seed.PlaceSlots))
JOIN seed.Places pl ON pl.I = ps.PlaceI
WHERE n.N <= @CustomerCount
ORDER BY n.N;
GO

/* ------------------------------------------------------------------ sales: orders */

DECLARE @OrderCount int = 6000;
DECLARE @Start datetime2(0) = '2023-01-01', @CutOff datetime2(0) = '2026-08-31T23:59:59';
DECLARE @Minutes int = DATEDIFF(minute, '2023-01-01', '2026-08-31T23:59:59');
DECLARE @CustStart datetime2(0) = '2022-06-01', @CustDays int = DATEDIFF(day, '2022-06-01', '2026-07-31');

-- Sales reps: employees in the Sales department (not the VP).
CREATE TABLE #Reps (I int PRIMARY KEY, EmployeeId int);
INSERT #Reps SELECT ROW_NUMBER() OVER (ORDER BY EmployeeId) - 1, EmployeeId FROM hr.Employees WHERE DepartmentId = 2 AND ManagerId <> 1;
DECLARE @RepCount int = (SELECT COUNT(*) FROM #Reps);

-- Order dates skew towards recent months (business growth): t = r^0.75.
WITH o AS (
    SELECT n.N,
           DATEADD(minute, CAST(POWER(seed.R(n.N, 51), 0.75) * @Minutes AS int), @Start) AS OrderDate
    FROM seed.Numbers n WHERE n.N <= @OrderCount
), oc AS (
    SELECT o.N, o.OrderDate,
           1 + seed.Pick(o.N, 52, IIF(DATEDIFF(day, @CustStart, o.OrderDate) * 600 / @CustDays > 600, 600, DATEDIFF(day, @CustStart, o.OrderDate) * 600 / @CustDays)) AS CustomerId
    FROM o
)
INSERT sales.Orders (CustomerId, SalesRepId, WarehouseId, OrderDate, Status, ShippedDate, ShipCity, ShipCountry, ShippingFee)
SELECT oc.CustomerId,
       CASE WHEN c.Segment <> 'Consumer' OR seed.R(oc.N, 53) < 0.12 THEN r.EmployeeId END,
       w.WarehouseId,
       oc.OrderDate,
       s.Status,
       CASE WHEN s.Status IN ('Shipped', 'Delivered', 'Returned') THEN DATEADD(hour, 18 + seed.Pick(oc.N, 55, 50), oc.OrderDate) END,
       c.City, c.Country,
       CASE c.Segment WHEN 'Consumer' THEN 6.95 WHEN 'SmallBusiness' THEN 12.50 ELSE 0 END
FROM oc
JOIN sales.Customers c ON c.CustomerId = oc.CustomerId
JOIN seed.Places pl ON pl.City = c.City
JOIN inventory.Warehouses w ON w.Name = pl.WarehouseName
JOIN #Reps r ON r.I = seed.Pick(oc.N, 54, @RepCount)
CROSS APPLY (
    SELECT CASE
        WHEN DATEDIFF(day, oc.OrderDate, @CutOff) < 3 THEN 'Pending'
        WHEN DATEDIFF(day, oc.OrderDate, @CutOff) < 10 THEN IIF(seed.R(oc.N, 56) < 0.06, 'Cancelled', 'Shipped')
        WHEN seed.R(oc.N, 56) < 0.05 THEN 'Cancelled'
        WHEN seed.R(oc.N, 56) < 0.08 THEN 'Returned'
        ELSE 'Delivered' END AS Status
) s
ORDER BY oc.OrderDate, oc.N;
GO

/* ------------------------------------------------------------------ sales: order lines */

-- Consumers buy 1-3 lines of 1-3 units; businesses 2-6 lines of larger quantities. Products are
-- spread with a stride of 37 (coprime with 150) so lines within an order are distinct; a product
-- not launched yet on the order date is swapped for an older one. Prices rise ~3% a year to today's
-- list price, with November promotions for consumers and negotiated discounts for businesses.
INSERT sales.OrderLines (OrderId, ProductId, Quantity, UnitPrice, DiscountPct)
SELECT o.OrderId, p.ProductId,
       CASE c.Segment WHEN 'Consumer' THEN 1 + seed.Pick(o.OrderId * 10 + k.N, 62, 3)
                      WHEN 'SmallBusiness' THEN 2 + seed.Pick(o.OrderId * 10 + k.N, 62, 19)
                      ELSE 5 + seed.Pick(o.OrderId * 10 + k.N, 62, 96) END,
       ROUND(p.ListPrice * (1 - 0.03 * DATEDIFF(day, o.OrderDate, '2026-08-31') / 365.0), 2),
       CASE c.Segment WHEN 'Consumer' THEN IIF(MONTH(o.OrderDate) = 11, 10, 0)
                      WHEN 'SmallBusiness' THEN 5 * seed.Pick(o.OrderId * 10 + k.N, 63, 2)
                      ELSE 10 + 5 * seed.Pick(o.OrderId * 10 + k.N, 63, 3) END
FROM sales.Orders o
JOIN sales.Customers c ON c.CustomerId = o.CustomerId
JOIN seed.Numbers k ON k.N <= CASE WHEN c.Segment = 'Consumer' THEN 1 + seed.Pick(o.OrderId, 61, 3) ELSE 2 + seed.Pick(o.OrderId, 61, 5) END
CROSS APPLY (SELECT 1 + (seed.Pick(o.OrderId, 64, 150) + 37 * (k.N - 1)) % 150 AS Candidate) cand
CROSS APPLY (
    SELECT TOP (1) pr.ProductId, pr.ListPrice
    FROM catalog.Products pr
    WHERE pr.ProductId = IIF((SELECT LaunchedOn FROM catalog.Products WHERE ProductId = cand.Candidate) <= CAST(o.OrderDate AS date),
                             cand.Candidate, 1 + (cand.Candidate - 1) % 130)
) p
ORDER BY o.OrderId, k.N;
GO

/* ------------------------------------------------------------------ sales: payments */

-- Card/PayPal settle within minutes, bank transfers in ~2 days, invoices in 14-45 days (so some
-- recent invoice orders are still unpaid at the cut-off). Cancelled orders have no payment;
-- returned orders are paid and then refunded.
WITH totals AS (
    SELECT o.OrderId, o.OrderDate, o.Status, o.ShippedDate, c.Segment,
           SUM(l.LineTotal) + o.ShippingFee AS Total
    FROM sales.Orders o
    JOIN sales.Customers c ON c.CustomerId = o.CustomerId
    JOIN sales.OrderLines l ON l.OrderId = o.OrderId
    WHERE o.Status <> 'Cancelled'
    GROUP BY o.OrderId, o.OrderDate, o.Status, o.ShippedDate, c.Segment, o.ShippingFee
), m AS (
    SELECT t.*,
           CASE WHEN t.Segment = 'Consumer' THEN IIF(seed.R(t.OrderId, 71) < 0.8, 'Card', 'PayPal')
                WHEN t.Segment = 'Enterprise' THEN 'Invoice'
                ELSE CHOOSE(1 + seed.Pick(t.OrderId, 71, 3), 'Card', 'BankTransfer', 'Invoice') END AS Method
    FROM totals t
), paid AS (
    SELECT m.*,
           CASE m.Method WHEN 'Invoice' THEN DATEADD(day, 14 + seed.Pick(m.OrderId, 72, 32), m.OrderDate)
                         WHEN 'BankTransfer' THEN DATEADD(hour, 30 + seed.Pick(m.OrderId, 72, 40), m.OrderDate)
                         ELSE DATEADD(minute, 1 + seed.Pick(m.OrderId, 72, 20), m.OrderDate) END AS PaidAt
    FROM m
)
INSERT sales.Payments (OrderId, PaidAt, Amount, Method, IsRefund)
SELECT OrderId, PaidAt, Total, Method, 0 FROM paid WHERE PaidAt <= '2026-08-31T23:59:59'
UNION ALL
SELECT OrderId, DATEADD(day, 14 + seed.Pick(OrderId, 73, 10), ShippedDate), -Total, Method, 1
FROM paid WHERE Status = 'Returned' AND PaidAt <= '2026-08-31T23:59:59'
ORDER BY 2, 1;
GO

/* ------------------------------------------------------------------ clean up */

DROP TABLE seed.Variants, seed.Subcategories, seed.Roles, seed.PlaceSlots, seed.Places, seed.LastNames, seed.FirstNames, seed.Numbers;
DROP FUNCTION seed.Pick;
DROP FUNCTION seed.R;
DROP SCHEMA seed;
GO
