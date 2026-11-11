/* =====================================================================
   MadeeAutomation - 011_Phase5_Meta.sql
   Run AFTER 010. Safe to run more than once.

   Adds
     InboundEvents.CustomerName
         - the name WhatsApp sends with each message (the customer's WhatsApp profile name)

   The Meta app secret and verify token for WhatsApp / Messenger are saved with the client's
   other credentials (encrypted), so they need no new columns.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.WorkflowDefinitions', 'NextRunAtUtc') IS NULL
    BEGIN
        ;THROW 50011, 'Run 010_Phase4_Scheduler.sql first.', 1;
    END

    IF COL_LENGTH('dbo.InboundEvents', 'CustomerName') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.InboundEvents ADD CustomerName nvarchar(200) NULL;');
        PRINT 'Added InboundEvents.CustomerName';
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
