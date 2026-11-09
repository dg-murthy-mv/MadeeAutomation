/* =====================================================================
   MadeeAutomation - 001_Baseline_and_Round001.sql

   Safe to run on:
     * your existing MadeeAutomation database (creates only what is missing), and
     * a brand-new empty database (creates everything).

   What it does
     1. Creates ClientConfigs / WorkflowDefinitions / ExecutionLogs if missing
        (same shape the app already uses - nothing is dropped).
     2. Round 001 additions:
          ClientConfigs.KnowledgeBaseText, ClientConfigs.MenuText
          WorkflowDefinitions.Channel
          ExecutionLogs.InboundEventId / Channel / CustomerId / ActionsJson
          new table InboundEvents (+ unique index = duplicate guard)
     3. Moves old handler keys to the new ones:
          whatsapp-order-parser / line-order-parser / messenger-order-parser -> order-parser (+ Channel)
        and makes customer-message workflows TriggerType = MessageReceived (3).
     4. Lower-cases ClientCode (webhook URLs are matched in lower case).

   HOW TO RUN (same convention as Merkai)
     First run as is: @Commit = 0 -> everything runs, then rolls back. Read the messages.
     Then change @Commit to 1 and run again to keep the changes.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    /* ---------- 1. Base tables ---------- */
    IF OBJECT_ID(N'dbo.ClientConfigs', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.ClientConfigs (
            Id                         uniqueidentifier NOT NULL CONSTRAINT PK_ClientConfigs PRIMARY KEY,
            ClientName                 nvarchar(200)    NOT NULL,
            ClientCode                 nvarchar(50)     NOT NULL,
            Country                    nvarchar(max)    NOT NULL,
            TimeZoneId                 nvarchar(max)    NOT NULL,
            Currency                   nvarchar(max)    NOT NULL,
            Vertical                   int              NOT NULL,
            Status                     int              NOT NULL,
            IntegrationSettingsJson    nvarchar(max)    NOT NULL,
            MerkaiIntegrationEnabled   bit              NOT NULL,
            PosFirstIntegrationEnabled bit              NOT NULL,
            CreatedAtUtc               datetime2        NOT NULL,
            UpdatedAtUtc               datetime2        NULL
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_ClientConfigs_ClientCode ON dbo.ClientConfigs (ClientCode);');
        PRINT 'Created table ClientConfigs';
    END

    IF OBJECT_ID(N'dbo.WorkflowDefinitions', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.WorkflowDefinitions (
            Id                uniqueidentifier NOT NULL CONSTRAINT PK_WorkflowDefinitions PRIMARY KEY,
            ClientConfigId    uniqueidentifier NOT NULL,
            Name              nvarchar(200)    NOT NULL,
            HandlerKey        nvarchar(100)    NOT NULL,
            Vertical          int              NOT NULL,
            TriggerType       int              NOT NULL,
            TriggerConfigJson nvarchar(max)    NOT NULL,
            IsEnabled         bit              NOT NULL,
            CreatedAtUtc      datetime2        NOT NULL,
            UpdatedAtUtc      datetime2        NULL,
            CONSTRAINT FK_WorkflowDefinitions_ClientConfigs_ClientConfigId FOREIGN KEY (ClientConfigId)
                REFERENCES dbo.ClientConfigs (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE INDEX IX_WorkflowDefinitions_ClientConfigId ON dbo.WorkflowDefinitions (ClientConfigId);');
        PRINT 'Created table WorkflowDefinitions';
    END

    IF OBJECT_ID(N'dbo.ExecutionLogs', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.ExecutionLogs (
            Id                   uniqueidentifier NOT NULL CONSTRAINT PK_ExecutionLogs PRIMARY KEY,
            WorkflowDefinitionId uniqueidentifier NOT NULL,
            ClientConfigId       uniqueidentifier NOT NULL,
            Status               int              NOT NULL,
            TriggerPayloadJson   nvarchar(max)    NULL,
            ResultSummary        nvarchar(max)    NULL,
            ErrorMessage         nvarchar(max)    NULL,
            RetryCount           int              NOT NULL,
            StartedAtUtc         datetime2        NOT NULL,
            CompletedAtUtc       datetime2        NULL,
            DurationMs           bigint           NULL,
            CONSTRAINT FK_ExecutionLogs_WorkflowDefinitions_WorkflowDefinitionId FOREIGN KEY (WorkflowDefinitionId)
                REFERENCES dbo.WorkflowDefinitions (Id) ON DELETE CASCADE
        );');
        EXEC(N'CREATE INDEX IX_ExecutionLogs_WorkflowDefinitionId ON dbo.ExecutionLogs (WorkflowDefinitionId);');
        EXEC(N'CREATE INDEX IX_ExecutionLogs_ClientConfigId_StartedAtUtc ON dbo.ExecutionLogs (ClientConfigId, StartedAtUtc);');
        PRINT 'Created table ExecutionLogs';
    END

    /* ---------- 2. Round 001 columns ---------- */
    IF COL_LENGTH('dbo.ClientConfigs', 'KnowledgeBaseText') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ClientConfigs ADD KnowledgeBaseText nvarchar(max) NULL;'); PRINT 'Added ClientConfigs.KnowledgeBaseText'; END

    IF COL_LENGTH('dbo.ClientConfigs', 'MenuText') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ClientConfigs ADD MenuText nvarchar(max) NULL;'); PRINT 'Added ClientConfigs.MenuText'; END

    IF COL_LENGTH('dbo.WorkflowDefinitions', 'Channel') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.WorkflowDefinitions ADD Channel nvarchar(20) NULL;'); PRINT 'Added WorkflowDefinitions.Channel'; END

    IF COL_LENGTH('dbo.ExecutionLogs', 'InboundEventId') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ExecutionLogs ADD InboundEventId uniqueidentifier NULL;'); PRINT 'Added ExecutionLogs.InboundEventId'; END

    IF COL_LENGTH('dbo.ExecutionLogs', 'Channel') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ExecutionLogs ADD Channel nvarchar(20) NULL;'); PRINT 'Added ExecutionLogs.Channel'; END

    IF COL_LENGTH('dbo.ExecutionLogs', 'CustomerId') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ExecutionLogs ADD CustomerId nvarchar(100) NULL;'); PRINT 'Added ExecutionLogs.CustomerId'; END

    IF COL_LENGTH('dbo.ExecutionLogs', 'ActionsJson') IS NULL
    BEGIN EXEC(N'ALTER TABLE dbo.ExecutionLogs ADD ActionsJson nvarchar(max) NULL;'); PRINT 'Added ExecutionLogs.ActionsJson'; END

    IF OBJECT_ID(N'dbo.InboundEvents', N'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.InboundEvents (
            Id                   uniqueidentifier NOT NULL CONSTRAINT PK_InboundEvents PRIMARY KEY,
            ClientConfigId       uniqueidentifier NOT NULL,
            WorkflowDefinitionId uniqueidentifier NULL,
            Channel              nvarchar(20)     NOT NULL,
            ExternalEventId      nvarchar(200)    NOT NULL,
            CustomerId           nvarchar(100)    NULL,
            MessageText          nvarchar(max)    NULL,
            ReplyToken           nvarchar(200)    NULL,
            Status               int              NOT NULL,
            Attempts             int              NOT NULL,
            LastError            nvarchar(2000)   NULL,
            ExecutionLogId       uniqueidentifier NULL,
            ReceivedAtUtc        datetime2        NOT NULL,
            ProcessedAtUtc       datetime2        NULL
        );');
        EXEC(N'CREATE UNIQUE INDEX IX_InboundEvents_ClientConfigId_Channel_ExternalEventId
               ON dbo.InboundEvents (ClientConfigId, Channel, ExternalEventId);');
        EXEC(N'CREATE INDEX IX_InboundEvents_Status_ReceivedAtUtc ON dbo.InboundEvents (Status, ReceivedAtUtc);');
        PRINT 'Created table InboundEvents';
    END

    /* ---------- 3. Data: old handler keys -> new ones ---------- */
    DECLARE @n int;

    EXEC sp_executesql N'
        UPDATE dbo.WorkflowDefinitions
        SET Channel = CASE HandlerKey
                        WHEN ''whatsapp-order-parser''  THEN ''whatsapp''
                        WHEN ''line-order-parser''      THEN ''line''
                        WHEN ''messenger-order-parser'' THEN ''messenger'' END,
            HandlerKey = ''order-parser'',
            TriggerType = 3,
            UpdatedAtUtc = SYSUTCDATETIME()
        WHERE HandlerKey IN (''whatsapp-order-parser'', ''line-order-parser'', ''messenger-order-parser'');
        SET @n = @@ROWCOUNT;', N'@n int OUTPUT', @n = @n OUTPUT;
    PRINT CONCAT('Old order-parser workflows moved to "order-parser": ', @n);

    EXEC sp_executesql N'
        UPDATE dbo.WorkflowDefinitions
        SET TriggerType = 3, UpdatedAtUtc = SYSUTCDATETIME()
        WHERE HandlerKey IN (''order-parser'', ''faq-responder'', ''customer-message'') AND TriggerType <> 3;
        SET @n = @@ROWCOUNT;', N'@n int OUTPUT', @n = @n OUTPUT;
    PRINT CONCAT('Message workflows set to TriggerType MessageReceived: ', @n);

    /* ---------- 4. Lower-case client codes ---------- */
    IF EXISTS (SELECT LOWER(LTRIM(RTRIM(ClientCode))) FROM dbo.ClientConfigs
               GROUP BY LOWER(LTRIM(RTRIM(ClientCode))) HAVING COUNT(*) > 1)
    BEGIN
        ;THROW 50001, 'Two clients would end up with the same lower-case ClientCode. Rename one first.', 1;
    END

    UPDATE dbo.ClientConfigs SET ClientCode = LOWER(LTRIM(RTRIM(ClientCode)))
    WHERE ClientCode <> LOWER(LTRIM(RTRIM(ClientCode))) COLLATE Latin1_General_CS_AS;
    PRINT CONCAT('Client codes lower-cased: ', @@ROWCOUNT);

    /* ---------- Checks (read-only) ---------- */
    EXEC sp_executesql N'
        SELECT c.ClientCode, ISNULL(w.Channel, ''(any)'') AS Channel, COUNT(*) AS EnabledMessageWorkflows
        INTO #Clash
        FROM dbo.WorkflowDefinitions w JOIN dbo.ClientConfigs c ON c.Id = w.ClientConfigId
        WHERE w.IsEnabled = 1 AND w.TriggerType = 3
        GROUP BY c.ClientCode, w.Channel HAVING COUNT(*) > 1;
        IF EXISTS (SELECT 1 FROM #Clash)
        BEGIN
            PRINT ''WARNING: these clients have more than one enabled message workflow on the same channel.'';
            PRINT ''         Only the oldest one answers. Disable the extras in Admin.'';
            SELECT * FROM #Clash;
        END';

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
