/* =====================================================================
   Migration — run this ONCE on your EXISTING ShippingDB database.
   (New databases created from ShippingDB_Web.sql already include this.)

   1. Adds ScrapedData.IsSaved — status flag set when a row is saved
      into the ArrivalLog (master data / date-wise history).
   2. Cleans the UselessVessels list: removes junk IMO entries
      ('---', '0', blanks, non-7-digit values). These junk values were
      shared by many scraped rows and caused unrelated records to be
      auto-marked useless on Load Data.
   3. Un-flags ScrapedData rows that were wrongly marked useless by
      those junk IMO entries (rows whose IMO is invalid but flagged,
      and that were never manually saved).
   ===================================================================== */

USE ShippingDB;   -- adjust if your database name differs
GO

-- 1) IsSaved status column
IF COL_LENGTH('dbo.ScrapedData', 'IsSaved') IS NULL
BEGIN
    ALTER TABLE dbo.ScrapedData ADD IsSaved BIT NOT NULL CONSTRAINT DF_Scraped_IsSaved DEFAULT 0;
    PRINT 'Added ScrapedData.IsSaved';
END
GO

-- Backfill: rows whose IMO+Port already exist in ArrivalLog count as saved
UPDATE s SET s.IsSaved = 1
FROM dbo.ScrapedData s
WHERE s.IsSaved = 0
  AND s.IMO_Number IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.ArrivalLog al
              WHERE al.IMO_Number = s.IMO_Number
                AND al.ArrivalDate = s.ImportDate
                AND al.PortName    = s.PortName);
PRINT CONCAT(@@ROWCOUNT, ' existing row(s) backfilled as Saved');
GO

-- 2) Remove junk IMOs from the global useless list (a real IMO is exactly 7 digits)
DELETE FROM dbo.UselessVessels
WHERE IMO_Number IS NULL
   OR LEN(LTRIM(RTRIM(IMO_Number))) <> 7
   OR IMO_Number LIKE '%[^0-9]%';
PRINT CONCAT(@@ROWCOUNT, ' junk entr(ies) removed from UselessVessels');
GO

-- 3) Un-flag rows that were wrongly auto-marked useless via junk IMOs.
--    A row stays useless only if its (valid) IMO is still in the cleaned list.
UPDATE s SET s.IsUseless = 0
FROM dbo.ScrapedData s
WHERE s.IsUseless = 1
  AND (s.IMO_Number IS NULL
       OR LEN(s.IMO_Number) <> 7
       OR s.IMO_Number LIKE '%[^0-9]%'
       OR NOT EXISTS (SELECT 1 FROM dbo.UselessVessels uv WHERE uv.IMO_Number = s.IMO_Number));
PRINT CONCAT(@@ROWCOUNT, ' wrongly-flagged row(s) un-marked as useless');
GO

PRINT 'Migration complete.';
