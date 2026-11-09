/* =====================================================================
   MadeeAutomation - 002_Round002_AdminAuth_Alerts.sql
   Run AFTER 001. Safe to run more than once (creates only what is missing).

   Adds
     AdminUsers            - Admin.Web sign-in (passwords stored as hashes)
     PasswordResetTokens   - "forgot password" links (only a hash of the token is stored)
     ClientConfigs.FallbackReplyText / StaffAlertEmail / StaffAlertLineUserId

   No admin account is created here: the first time you open Admin.Web it asks
   you to create the first admin (that page disappears once one exists).

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ClientConfigs', N'U') IS NULL
    BEGIN
        ;THROW 50002, 'ClientConfigs does not exist - run 001_Baseline_and_Round001.sql first.', 1;
    END

    IF OBJECT_ID(N'dbo.AdminUsers', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.AdminUsers (
            Id                 uniqueidentifier NOT NULL CONSTRAINT PK_AdminUsers PRIMARY KEY,
            Email              nvarchar(256)    NOT NULL,
            DisplayName        nvarchar(200)    NOT NULL,
            PasswordHash       nvarchar(500)    NOT NULL,
            SecurityStamp      nvarchar(64)     NOT NULL,
            IsActive           bit              NOT NULL,
            MustChangePassword bit              NOT NULL,
            FailedLoginCount   int              NOT NULL,
            LockoutEndUtc      datetime2        NULL,
            LastLoginAtUtc     datetime2        NULL,
            CreatedAtUtc       datetime2        NOT NULL,
            UpdatedAtUtc       datetime2        NULL
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_AdminUsers_Email ON dbo.AdminUsers (Email);');
        PRINT 'Created table AdminUsers';
    END

    IF OBJECT_ID(N'dbo.PasswordResetTokens', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.PasswordResetTokens (
            Id           uniqueidentifier NOT NULL CONSTRAINT PK_PasswordResetTokens PRIMARY KEY,
            AdminUserId  uniqueidentifier NOT NULL,
            TokenHash    nvarchar(64)     NOT NULL,
            ExpiresAtUtc datetime2        NOT NULL,
            UsedAtUtc    datetime2        NULL,
            CreatedAtUtc datetime2        NOT NULL,
            CONSTRAINT FK_PasswordResetTokens_AdminUsers_AdminUserId FOREIGN KEY (AdminUserId)
                REFERENCES dbo.AdminUsers (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_PasswordResetTokens_TokenHash ON dbo.PasswordResetTokens (TokenHash);');
        EXEC(N'CREATE INDEX IX_PasswordResetTokens_AdminUserId ON dbo.PasswordResetTokens (AdminUserId);');
        PRINT 'Created table PasswordResetTokens';
    END

    IF COL_LENGTH('dbo.ClientConfigs', 'FallbackReplyText') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ClientConfigs ADD FallbackReplyText nvarchar(1000) NULL;'); PRINT 'Added ClientConfigs.FallbackReplyText'; END

    IF COL_LENGTH('dbo.ClientConfigs', 'StaffAlertEmail') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ClientConfigs ADD StaffAlertEmail nvarchar(256) NULL;'); PRINT 'Added ClientConfigs.StaffAlertEmail'; END

    IF COL_LENGTH('dbo.ClientConfigs', 'StaffAlertLineUserId') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ClientConfigs ADD StaffAlertLineUserId nvarchar(100) NULL;'); PRINT 'Added ClientConfigs.StaffAlertLineUserId'; END

    EXEC sp_executesql N'
        DECLARE @admins int = (SELECT COUNT(*) FROM dbo.AdminUsers);
        PRINT CONCAT(''Admin accounts: '', @admins, CASE WHEN @admins = 0 THEN '' - open Admin.Web to create the first one.'' ELSE '''' END);';

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
