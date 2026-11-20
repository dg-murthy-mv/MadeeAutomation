/* =====================================================================
   MadeeAutomation - 015_Plans.sql
   Run AFTER 014. Safe to run more than once.

   Plans and subscriptions:
     ClientConfigs        - Plan (0 Demo, 1 Trial, 2 Starter, 3 Business, 4 Premium), BillingCycle
                            (1 monthly, 2 yearly), PlanEndsAtUtc, PlanPrice (agreed price per cycle), PlanNote
     SubscriptionPayments - payments recorded in Admin (bank transfer, GCash...)
     SubscriptionNotices  - plan emails / messages already sent (each goes out once)

   Every client that exists today becomes "Demo" (no limits, never ends) - your demo and test clients
   keep working exactly as before. New clients made in Admin start on a 15-day free trial.
   Change a real client's plan in Admin -> Clients -> (client) -> Plan.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID('dbo.WaitlistEntries', 'U') IS NULL
    BEGIN
        ;THROW 50015, 'Run 014_Clinics.sql first.', 1;
    END

    -- ---- Plan on each client (existing clients = Demo) ----
    IF COL_LENGTH('dbo.ClientConfigs', 'Plan') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   [Plan] int NOT NULL CONSTRAINT DF_ClientConfigs_Plan DEFAULT (0),
                   BillingCycle int NULL,
                   PlanEndsAtUtc datetime2 NULL,
                   PlanPrice decimal(18,2) NULL,
                   PlanNote nvarchar(500) NULL;');
        PRINT 'Added Plan / BillingCycle / PlanEndsAtUtc / PlanPrice / PlanNote to ClientConfigs (existing clients = Demo)';
    END

    -- ---- Payments ----
    IF OBJECT_ID('dbo.SubscriptionPayments', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.SubscriptionPayments (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_SubscriptionPayments PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_SubscriptionPayments_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            PaidOn date NOT NULL,
            Amount decimal(18,2) NOT NULL,
            Currency nvarchar(3) NOT NULL,
            Method nvarchar(50) NOT NULL,
            Reference nvarchar(100) NULL,
            [Plan] int NOT NULL,
            Cycle int NOT NULL,
            Cycles int NOT NULL,
            PeriodFromUtc datetime2 NOT NULL,
            PeriodToUtc datetime2 NOT NULL,
            Note nvarchar(500) NULL,
            RecordedBy nvarchar(256) NOT NULL,
            CreatedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_SubscriptionPayments_ClientConfigId_PaidOn ON dbo.SubscriptionPayments (ClientConfigId, PaidOn);
            CREATE INDEX IX_SubscriptionPayments_PaidOn ON dbo.SubscriptionPayments (PaidOn);');
        PRINT 'Created SubscriptionPayments';
    END

    -- ---- Notices sent once ----
    IF OBJECT_ID('dbo.SubscriptionNotices', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.SubscriptionNotices (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_SubscriptionNotices PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_SubscriptionNotices_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            NoticeKey nvarchar(200) NOT NULL,
            CreatedAtUtc datetime2 NOT NULL);
            CREATE UNIQUE INDEX IX_SubscriptionNotices_ClientConfigId_NoticeKey ON dbo.SubscriptionNotices (ClientConfigId, NoticeKey);');
        PRINT 'Created SubscriptionNotices';
    END

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT '=== DRY RUN - rolled back. Set @Commit = 1 to keep the changes. ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT '=== FAILED - nothing was changed ===';
    THROW;
END CATCH;
