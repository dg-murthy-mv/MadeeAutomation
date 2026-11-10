/* =====================================================================
   MadeeAutomation - 004_Round004_MenuAndHours.sql
   Run AFTER 003. Safe to run more than once.

   Adds
     MenuItems     - structured menu: name, category, price, other names, sold-out switch
     OpeningHours  - weekly opening periods in the client's time zone (2 per day max in Admin)
     ClosedDates   - holidays / one-off closed days

   Existing free-text menus (ClientConfigs.MenuText) keep working until you press
   "Import from old menu text" on the client's Menu page in Admin.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.AdminUsers', 'TimeZoneId') IS NULL
    BEGIN
        ;THROW 50004, 'Run 003_Round003_Markets.sql first.', 1;
    END

    IF OBJECT_ID(N'dbo.MenuItems', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.MenuItems (
            Id             uniqueidentifier NOT NULL CONSTRAINT PK_MenuItems PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL,
            Name           nvarchar(200)    NOT NULL,
            Category       nvarchar(100)    NULL,
            Price          decimal(18,2)    NULL,
            Aliases        nvarchar(500)    NULL,
            IsAvailable    bit              NOT NULL,
            SortOrder      int              NOT NULL,
            CreatedAtUtc   datetime2        NOT NULL,
            UpdatedAtUtc   datetime2        NULL,
            CONSTRAINT FK_MenuItems_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE INDEX IX_MenuItems_ClientConfigId ON dbo.MenuItems (ClientConfigId);');
        PRINT 'Created table MenuItems';
    END

    IF OBJECT_ID(N'dbo.OpeningHours', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.OpeningHours (
            Id             uniqueidentifier NOT NULL CONSTRAINT PK_OpeningHours PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL,
            DayOfWeek      int              NOT NULL,   -- 0 = Sunday ... 6 = Saturday
            OpenTime       time(0)          NOT NULL,
            CloseTime      time(0)          NOT NULL,   -- earlier than OpenTime = past midnight
            CONSTRAINT FK_OpeningHours_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE,
            CONSTRAINT CK_OpeningHours_DayOfWeek CHECK (DayOfWeek BETWEEN 0 AND 6)
        );');
        EXEC(N'CREATE INDEX IX_OpeningHours_ClientConfigId ON dbo.OpeningHours (ClientConfigId);');
        PRINT 'Created table OpeningHours';
    END

    IF OBJECT_ID(N'dbo.ClosedDates', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.ClosedDates (
            Id             uniqueidentifier NOT NULL CONSTRAINT PK_ClosedDates PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL,
            [Date]         date             NOT NULL,
            Reason         nvarchar(200)    NULL,
            CONSTRAINT FK_ClosedDates_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_ClosedDates_ClientConfigId_Date ON dbo.ClosedDates (ClientConfigId, [Date]);');
        PRINT 'Created table ClosedDates';
    END

    EXEC sp_executesql N'
        DECLARE @withText int = (SELECT COUNT(*) FROM dbo.ClientConfigs WHERE MenuText IS NOT NULL AND LEN(MenuText) > 0);
        PRINT CONCAT(''Clients with an old free-text menu (import them in Admin -> Menu): '', @withText);';

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
