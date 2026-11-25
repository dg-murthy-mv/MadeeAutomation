/* =====================================================================
   MadeeAutomation - 016_Reviews.sql
   Run AFTER 015. Safe to run more than once.

   Review booster:
     ClientConfigs  - GoogleReviewUrl, FacebookReviewUrl (the business's "write a review" links),
                      ReviewRequestsEnabled (send the links after the rating question; on by default,
                      but nothing is sent until a link is saved on the Reviews page)
     Bookings       - ReviewAskedAtUtc (when the customer was sent the review links)
     ReviewClicks   - each time a customer opens a review link (chat-app link previews are not counted)

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.ClientConfigs', 'Plan') IS NULL
    BEGIN
        ;THROW 50016, 'Run 015_Plans.sql first.', 1;
    END

    -- ---- Review links on each client ----
    IF COL_LENGTH('dbo.ClientConfigs', 'GoogleReviewUrl') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   GoogleReviewUrl nvarchar(500) NULL,
                   FacebookReviewUrl nvarchar(500) NULL,
                   ReviewRequestsEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_ReviewRequestsEnabled DEFAULT (1);');
        PRINT 'Added GoogleReviewUrl / FacebookReviewUrl / ReviewRequestsEnabled to ClientConfigs';
    END

    -- ---- When the customer was asked ----
    IF COL_LENGTH('dbo.Bookings', 'ReviewAskedAtUtc') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Bookings ADD ReviewAskedAtUtc datetime2 NULL;');
        PRINT 'Added ReviewAskedAtUtc to Bookings';
    END

    -- ---- Link clicks ----
    IF OBJECT_ID('dbo.ReviewClicks', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.ReviewClicks (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_ReviewClicks PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_ReviewClicks_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            BookingId uniqueidentifier NULL,
            Site nvarchar(20) NOT NULL,
            ClickedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_ReviewClicks_ClientConfigId_ClickedAtUtc ON dbo.ReviewClicks (ClientConfigId, ClickedAtUtc);');
        PRINT 'Created ReviewClicks';
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
