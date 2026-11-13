/* =====================================================================
   MadeeAutomation - 012_Beauty_Bookings.sql
   Run AFTER 011. Safe to run more than once.

   Appointments for beauty & wellness clients (spa, massage, nails, hair):
     ServiceItems     - services with duration, price, clean-up time, home service
     StaffMembers     - therapists / nail technicians / stylists, weekly day off, home service
     StaffServices    - who does which service
     Bookings         - the appointments (BK-0001 ...), with reminder tracking
     ClientConfigs    - booking settings (time steps, notice, days ahead, home service fee & travel, reminders)
     Conversations    - FlowJson (a booking being made in the chat)

   Food & retail clients are not touched. A client becomes a beauty client by setting its
   Vertical to BeautyWellness (5) in Admin -> Edit client.

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.InboundEvents', 'CustomerName') IS NULL
    BEGIN
        ;THROW 50012, 'Run 011_Phase5_Meta.sql first.', 1;
    END

    -- ---- Booking settings on the client ----
    IF COL_LENGTH('dbo.ClientConfigs', 'BookingSlotMinutes') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   BookingSlotMinutes int NOT NULL CONSTRAINT DF_ClientConfigs_BookingSlotMinutes DEFAULT (30),
                   BookingMinNoticeMinutes int NOT NULL CONSTRAINT DF_ClientConfigs_BookingMinNoticeMinutes DEFAULT (60),
                   BookingDaysAhead int NOT NULL CONSTRAINT DF_ClientConfigs_BookingDaysAhead DEFAULT (14),
                   HomeServiceEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_HomeServiceEnabled DEFAULT (0),
                   HomeServiceFee decimal(18,2) NULL,
                   HomeServiceTravelMinutes int NOT NULL CONSTRAINT DF_ClientConfigs_HomeServiceTravelMinutes DEFAULT (30),
                   BookingRemindersEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_BookingRemindersEnabled DEFAULT (1);');
        PRINT 'Added booking settings to ClientConfigs';
    END

    -- ---- A booking being made in the chat ----
    IF COL_LENGTH('dbo.Conversations', 'FlowJson') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Conversations ADD FlowJson nvarchar(max) NULL;');
        PRINT 'Added Conversations.FlowJson';
    END

    -- ---- Services ----
    IF OBJECT_ID('dbo.ServiceItems', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.ServiceItems (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_ServiceItems PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_ServiceItems_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            Name nvarchar(200) NOT NULL,
            Category nvarchar(100) NULL,
            Description nvarchar(500) NULL,
            DurationMinutes int NOT NULL,
            BufferMinutes int NOT NULL CONSTRAINT DF_ServiceItems_Buffer DEFAULT (0),
            Price decimal(18,2) NULL,
            HomeServiceAllowed bit NOT NULL CONSTRAINT DF_ServiceItems_Home DEFAULT (0),
            IsActive bit NOT NULL CONSTRAINT DF_ServiceItems_Active DEFAULT (1),
            SortOrder int NOT NULL CONSTRAINT DF_ServiceItems_Sort DEFAULT (0),
            CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_ServiceItems_Created DEFAULT (SYSUTCDATETIME()));
            CREATE INDEX IX_ServiceItems_ClientConfigId ON dbo.ServiceItems (ClientConfigId);');
        PRINT 'Created ServiceItems';
    END

    -- ---- Staff ----
    IF OBJECT_ID('dbo.StaffMembers', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.StaffMembers (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_StaffMembers PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_StaffMembers_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            Name nvarchar(100) NOT NULL,
            Title nvarchar(100) NULL,
            Gender nvarchar(1) NULL,
            DaysOffMask int NOT NULL CONSTRAINT DF_StaffMembers_DaysOff DEFAULT (0),
            DoesHomeService bit NOT NULL CONSTRAINT DF_StaffMembers_Home DEFAULT (0),
            IsActive bit NOT NULL CONSTRAINT DF_StaffMembers_Active DEFAULT (1),
            SortOrder int NOT NULL CONSTRAINT DF_StaffMembers_Sort DEFAULT (0),
            CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StaffMembers_Created DEFAULT (SYSUTCDATETIME()));
            CREATE INDEX IX_StaffMembers_ClientConfigId ON dbo.StaffMembers (ClientConfigId);');
        PRINT 'Created StaffMembers';
    END

    IF OBJECT_ID('dbo.StaffServices', 'U') IS NULL
    BEGIN
        -- Deleting a staff member or a service removes its rows here (NO ACTION on the second key avoids
        -- SQL Server''s "multiple cascade paths" error - the app deletes those rows first).
        EXEC(N'CREATE TABLE dbo.StaffServices (
            StaffMemberId uniqueidentifier NOT NULL CONSTRAINT FK_StaffServices_StaffMembers REFERENCES dbo.StaffMembers(Id) ON DELETE CASCADE,
            ServiceItemId uniqueidentifier NOT NULL CONSTRAINT FK_StaffServices_ServiceItems REFERENCES dbo.ServiceItems(Id),
            CONSTRAINT PK_StaffServices PRIMARY KEY (StaffMemberId, ServiceItemId));
            CREATE INDEX IX_StaffServices_ServiceItemId ON dbo.StaffServices (ServiceItemId);');
        PRINT 'Created StaffServices';
    END

    -- ---- Bookings ----
    IF OBJECT_ID('dbo.Bookings', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.Bookings (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_Bookings_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            BookingNumber int NOT NULL,
            Reference nvarchar(30) NOT NULL,
            Source nvarchar(10) NOT NULL,
            Channel nvarchar(20) NOT NULL,
            CustomerId nvarchar(200) NULL,
            CustomerName nvarchar(200) NULL,
            CustomerPhone nvarchar(30) NULL,
            ServiceItemId uniqueidentifier NULL,
            ServiceName nvarchar(200) NOT NULL,
            DurationMinutes int NOT NULL,
            Currency nvarchar(3) NULL,
            Price decimal(18,2) NULL,
            HomeServiceFee decimal(18,2) NULL,
            Total decimal(18,2) NULL,
            StaffMemberId uniqueidentifier NULL,
            StaffName nvarchar(100) NULL,
            StaffPreference nvarchar(10) NOT NULL,
            StartUtc datetime2 NOT NULL,
            EndUtc datetime2 NOT NULL,
            BusyFromUtc datetime2 NOT NULL,
            BusyUntilUtc datetime2 NOT NULL,
            LocalDate date NOT NULL,
            AtHome bit NOT NULL,
            HomeAddress nvarchar(500) NULL,
            CustomerNote nvarchar(1000) NULL,
            Status int NOT NULL,
            StatusChangedBy nvarchar(200) NULL,
            StatusChangedAtUtc datetime2 NULL,
            StatusReason nvarchar(300) NULL,
            Language nvarchar(40) NULL,
            CustomerNoticeSent bit NULL,
            CustomerNoticeNote nvarchar(500) NULL,
            ReminderDueUtc datetime2 NULL,
            ReminderSentAtUtc datetime2 NULL,
            ReminderNote nvarchar(300) NULL,
            CustomerConfirmedAtUtc datetime2 NULL,
            CreatedAtUtc datetime2 NOT NULL);
            CREATE UNIQUE INDEX IX_Bookings_ClientConfigId_BookingNumber ON dbo.Bookings (ClientConfigId, BookingNumber);
            CREATE INDEX IX_Bookings_ClientConfigId_LocalDate ON dbo.Bookings (ClientConfigId, LocalDate);
            CREATE INDEX IX_Bookings_StaffMemberId_BusyFromUtc ON dbo.Bookings (StaffMemberId, BusyFromUtc);
            CREATE INDEX IX_Bookings_Status_ReminderDueUtc ON dbo.Bookings (Status, ReminderDueUtc);
            CREATE INDEX IX_Bookings_ClientConfigId_Channel_CustomerId ON dbo.Bookings (ClientConfigId, Channel, CustomerId);');
        PRINT 'Created Bookings';
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
