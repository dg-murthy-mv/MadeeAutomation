/* =====================================================================
   MadeeAutomation - 008_Round008_MenuButtons.sql
   Run AFTER 006. Safe to run more than once. (There is no script 007 - Round 007 needed none.)

   Adds
     Conversations                    - each customer's cart and chat state between messages
     InboundEvents.Kind               - 'text' (typed), 'tap' (a button) or 'follow' (added as friend)
     ClientConfigs.MenuButtonsEnabled - show the menu with buttons in the chat (ON for every client)

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.Orders', 'Language') IS NULL
    BEGIN
        ;THROW 50008, 'Run 006_Round006_OrderNotices.sql first.', 1;
    END

    IF COL_LENGTH('dbo.InboundEvents', 'Kind') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.InboundEvents ADD Kind nvarchar(20) NOT NULL
                   CONSTRAINT DF_InboundEvents_Kind DEFAULT (N''text'');');
        PRINT 'Added InboundEvents.Kind (existing events are text)';
    END

    IF COL_LENGTH('dbo.ClientConfigs', 'MenuButtonsEnabled') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD MenuButtonsEnabled bit NOT NULL
                   CONSTRAINT DF_ClientConfigs_MenuButtonsEnabled DEFAULT (1);');
        PRINT 'Added ClientConfigs.MenuButtonsEnabled (on for every client)';
    END

    IF OBJECT_ID(N'dbo.Conversations', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.Conversations (
            Id             uniqueidentifier NOT NULL CONSTRAINT PK_Conversations PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL,
            Channel        nvarchar(20)     NOT NULL,
            CustomerId     nvarchar(200)    NOT NULL,
            CartJson       nvarchar(max)    NULL,       -- [{ "i": menu item id, "q": quantity }]
            State          nvarchar(30)     NOT NULL,   -- '''' or ''awaiting_time''
            Language       nvarchar(40)     NULL,
            LastCategory   nvarchar(100)    NULL,
            UpdatedAtUtc   datetime2        NOT NULL,
            CONSTRAINT FK_Conversations_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_Conversations_ClientConfigId_Channel_CustomerId
                   ON dbo.Conversations (ClientConfigId, Channel, CustomerId);');
        PRINT 'Created table Conversations';
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
