/*
  FoundryTrade sample database — 02: schemas, tables, constraints and descriptions.

  FoundryTrade is a fictional distributor of workshop tools and hardware that sells to consumers,
  small businesses and enterprises from four warehouses. The model is deliberately realistic enough
  to exercise Text-to-SQL: several schemas, one-to-many and self-referencing relationships, status
  enums, money and date columns, and one sensitive column (hr.Employees.Salary).

  Every table and column has an MS_Description extended property; the agent reads these to give the
  model business meaning, not just names (V2, schema-aware).
*/
SET NOCOUNT ON;
USE FoundryTrade;
GO

CREATE SCHEMA catalog AUTHORIZATION dbo;
GO
CREATE SCHEMA sales AUTHORIZATION dbo;
GO
CREATE SCHEMA inventory AUTHORIZATION dbo;
GO
CREATE SCHEMA hr AUTHORIZATION dbo;
GO

/* ------------------------------------------------------------------ hr */

CREATE TABLE hr.Departments (
    DepartmentId   int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Departments PRIMARY KEY,
    Name           nvarchar(60)  NOT NULL CONSTRAINT UQ_Departments_Name UNIQUE
);

CREATE TABLE hr.Employees (
    EmployeeId     int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    FirstName      nvarchar(50)  NOT NULL,
    LastName       nvarchar(50)  NOT NULL,
    Email          nvarchar(120) NOT NULL CONSTRAINT UQ_Employees_Email UNIQUE,
    JobTitle       nvarchar(80)  NOT NULL,
    DepartmentId   int           NOT NULL CONSTRAINT FK_Employees_Departments REFERENCES hr.Departments (DepartmentId),
    ManagerId      int           NULL     CONSTRAINT FK_Employees_Manager REFERENCES hr.Employees (EmployeeId),
    HireDate       date          NOT NULL,
    TerminationDate date         NULL,
    Salary         decimal(12, 2) NOT NULL CONSTRAINT CK_Employees_Salary CHECK (Salary > 0),
    CONSTRAINT CK_Employees_Dates CHECK (TerminationDate IS NULL OR TerminationDate >= HireDate)
);

/* ------------------------------------------------------------------ catalog */

CREATE TABLE catalog.Categories (
    CategoryId       int          IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
    Name             nvarchar(60) NOT NULL CONSTRAINT UQ_Categories_Name UNIQUE,
    ParentCategoryId int          NULL CONSTRAINT FK_Categories_Parent REFERENCES catalog.Categories (CategoryId)
);

CREATE TABLE catalog.Suppliers (
    SupplierId     int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
    Name           nvarchar(100) NOT NULL CONSTRAINT UQ_Suppliers_Name UNIQUE,
    Country        nvarchar(60)  NOT NULL,
    ContactEmail   nvarchar(120) NOT NULL,
    LeadTimeDays   smallint      NOT NULL CONSTRAINT CK_Suppliers_LeadTime CHECK (LeadTimeDays BETWEEN 1 AND 120)
);

CREATE TABLE catalog.Products (
    ProductId      int            IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Sku            varchar(20)    NOT NULL CONSTRAINT UQ_Products_Sku UNIQUE,
    Name           nvarchar(120)  NOT NULL,
    CategoryId     int            NOT NULL CONSTRAINT FK_Products_Categories REFERENCES catalog.Categories (CategoryId),
    SupplierId     int            NOT NULL CONSTRAINT FK_Products_Suppliers REFERENCES catalog.Suppliers (SupplierId),
    ListPrice      decimal(10, 2) NOT NULL CONSTRAINT CK_Products_ListPrice CHECK (ListPrice > 0),
    UnitCost       decimal(10, 2) NOT NULL CONSTRAINT CK_Products_UnitCost CHECK (UnitCost > 0),
    IsDiscontinued bit            NOT NULL CONSTRAINT DF_Products_IsDiscontinued DEFAULT (0),
    LaunchedOn     date           NOT NULL
);
CREATE INDEX IX_Products_CategoryId ON catalog.Products (CategoryId);
CREATE INDEX IX_Products_SupplierId ON catalog.Products (SupplierId);

/* ------------------------------------------------------------------ sales */

CREATE TABLE sales.Customers (
    CustomerId     int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    CompanyName    nvarchar(100) NULL,
    FirstName      nvarchar(50)  NOT NULL,
    LastName       nvarchar(50)  NOT NULL,
    Email          nvarchar(120) NOT NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    Segment        varchar(13)   NOT NULL CONSTRAINT CK_Customers_Segment CHECK (Segment IN ('Consumer', 'SmallBusiness', 'Enterprise')),
    City           nvarchar(60)  NOT NULL,
    Country        nvarchar(60)  NOT NULL,
    CreatedAt      datetime2(0)  NOT NULL
);
CREATE INDEX IX_Customers_Country ON sales.Customers (Country);

CREATE TABLE sales.Orders (
    OrderId        int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId     int           NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES sales.Customers (CustomerId),
    SalesRepId     int           NULL     CONSTRAINT FK_Orders_SalesRep REFERENCES hr.Employees (EmployeeId),
    WarehouseId    int           NOT NULL,
    OrderDate      datetime2(0)  NOT NULL,
    Status         varchar(10)   NOT NULL CONSTRAINT CK_Orders_Status CHECK (Status IN ('Pending', 'Shipped', 'Delivered', 'Cancelled', 'Returned')),
    ShippedDate    datetime2(0)  NULL,
    ShipCity       nvarchar(60)  NOT NULL,
    ShipCountry    nvarchar(60)  NOT NULL,
    ShippingFee    decimal(8, 2) NOT NULL CONSTRAINT DF_Orders_ShippingFee DEFAULT (0),
    CONSTRAINT CK_Orders_Shipped CHECK (ShippedDate IS NULL OR ShippedDate >= OrderDate)
);
CREATE INDEX IX_Orders_CustomerId ON sales.Orders (CustomerId);
CREATE INDEX IX_Orders_OrderDate ON sales.Orders (OrderDate) INCLUDE (Status, CustomerId);

CREATE TABLE sales.OrderLines (
    OrderLineId    int            IDENTITY(1, 1) NOT NULL CONSTRAINT PK_OrderLines PRIMARY KEY,
    OrderId        int            NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES sales.Orders (OrderId),
    ProductId      int            NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES catalog.Products (ProductId),
    Quantity       int            NOT NULL CONSTRAINT CK_OrderLines_Quantity CHECK (Quantity > 0),
    UnitPrice      decimal(10, 2) NOT NULL CONSTRAINT CK_OrderLines_UnitPrice CHECK (UnitPrice >= 0),
    DiscountPct    decimal(4, 2)  NOT NULL CONSTRAINT DF_OrderLines_DiscountPct DEFAULT (0)
                                  CONSTRAINT CK_OrderLines_DiscountPct CHECK (DiscountPct BETWEEN 0 AND 50),
    LineTotal      AS (CONVERT(decimal(12, 2), Quantity * UnitPrice * (1 - DiscountPct / 100))) PERSISTED
);
CREATE INDEX IX_OrderLines_OrderId ON sales.OrderLines (OrderId);
CREATE INDEX IX_OrderLines_ProductId ON sales.OrderLines (ProductId);

CREATE TABLE sales.Payments (
    PaymentId      int            IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    OrderId        int            NOT NULL CONSTRAINT FK_Payments_Orders REFERENCES sales.Orders (OrderId),
    PaidAt         datetime2(0)   NOT NULL,
    Amount         decimal(12, 2) NOT NULL,
    Method         varchar(12)    NOT NULL CONSTRAINT CK_Payments_Method CHECK (Method IN ('Card', 'BankTransfer', 'Invoice', 'PayPal')),
    IsRefund       bit            NOT NULL CONSTRAINT DF_Payments_IsRefund DEFAULT (0)
);
CREATE INDEX IX_Payments_OrderId ON sales.Payments (OrderId);

/* ------------------------------------------------------------------ inventory */

CREATE TABLE inventory.Warehouses (
    WarehouseId    int           IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Warehouses PRIMARY KEY,
    Name           nvarchar(60)  NOT NULL CONSTRAINT UQ_Warehouses_Name UNIQUE,
    City           nvarchar(60)  NOT NULL,
    Country        nvarchar(60)  NOT NULL
);

CREATE TABLE inventory.Stock (
    WarehouseId    int NOT NULL CONSTRAINT FK_Stock_Warehouses REFERENCES inventory.Warehouses (WarehouseId),
    ProductId      int NOT NULL CONSTRAINT FK_Stock_Products REFERENCES catalog.Products (ProductId),
    QuantityOnHand int NOT NULL CONSTRAINT CK_Stock_Quantity CHECK (QuantityOnHand >= 0),
    ReorderLevel   int NOT NULL CONSTRAINT CK_Stock_Reorder CHECK (ReorderLevel >= 0),
    CONSTRAINT PK_Stock PRIMARY KEY (WarehouseId, ProductId)
);

ALTER TABLE sales.Orders ADD CONSTRAINT FK_Orders_Warehouses FOREIGN KEY (WarehouseId) REFERENCES inventory.Warehouses (WarehouseId);
GO

/* ------------------------------------------------------------------ descriptions */

CREATE OR ALTER PROCEDURE #Describe @Schema sysname, @Table sysname, @Column sysname = NULL, @Text nvarchar(500)
AS
BEGIN
    IF @Column IS NULL
        EXEC sys.sp_addextendedproperty N'MS_Description', @Text, N'SCHEMA', @Schema, N'TABLE', @Table;
    ELSE
        EXEC sys.sp_addextendedproperty N'MS_Description', @Text, N'SCHEMA', @Schema, N'TABLE', @Table, N'COLUMN', @Column;
END;
GO

EXEC #Describe 'hr', 'Departments', NULL, N'Company departments (Sales, Warehouse Operations, Finance, ...).';
EXEC #Describe 'hr', 'Departments', 'Name', N'Department name.';

EXEC #Describe 'hr', 'Employees', NULL, N'Staff. ManagerId points to the employee''s manager (NULL for the CEO). Sales reps are employees in the Sales department.';
EXEC #Describe 'hr', 'Employees', 'Email', N'Work email address.';
EXEC #Describe 'hr', 'Employees', 'JobTitle', N'Job title, e.g. Account Executive, Warehouse Associate.';
EXEC #Describe 'hr', 'Employees', 'ManagerId', N'EmployeeId of this employee''s direct manager; NULL for the CEO.';
EXEC #Describe 'hr', 'Employees', 'HireDate', N'Date the employee started.';
EXEC #Describe 'hr', 'Employees', 'TerminationDate', N'Date the employee left; NULL means currently employed.';
EXEC #Describe 'hr', 'Employees', 'Salary', N'Annual gross salary in USD. Confidential.';

EXEC #Describe 'catalog', 'Categories', NULL, N'Product categories in a two-level tree: top-level categories have ParentCategoryId NULL.';
EXEC #Describe 'catalog', 'Categories', 'ParentCategoryId', N'Parent category; NULL for a top-level category.';

EXEC #Describe 'catalog', 'Suppliers', NULL, N'Manufacturers and wholesalers that FoundryTrade buys products from.';
EXEC #Describe 'catalog', 'Suppliers', 'LeadTimeDays', N'Typical days from purchase order to delivery.';

EXEC #Describe 'catalog', 'Products', NULL, N'Products FoundryTrade sells. Each product belongs to one (sub)category and one supplier.';
EXEC #Describe 'catalog', 'Products', 'Sku', N'Stock keeping unit code, e.g. PT-0042.';
EXEC #Describe 'catalog', 'Products', 'ListPrice', N'Current catalogue selling price per unit in USD. Historic sale prices are in sales.OrderLines.UnitPrice.';
EXEC #Describe 'catalog', 'Products', 'UnitCost', N'Current purchase cost per unit in USD; margin = ListPrice - UnitCost.';
EXEC #Describe 'catalog', 'Products', 'IsDiscontinued', N'1 when the product is no longer sold.';
EXEC #Describe 'catalog', 'Products', 'LaunchedOn', N'Date the product was first offered.';

EXEC #Describe 'sales', 'Customers', NULL, N'Customers. CompanyName is NULL for consumers (individuals).';
EXEC #Describe 'sales', 'Customers', 'CompanyName', N'Business name; NULL when Segment = ''Consumer''.';
EXEC #Describe 'sales', 'Customers', 'FirstName', N'First name of the customer or of the business contact.';
EXEC #Describe 'sales', 'Customers', 'Segment', N'Customer segment: Consumer, SmallBusiness or Enterprise.';
EXEC #Describe 'sales', 'Customers', 'CreatedAt', N'When the customer account was created (UTC).';

EXEC #Describe 'sales', 'Orders', NULL, N'Sales orders (header). Revenue is the sum of sales.OrderLines.LineTotal; exclude Cancelled orders (and usually Returned) when reporting revenue.';
EXEC #Describe 'sales', 'Orders', 'SalesRepId', N'Employee who handled the order; NULL for web orders placed without a rep.';
EXEC #Describe 'sales', 'Orders', 'WarehouseId', N'Warehouse the order ships from.';
EXEC #Describe 'sales', 'Orders', 'OrderDate', N'When the order was placed (UTC).';
EXEC #Describe 'sales', 'Orders', 'Status', N'Pending, Shipped, Delivered, Cancelled or Returned.';
EXEC #Describe 'sales', 'Orders', 'ShippedDate', N'When the order left the warehouse; NULL if not shipped yet or cancelled.';
EXEC #Describe 'sales', 'Orders', 'ShippingFee', N'Shipping charged to the customer in USD (not part of LineTotal).';

EXEC #Describe 'sales', 'OrderLines', NULL, N'Order line items: one product per line.';
EXEC #Describe 'sales', 'OrderLines', 'UnitPrice', N'Price per unit actually charged on this order, before discount, in USD.';
EXEC #Describe 'sales', 'OrderLines', 'DiscountPct', N'Discount percentage applied to this line, 0-50.';
EXEC #Describe 'sales', 'OrderLines', 'LineTotal', N'Computed: Quantity * UnitPrice * (1 - DiscountPct / 100), in USD.';

EXEC #Describe 'sales', 'Payments', NULL, N'Payments received against orders. Refunds are rows with IsRefund = 1 and a negative Amount.';
EXEC #Describe 'sales', 'Payments', 'Amount', N'Amount in USD; negative for refunds.';
EXEC #Describe 'sales', 'Payments', 'Method', N'Card, BankTransfer, Invoice or PayPal.';

EXEC #Describe 'inventory', 'Warehouses', NULL, N'FoundryTrade warehouses that hold stock and ship orders.';
EXEC #Describe 'inventory', 'Stock', NULL, N'Current stock level of each product in each warehouse.';
EXEC #Describe 'inventory', 'Stock', 'QuantityOnHand', N'Units currently in the warehouse.';
EXEC #Describe 'inventory', 'Stock', 'ReorderLevel', N'When QuantityOnHand falls below this, the product should be reordered.';
GO
