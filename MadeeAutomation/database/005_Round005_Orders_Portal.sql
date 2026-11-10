/* =====================================================================
   MadeeAutomation - 005_Round005_Orders_Portal.sql
   Run AFTER 004. Safe to run more than once.

   Adds
     Orders / OrderItems       - every order the bot takes is saved here (with or without a POS)
     MenuItems.DailyLimit      - the most the bot may take orders for in one day (NULL = no limit)
     AdminUsers.ClientConfigId - NULL = MadeeAutomation staff; set = a client's own portal user,
                                 limited to that one business

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NULL
    BEGIN
        ;THROW 50005, 'Run 004_Round004_MenuAndHours.sql first.', 1;
    END

    IF COL_LENGTH('dbo.MenuItems', 'DailyLimit') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.MenuItems ADD DailyLimit int NULL;');
        PRINT 'Added MenuItems.DailyLimit';
    END

    IF COL_LENGTH('dbo.AdminUsers', 'ClientConfigId') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.AdminUsers ADD ClientConfigId uniqueidentifier NULL
                   CONSTRAINT FK_AdminUsers_ClientConfigs_ClientConfigId FOREIGN KEY
                   REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE;');
        EXEC(N'CREATE INDEX IX_AdminUsers_ClientConfigId ON dbo.AdminUsers (ClientConfigId);');
        PRINT 'Added AdminUsers.ClientConfigId (existing accounts stay MadeeAutomation staff)';
    END

    IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.Orders (
            Id                 uniqueidentifier NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            ClientConfigId     uniqueidentifier NOT NULL,
            OrderNumber        int              NOT NULL,   -- 1, 2, 3 ... per client
            Reference          nvarchar(100)    NOT NULL,   -- POS order id, or ORD-0001 when there is no POS
            PosOrderId         nvarchar(100)    NULL,
            Channel            nvarchar(20)     NOT NULL,
            CustomerId         nvarchar(200)    NULL,
            CustomerName       nvarchar(200)    NULL,
            CustomerMessage    nvarchar(max)    NULL,
            Currency           nvarchar(3)      NULL,
            Total              decimal(18,2)    NULL,
            RequestedForUtc    datetime2        NULL,
            RequestedForText   nvarchar(200)    NULL,
            OrderDate          date             NOT NULL,   -- local day the order is FOR (daily limits count on it)
            CustomerNote       nvarchar(1000)   NULL,
            Status             int              NOT NULL,   -- 1 New, 2 Accepted, 3 Completed, 4 Cancelled
            StatusChangedBy    nvarchar(200)    NULL,
            StatusChangedAtUtc datetime2        NULL,
            CreatedAtUtc       datetime2        NOT NULL,
            CONSTRAINT FK_Orders_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE,
            CONSTRAINT CK_Orders_Status CHECK (Status BETWEEN 1 AND 4)
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_Orders_ClientConfigId_OrderNumber ON dbo.Orders (ClientConfigId, OrderNumber);');
        EXEC(N'CREATE INDEX IX_Orders_ClientConfigId_OrderDate ON dbo.Orders (ClientConfigId, OrderDate);');
        EXEC(N'CREATE INDEX IX_Orders_ClientConfigId_CreatedAtUtc ON dbo.Orders (ClientConfigId, CreatedAtUtc);');
        PRINT 'Created table Orders';
    END

    IF OBJECT_ID(N'dbo.OrderItems', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.OrderItems (
            Id         uniqueidentifier NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
            OrderId    uniqueidentifier NOT NULL,
            MenuItemId uniqueidentifier NULL,     -- no foreign key on purpose: deleting a menu item keeps past orders
            Name       nvarchar(200)    NOT NULL,
            Quantity   int              NOT NULL,
            UnitPrice  decimal(18,2)    NULL,
            LineTotal  decimal(18,2)    NULL,
            CONSTRAINT FK_OrderItems_Orders_OrderId FOREIGN KEY (OrderId)
                REFERENCES dbo.Orders (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE INDEX IX_OrderItems_OrderId ON dbo.OrderItems (OrderId);');
        EXEC(N'CREATE INDEX IX_OrderItems_MenuItemId ON dbo.OrderItems (MenuItemId);');
        PRINT 'Created table OrderItems';
    END

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT '=== DRY RUN - rolled back. Set @Commit = 1 to apply. ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT '=== FAILED - nothing was changed ===';
    THROW;
END CATCH;
