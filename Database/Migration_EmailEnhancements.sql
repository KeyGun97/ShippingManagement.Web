/* ============================================================
   Migration: Email enhancements
     1) Adds EmailLog.SentVia  — records which sending account
        (Google / Hotmail / …) a message went out through.
     2) Creates dbo.EmailTemplates — reusable subject/body presets
        that the Auto Emails compose form can load from a dropdown.

   Safe to run repeatedly on an existing ShippingDB. Run once in
   SSMS against your ShippingDB database (after Migration_AddEmailLog.sql).
   ============================================================ */
USE ShippingDB;
GO

/* ── 1) EmailLog.SentVia ─────────────────────────────────────── */
IF COL_LENGTH('dbo.EmailLog', 'SentVia') IS NULL
BEGIN
    ALTER TABLE dbo.EmailLog ADD SentVia NVARCHAR(60) NULL;
    PRINT 'Added column dbo.EmailLog.SentVia.';
END
ELSE
    PRINT 'Column dbo.EmailLog.SentVia already exists - skipped.';
GO

/* ── 2) EmailTemplates ───────────────────────────────────────── */
IF OBJECT_ID('dbo.EmailTemplates') IS NULL
BEGIN
    CREATE TABLE dbo.EmailTemplates (
        TemplateID  INT IDENTITY(1,1) PRIMARY KEY,
        Name        NVARCHAR(120) NOT NULL,
        Category    NVARCHAR(40)  NULL,                    -- NULL = applies to any category
        Subject     NVARCHAR(300) NOT NULL,
        Body        NVARCHAR(MAX) NOT NULL,
        IsHtml      BIT NOT NULL DEFAULT 0,
        CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2 NULL
    );
    PRINT 'Created table dbo.EmailTemplates.';

    -- A couple of starter templates so the dropdown is not empty.
    INSERT INTO dbo.EmailTemplates (Name, Category, Subject, Body, IsHtml) VALUES
    ('Confirmation request', 'Confirm',
     'Confirm – {Vessel} (IMO {IMO}) arriving {Port}',
     'Dear {Company},' + CHAR(13)+CHAR(10) + CHAR(13)+CHAR(10) +
     'Please confirm the arrival of vessel {Vessel} (IMO {IMO}) at {Port} on {Date}.' + CHAR(13)+CHAR(10) + CHAR(13)+CHAR(10) +
     'Regards,' + CHAR(13)+CHAR(10) + 'Operations Team', 0),
    ('Catering enquiry', 'Catering',
     'Catering supplies for {Vessel} at {Port}',
     'Dear {Company},' + CHAR(13)+CHAR(10) + CHAR(13)+CHAR(10) +
     'We can supply catering provisions for {Vessel} (IMO {IMO}) during its call at {Port} on {Date}.' + CHAR(13)+CHAR(10) +
     'Please share your requirements at your earliest convenience.' + CHAR(13)+CHAR(10) + CHAR(13)+CHAR(10) +
     'Regards,' + CHAR(13)+CHAR(10) + 'Operations Team', 0);
    PRINT 'Seeded 2 starter templates.';
END
ELSE
    PRINT 'Table dbo.EmailTemplates already exists - skipped.';
GO
