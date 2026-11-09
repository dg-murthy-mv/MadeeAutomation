/* =====================================================================
   MadeeAutomation - 003_Round003_Markets.sql
   Run AFTER 002. Safe to run more than once.

   Adds
     AdminUsers.TimeZoneId   - each admin's own time zone for admin-only pages (default Asia/Bangkok)
   Tidies
     ClientConfigs.Currency  - upper-case (THB, INR, PHP, AED)
   Checks (read-only)
     lists clients whose country/currency/time zone don't match the four markets

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.AdminUsers', N'U') IS NULL
    BEGIN
        ;THROW 50003, 'AdminUsers does not exist - run 002_Round002_AdminAuth_Alerts.sql first.', 1;
    END

    IF COL_LENGTH('dbo.AdminUsers', 'TimeZoneId') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.AdminUsers ADD TimeZoneId nvarchar(64) NOT NULL
               CONSTRAINT DF_AdminUsers_TimeZoneId DEFAULT (N''Asia/Bangkok'');');
        PRINT 'Added AdminUsers.TimeZoneId (default Asia/Bangkok)';
    END

    UPDATE dbo.ClientConfigs SET Currency = UPPER(LTRIM(RTRIM(Currency)))
    WHERE Currency <> UPPER(LTRIM(RTRIM(Currency))) COLLATE Latin1_General_CS_AS;
    PRINT CONCAT('Client currencies upper-cased: ', @@ROWCOUNT);

    IF EXISTS (SELECT 1 FROM dbo.ClientConfigs
               WHERE NOT ((Country = 'TH' AND Currency = 'THB') OR (Country = 'IN' AND Currency = 'INR')
                       OR (Country = 'PH' AND Currency = 'PHP') OR (Country = 'AE' AND Currency = 'AED'))
                  OR TimeZoneId NOT IN ('Asia/Bangkok', 'Asia/Kolkata', 'Asia/Manila', 'Asia/Dubai', 'UTC'))
    BEGIN
        PRINT 'CHECK: these clients have an unusual country / currency / time zone combination. Fix in Admin if wrong:';
        SELECT ClientCode, ClientName, Country, Currency, TimeZoneId
        FROM dbo.ClientConfigs
        WHERE NOT ((Country = 'TH' AND Currency = 'THB') OR (Country = 'IN' AND Currency = 'INR')
                OR (Country = 'PH' AND Currency = 'PHP') OR (Country = 'AE' AND Currency = 'AED'))
           OR TimeZoneId NOT IN ('Asia/Bangkok', 'Asia/Kolkata', 'Asia/Manila', 'Asia/Dubai', 'UTC');
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
