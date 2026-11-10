/* =====================================================================
   MadeeAutomation - 006_Round006_OrderNotices.sql
   Run AFTER 005. Safe to run more than once.

   Adds
     ClientConfigs.NotifyCustomerOnOrderStatus - message the customer when staff accept or cancel
                                                 an order (ON for every existing client)
     Orders.Language            - the language the customer wrote in
     Orders.CancelReason        - why staff cancelled (goes into the customer's message)
     Orders.CustomerNoticeSent  - 1 = customer was told, 0 = could not be told, NULL = not tried
     Orders.CustomerNoticeNote  - what was sent, or why it could not be sent

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
    BEGIN
        ;THROW 50006, 'Run 005_Round005_Orders_Portal.sql first.', 1;
    END

    IF COL_LENGTH('dbo.ClientConfigs', 'NotifyCustomerOnOrderStatus') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD NotifyCustomerOnOrderStatus bit NOT NULL
                   CONSTRAINT DF_ClientConfigs_NotifyCustomerOnOrderStatus DEFAULT (1);');
        PRINT 'Added ClientConfigs.NotifyCustomerOnOrderStatus (on for every client)';
    END

    IF COL_LENGTH('dbo.Orders', 'Language') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Orders ADD
                   Language           nvarchar(40)  NULL,
                   CancelReason       nvarchar(300) NULL,
                   CustomerNoticeSent bit           NULL,
                   CustomerNoticeNote nvarchar(500) NULL;');
        PRINT 'Added Orders.Language, CancelReason, CustomerNoticeSent, CustomerNoticeNote';
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
