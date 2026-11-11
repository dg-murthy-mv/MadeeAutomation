/* =====================================================================
   MadeeAutomation - 010_Phase4_Scheduler.sql
   Run AFTER 009. Safe to run more than once.

   Adds
     WorkflowDefinitions.NextRunAtUtc, LastRunAtUtc, LastRunStatus, RetryAttempt
         - kept up to date by the scheduler in WebApi (scheduled workflows only)
     Orders.PosError
         - why an order could not be sent to the client's POS (NULL = it was, or there is no POS)

   Existing scheduled workflows get their next run worked out by WebApi on its first check.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.Orders', 'TaxAmount') IS NULL
    BEGIN
        ;THROW 50010, 'Run 009_Round009_Tax.sql first.', 1;
    END

    IF COL_LENGTH('dbo.WorkflowDefinitions', 'NextRunAtUtc') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.WorkflowDefinitions ADD
                   NextRunAtUtc  datetime2      NULL,
                   LastRunAtUtc  datetime2      NULL,
                   LastRunStatus nvarchar(300)  NULL,
                   RetryAttempt  int NOT NULL CONSTRAINT DF_WorkflowDefinitions_RetryAttempt DEFAULT (0);');
        EXEC(N'CREATE INDEX IX_WorkflowDefinitions_TriggerType_IsEnabled_NextRunAtUtc
                   ON dbo.WorkflowDefinitions (TriggerType, IsEnabled, NextRunAtUtc);');
        PRINT 'Added WorkflowDefinitions.NextRunAtUtc, LastRunAtUtc, LastRunStatus, RetryAttempt';
    END

    IF COL_LENGTH('dbo.Orders', 'PosError') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Orders ADD PosError nvarchar(300) NULL;');
        PRINT 'Added Orders.PosError';
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
