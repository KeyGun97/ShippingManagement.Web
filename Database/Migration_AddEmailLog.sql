/* ============================================================
   Migration: add the EmailLog table (Auto Emails module)
   Safe to run on an existing ShippingDB. Creates the table and
   its index only if they are missing. Run once in SSMS against
   your ShippingDB database.
   ============================================================ */
USE ShippingDB;
GO

IF OBJECT_ID('dbo.EmailLog') IS NULL
BEGIN
    CREATE TABLE dbo.EmailLog (
        EmailID     INT IDENTITY(1,1) PRIMARY KEY,
        Category    NVARCHAR(40)  NOT NULL,                    -- Confirm | Purchase | Catering | Generate | DeckEng | General
        ToAddress   NVARCHAR(400) NOT NULL,
        Subject     NVARCHAR(300) NULL,
        Body        NVARCHAR(MAX) NULL,
        IMO_Number  VARCHAR(15)   NULL,
        VesselName  NVARCHAR(150) NULL,
        CompanyName NVARCHAR(150) NULL,
        Status      NVARCHAR(20)  NOT NULL DEFAULT 'Sent',     -- 'Sent' | 'Failed' | 'Logged'
        ErrorText   NVARCHAR(500) NULL,
        SentBy      INT NULL REFERENCES dbo.Users(UserID),
        SentAt      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    PRINT 'Created table dbo.EmailLog.';
END
ELSE
    PRINT 'Table dbo.EmailLog already exists - skipped.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmailLog_SentAt' AND object_id = OBJECT_ID('dbo.EmailLog'))
BEGIN
    CREATE INDEX IX_EmailLog_SentAt ON dbo.EmailLog(SentAt DESC);
    PRINT 'Created index IX_EmailLog_SentAt.';
END
ELSE
    PRINT 'Index IX_EmailLog_SentAt already exists - skipped.';
GO
