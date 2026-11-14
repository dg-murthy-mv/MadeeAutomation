/* =====================================================================
   MadeeAutomation - 013_Reports.sql
   Run AFTER 012. Safe to run more than once.

   Reports for clients (portal) and for MadeeVision (Admin):
     AiUsage        - one row per AI (Claude) call: client, purpose, tokens, cost in US$
     ClientConfigs  - MonthlyReportEnabled (on for every client) and MonthlyReportSentFor
     InboundEvents  - an index so a client's messages for a period are found quickly

   No existing data is changed.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID('dbo.Bookings', 'U') IS NULL
    BEGIN
        ;THROW 50013, 'Run 012_Beauty_Bookings.sql first.', 1;
    END

    -- ---- AI usage ----
    IF OBJECT_ID('dbo.AiUsage', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.AiUsage (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_AiUsage PRIMARY KEY NONCLUSTERED,
            ClientConfigId uniqueidentifier NULL,
            Purpose nvarchar(50) NOT NULL,
            Model nvarchar(100) NOT NULL,
            InputTokens int NOT NULL,
            OutputTokens int NOT NULL,
            CacheReadTokens int NOT NULL CONSTRAINT DF_AiUsage_CacheReadTokens DEFAULT (0),
            CacheWriteTokens int NOT NULL CONSTRAINT DF_AiUsage_CacheWriteTokens DEFAULT (0),
            CostUsd decimal(18,6) NOT NULL,
            IsTest bit NOT NULL CONSTRAINT DF_AiUsage_IsTest DEFAULT (0),
            CreatedAtUtc datetime2 NOT NULL);
            CREATE CLUSTERED INDEX IX_AiUsage_CreatedAtUtc ON dbo.AiUsage (CreatedAtUtc);
            CREATE INDEX IX_AiUsage_ClientConfigId_CreatedAtUtc ON dbo.AiUsage (ClientConfigId, CreatedAtUtc);');
        PRINT 'Created AiUsage';
    END

    -- ---- Monthly report email ----
    IF COL_LENGTH('dbo.ClientConfigs', 'MonthlyReportEnabled') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   MonthlyReportEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_MonthlyReportEnabled DEFAULT (1),
                   MonthlyReportSentFor nvarchar(7) NULL;');
        PRINT 'Added MonthlyReportEnabled / MonthlyReportSentFor to ClientConfigs';
    END

    -- ---- Faster report queries ----
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InboundEvents_ClientConfigId_ReceivedAtUtc' AND object_id = OBJECT_ID('dbo.InboundEvents'))
    BEGIN
        EXEC(N'CREATE INDEX IX_InboundEvents_ClientConfigId_ReceivedAtUtc ON dbo.InboundEvents (ClientConfigId, ReceivedAtUtc) INCLUDE (Channel, CustomerId, Kind);');
        PRINT 'Created IX_InboundEvents_ClientConfigId_ReceivedAtUtc';
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
