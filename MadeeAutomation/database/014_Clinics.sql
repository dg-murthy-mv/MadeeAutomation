/* =====================================================================
   MadeeAutomation - 014_Clinics.sql
   Run AFTER 013. Safe to run more than once.

   Clinics (dental, skin, physio, general practice) - and extra appointment messages for spas too:
     StaffMembers     - ConsultationFee, Languages, Bio (doctor profile)
     StaffSessions    - each doctor's own weekly sessions (Mon 10:00-13:00 ...)
     Bookings         - PatientAge, VisitType (first / follow), FollowUpDate + FollowUpNote,
                        FeedbackRating + FeedbackComment + FeedbackAtUtc
     BookingMessages  - messages sent later about a booking: 2-hour reminder, follow-up, feedback question
     WaitlistEntries  - customers waiting for a time to open up
     ClientConfigs    - BookingSecondReminderEnabled, BookingFeedbackEnabled, BookingFeedbackDelayHours,
                        BookingWaitlistEnabled, WhatsApp template names and language

   No existing data is changed. Spas keep working exactly as before (the new options start off,
   except the waitlist, which only shows when no time is free).

   HOW TO RUN: as is first (@Commit = 0 = dry run), then with @Commit = 1.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;   -- 0 = dry run (roll back), 1 = keep changes

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID('dbo.AiUsage', 'U') IS NULL
    BEGIN
        ;THROW 50014, 'Run 013_Reports.sql first.', 1;
    END

    -- ---- Doctor profile ----
    IF COL_LENGTH('dbo.StaffMembers', 'ConsultationFee') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.StaffMembers ADD
                   ConsultationFee decimal(18,2) NULL,
                   Languages nvarchar(200) NULL,
                   Bio nvarchar(1000) NULL;');
        PRINT 'Added ConsultationFee / Languages / Bio to StaffMembers';
    END

    IF OBJECT_ID('dbo.StaffSessions', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.StaffSessions (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_StaffSessions PRIMARY KEY,
            StaffMemberId uniqueidentifier NOT NULL CONSTRAINT FK_StaffSessions_StaffMembers REFERENCES dbo.StaffMembers(Id) ON DELETE CASCADE,
            DayOfWeek int NOT NULL,           -- 0 = Sunday ... 6 = Saturday
            StartTime time(0) NOT NULL,
            EndTime time(0) NOT NULL);        -- earlier than StartTime = past midnight
            CREATE INDEX IX_StaffSessions_StaffMemberId ON dbo.StaffSessions (StaffMemberId);');
        PRINT 'Created StaffSessions';
    END

    -- ---- Booking: patient details, follow-up, feedback ----
    IF COL_LENGTH('dbo.Bookings', 'PatientAge') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.Bookings ADD
                   PatientAge int NULL,
                   VisitType nvarchar(10) NULL,
                   FollowUpDate date NULL,
                   FollowUpNote nvarchar(1000) NULL,
                   FeedbackRating int NULL,
                   FeedbackComment nvarchar(1000) NULL,
                   FeedbackAtUtc datetime2 NULL;');
        PRINT 'Added patient / follow-up / feedback columns to Bookings';
    END

    IF OBJECT_ID('dbo.BookingMessages', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.BookingMessages (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_BookingMessages PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL,
            BookingId uniqueidentifier NOT NULL CONSTRAINT FK_BookingMessages_Bookings REFERENCES dbo.Bookings(Id) ON DELETE CASCADE,
            Kind nvarchar(20) NOT NULL,       -- reminder2 / followup / feedback
            DueUtc datetime2 NOT NULL,
            SentAtUtc datetime2 NULL,
            Note nvarchar(300) NULL,
            CreatedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_BookingMessages_SentAtUtc_DueUtc ON dbo.BookingMessages (SentAtUtc, DueUtc);
            CREATE INDEX IX_BookingMessages_BookingId_Kind ON dbo.BookingMessages (BookingId, Kind);');
        PRINT 'Created BookingMessages';
    END

    IF OBJECT_ID('dbo.WaitlistEntries', 'U') IS NULL
    BEGIN
        EXEC(N'CREATE TABLE dbo.WaitlistEntries (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_WaitlistEntries PRIMARY KEY,
            ClientConfigId uniqueidentifier NOT NULL CONSTRAINT FK_WaitlistEntries_ClientConfigs REFERENCES dbo.ClientConfigs(Id) ON DELETE CASCADE,
            ServiceItemId uniqueidentifier NOT NULL,
            ServiceName nvarchar(200) NOT NULL,
            Preference nvarchar(10) NOT NULL,
            StaffMemberId uniqueidentifier NULL,
            StaffName nvarchar(100) NULL,
            LocalDate date NULL,
            Channel nvarchar(20) NOT NULL,
            CustomerId nvarchar(200) NOT NULL,
            CustomerName nvarchar(200) NULL,
            Language nvarchar(40) NULL,
            Status int NOT NULL,              -- 1 waiting, 2 offered, 3 booked, 4 closed
            OfferedAtUtc datetime2 NULL,
            Note nvarchar(300) NULL,
            CreatedAtUtc datetime2 NOT NULL);
            CREATE INDEX IX_WaitlistEntries_ClientConfigId_Status ON dbo.WaitlistEntries (ClientConfigId, Status);
            CREATE INDEX IX_WaitlistEntries_Status ON dbo.WaitlistEntries (Status);');
        PRINT 'Created WaitlistEntries';
    END

    -- ---- Settings ----
    IF COL_LENGTH('dbo.ClientConfigs', 'BookingSecondReminderEnabled') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.ClientConfigs ADD
                   BookingSecondReminderEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_BookingSecondReminderEnabled DEFAULT (0),
                   BookingFeedbackEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_BookingFeedbackEnabled DEFAULT (0),
                   BookingFeedbackDelayHours int NOT NULL CONSTRAINT DF_ClientConfigs_BookingFeedbackDelayHours DEFAULT (2),
                   BookingWaitlistEnabled bit NOT NULL CONSTRAINT DF_ClientConfigs_BookingWaitlistEnabled DEFAULT (1),
                   WhatsAppTemplateLanguage nvarchar(20) NULL,
                   WhatsAppTemplateReminder nvarchar(100) NULL,
                   WhatsAppTemplateFollowUp nvarchar(100) NULL,
                   WhatsAppTemplateFeedback nvarchar(100) NULL,
                   WhatsAppTemplateWaitlist nvarchar(100) NULL;');
        PRINT 'Added appointment message settings to ClientConfigs';
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
