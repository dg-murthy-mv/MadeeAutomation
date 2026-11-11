/* =====================================================================
   MadeeAutomation - 009_Round009_Tax.sql
   Run AFTER 008. Safe to run more than once.

   Adds
     ClientConfigs.TaxName, TaxRatePercent, PricesIncludeTax
         - the tax shown on that client's orders (GST / VAT). Blank = no tax lines.
     Orders.Subtotal, TaxAmount, TaxName, TaxRatePercent, PricesIncludeTax
         - the tax on each order, as it was when the order was taken

   Existing clients get NO tax until you set it on the client's Edit page.
   Existing orders are not changed.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Conversations', N'U') IS NULL
    BEGIN
        ;THROW 50009, 'Run 008_Round008_MenuButtons.sql first.', 1;
    END

    IF COL_LENGTH('dbo.ClientConfigs', 'TaxName') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   TaxName          nvarchar(20) NULL,
                   TaxRatePercent   decimal(5,2) NULL,
                   PricesIncludeTax bit NOT NULL CONSTRAINT DF_ClientConfigs_PricesIncludeTax DEFAULT (1);');
        PRINT 'Added ClientConfigs.TaxName, TaxRatePercent, PricesIncludeTax (no tax until you set it)';
    END

    IF COL_LENGTH('dbo.Orders', 'TaxAmount') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Orders ADD
                   Subtotal         decimal(18,2) NULL,
                   TaxAmount        decimal(18,2) NULL,
                   TaxName          nvarchar(20)  NULL,
                   TaxRatePercent   decimal(5,2)  NULL,
                   PricesIncludeTax bit NOT NULL CONSTRAINT DF_Orders_PricesIncludeTax DEFAULT (1);');
        PRINT 'Added Orders.Subtotal, TaxAmount, TaxName, TaxRatePercent, PricesIncludeTax';
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
