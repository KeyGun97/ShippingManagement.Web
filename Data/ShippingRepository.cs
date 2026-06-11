using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using ShippingManagement.Web.Models;

namespace ShippingManagement.Web.Data
{
    public class ShippingRepository
    {
        private readonly string _cs;
        string server = $"{Environment.MachineName}";

        public ShippingRepository(IConfiguration cfg) =>
_cs = $"Server={server};Database=ShippingDB;Trusted_Connection=True;TrustServerCertificate=True";

        private IDbConnection Conn() => new SqlConnection(_cs);

        /* ── Users / Auth ──────────────────────────────────────────────── */
        public User? GetUserByUsername(string username)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<User>(
                "SELECT * FROM Users WHERE Username=@u AND IsActive=1", new { u = username });
        }
        public IEnumerable<User> GetAllUsers()
        {
            using var c = Conn();
            return c.Query<User>("SELECT * FROM Users ORDER BY Username");
        }
        public int CountUsers()
        {
            using var c = Conn();
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Users");
        }
        public void CreateUser(User u)
        {
            using var c = Conn();
            c.Execute(@"INSERT INTO Users (Username, PasswordHash, FullName, Role, IsActive)
                        VALUES (@Username, @PasswordHash, @FullName, @Role, @IsActive)", u);
        }
        public void SetUserActive(int id, bool active)
        {
            using var c = Conn();
            c.Execute("UPDATE Users SET IsActive=@a WHERE UserID=@id", new { a = active, id });
        }
        public void ResetPassword(int id, string hash)
        {
            using var c = Conn();
            c.Execute("UPDATE Users SET PasswordHash=@h WHERE UserID=@id", new { h = hash, id });
        }

        /* ── Vessels ───────────────────────────────────────────────────── */
        public Vessel? GetVesselByIMO(string imo)
        {
            const string sql = @"
                SELECT v.*, vt.TypeName AS VesselType, c.CompanyName, c.Status AS CustomerStatus
                FROM Vessels v
                LEFT JOIN VesselTypes vt ON vt.TypeID = v.VesselTypeID
                LEFT JOIN Companies  c ON c.CompanyID = v.CompanyID
                WHERE v.IMO_Number = @imo";
            using var c = Conn();
            return c.QueryFirstOrDefault<Vessel>(sql, new { imo });
        }

        public IEnumerable<Vessel> SearchVessels(string? term, int? companyId = null, string? country = null,
                                                 int? typeId = null, bool regularOnly = false)
        {
            const string sql = @"
                SELECT v.*, vt.TypeName AS VesselType, c.CompanyName, c.Status AS CustomerStatus
                FROM Vessels v
                LEFT JOIN VesselTypes vt ON vt.TypeID = v.VesselTypeID
                LEFT JOIN Companies  c ON c.CompanyID = v.CompanyID
                WHERE (@term IS NULL OR v.VesselName LIKE @like OR v.IMO_Number LIKE @like)
                  AND (@companyId IS NULL OR v.CompanyID = @companyId)
                  AND (@country IS NULL OR v.Country = @country OR v.Port LIKE '%'+@country+'%')
                  AND (@typeId IS NULL OR v.VesselTypeID = @typeId)
                  AND (@regOnly = 0 OR c.Status = 'Regular')
                ORDER BY v.VesselName";
            using var c = Conn();
            return c.Query<Vessel>(sql, new
            {
                term, like = $"%{term}%", companyId, country, typeId,
                regOnly = regularOnly ? 1 : 0
            });
        }

        public void SaveVessel(Vessel v)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM Vessels WHERE IMO_Number = @IMO_Number)
                    UPDATE Vessels SET
                        VesselName=@VesselName, VesselTypeID=@VesselTypeID, CallSign=@CallSign,
                        CompanyID=@CompanyID, Port=@Port, ETA=@ETA, Country=@Country,
                        Address=@Address, PhoneNo=@PhoneNo, Terms=@Terms,
                        ConfirmEmail=@ConfirmEmail, GenerateEmail=@GenerateEmail,
                        DeckEngEmail=@DeckEngEmail, CateringEmail=@CateringEmail,
                        PurchaseEmail=@PurchaseEmail, GeneralEmail=@GeneralEmail,
                        Status=@Status, UpdatedAt=SYSUTCDATETIME()
                    WHERE IMO_Number=@IMO_Number
                ELSE
                    INSERT INTO Vessels
                        (IMO_Number, VesselName, VesselTypeID, CallSign, CompanyID, Port, ETA,
                         Country, Address, PhoneNo, Terms, ConfirmEmail, GenerateEmail,
                         DeckEngEmail, CateringEmail, PurchaseEmail, GeneralEmail, Status)
                    VALUES
                        (@IMO_Number, @VesselName, @VesselTypeID, @CallSign, @CompanyID, @Port, @ETA,
                         @Country, @Address, @PhoneNo, @Terms, @ConfirmEmail, @GenerateEmail,
                         @DeckEngEmail, @CateringEmail, @PurchaseEmail, @GeneralEmail, @Status)";
            using var c = Conn();
            c.Execute(sql, v);
        }

        public string? LookupIMOByVesselName(string name)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<string>(
                "SELECT TOP 1 IMO_Number FROM Vessels WHERE VesselName=@name", new { name });
        }

        /* ── Companies ─────────────────────────────────────────────────── */
        public IEnumerable<Company> GetAllCompanies(string? term = null, bool regularOnly = false)
        {
            const string sql = @"
                SELECT * FROM vw_CompanyFleet
                WHERE (@term IS NULL OR CompanyName LIKE @like)
                  AND (@regOnly = 0 OR Status='Regular')
                ORDER BY CompanyName";
            using var c = Conn();
            return c.Query<Company>(sql, new { term, like = $"%{term}%", regOnly = regularOnly ? 1 : 0 });
        }

        public Company? GetCompanyByID(int id)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<Company>(
                "SELECT * FROM vw_CompanyFleet WHERE CompanyID=@id", new { id });
        }

        public Company? GetCompanyByName(string name)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<Company>(
                "SELECT * FROM vw_CompanyFleet WHERE CompanyName=@name", new { name });
        }

        public int SaveCompany(Company co)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM Companies WHERE CompanyID=@CompanyID AND @CompanyID > 0)
                BEGIN
                    UPDATE Companies SET CompanyName=@CompanyName, Address=@Address, Country=@Country,
                        GeneralEmail=@GeneralEmail, Website=@Website, Telephone=@Telephone
                    WHERE CompanyID=@CompanyID;
                    SELECT @CompanyID;
                END
                ELSE
                BEGIN
                    INSERT INTO Companies (CompanyName, Address, Country, GeneralEmail, Website, Telephone, Status)
                    VALUES (@CompanyName, @Address, @Country, @GeneralEmail, @Website, @Telephone, ISNULL(@Status,'Non-Regular'));
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                END";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, co);
        }

        public void SetCompanyStatus(int companyId, string status)
        {
            using var c = Conn();
            c.Execute("UPDATE Companies SET Status=@status WHERE CompanyID=@companyId",
                      new { status, companyId });
        }

        public IEnumerable<Vessel> GetVesselsByCompany(int companyId)
        {
            const string sql = @"
                SELECT v.*, vt.TypeName AS VesselType, c.CompanyName, c.Status AS CustomerStatus
                FROM Vessels v
                LEFT JOIN VesselTypes vt ON vt.TypeID = v.VesselTypeID
                LEFT JOIN Companies c ON c.CompanyID = v.CompanyID
                WHERE v.CompanyID=@companyId ORDER BY v.VesselName";
            using var c = Conn();
            return c.Query<Vessel>(sql, new { companyId });
        }

        /* ── Vessel Types ──────────────────────────────────────────────── */
        public IEnumerable<VesselType> GetVesselTypes()
        {
            using var c = Conn();
            return c.Query<VesselType>("SELECT * FROM VesselTypes ORDER BY TypeName");
        }
        public void AddVesselType(string name)
        {
            using var c = Conn();
            c.Execute("IF NOT EXISTS (SELECT 1 FROM VesselTypes WHERE TypeName=@name) INSERT INTO VesselTypes (TypeName) VALUES (@name)", new { name });
        }
        public void DeleteVesselType(int id)
        {
            using var c = Conn();
            c.Execute("DELETE FROM VesselTypes WHERE TypeID=@id AND NOT EXISTS (SELECT 1 FROM Vessels WHERE VesselTypeID=@id)", new { id });
        }

        /* ── Countries / Ports / Sources (Ports Setup) ─────────────────── */
        public IEnumerable<CountryItem> GetCountries()
        {
            using var c = Conn();
            return c.Query<CountryItem>("SELECT * FROM Countries ORDER BY CountryName");
        }
        public void AddCountry(string name, bool isAsia)
        {
            using var c = Conn();
            c.Execute("IF NOT EXISTS (SELECT 1 FROM Countries WHERE CountryName=@name) INSERT INTO Countries (CountryName, IsAsia) VALUES (@name, @isAsia)", new { name, isAsia });
        }

        public IEnumerable<Port> GetPorts(int? countryId = null)
        {
            const string sql = @"
                SELECT p.*, c.CountryName,
                       (SELECT COUNT(*) FROM PortSources s WHERE s.PortID = p.PortID) AS SourceCount,
                       pa.UserID AS AssignedUserID, u.FullName AS AssignedUserName
                FROM Ports p
                JOIN Countries c ON c.CountryID = p.CountryID
                LEFT JOIN PortAssignments pa ON pa.PortID = p.PortID
                LEFT JOIN Users u ON u.UserID = pa.UserID
                WHERE (@countryId IS NULL OR p.CountryID = @countryId)
                ORDER BY c.CountryName, p.PortName";
            using var c = Conn();
            return c.Query<Port>(sql, new { countryId });
        }

        public int SavePort(Port p)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM Ports WHERE PortID=@PortID AND @PortID > 0)
                BEGIN
                    UPDATE Ports SET PortName=@PortName, CountryID=@CountryID, Notes=@Notes, MaxPages=@MaxPages
                    WHERE PortID=@PortID; SELECT @PortID;
                END
                ELSE
                BEGIN
                    INSERT INTO Ports (PortName, CountryID, Notes, MaxPages)
                    VALUES (@PortName, @CountryID, @Notes, @MaxPages);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                END";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, p);
        }
        public void DeletePort(int id)
        {
            using var c = Conn();
            c.Execute("DELETE FROM Ports WHERE PortID=@id", new { id });
        }

        public IEnumerable<PortSource> GetPortSources(int portId)
        {
            using var c = Conn();
            return c.Query<PortSource>("SELECT * FROM PortSources WHERE PortID=@portId ORDER BY SourceName", new { portId });
        }
        public void SavePortSource(PortSource s)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM PortSources WHERE SourceID=@SourceID AND @SourceID > 0)
                    UPDATE PortSources SET SourceName=@SourceName, Url=@Url, PageParamPattern=@PageParamPattern,
                        StartPage=@StartPage, EndPage=@EndPage, IsActive=@IsActive
                    WHERE SourceID=@SourceID
                ELSE
                    INSERT INTO PortSources (PortID, SourceName, Url, PageParamPattern, StartPage, EndPage, IsActive)
                    VALUES (@PortID, @SourceName, @Url, @PageParamPattern, @StartPage, @EndPage, @IsActive)";
            using var c = Conn();
            c.Execute(sql, s);
        }
        public void DeletePortSource(int id)
        {
            using var c = Conn();
            c.Execute("DELETE FROM PortSources WHERE SourceID=@id", new { id });
        }

        /* ── Port Assignments ──────────────────────────────────────────── */
        public void AssignPort(int portId, int userId)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM PortAssignments WHERE PortID=@portId)
                    UPDATE PortAssignments SET UserID=@userId, AssignedAt=SYSUTCDATETIME() WHERE PortID=@portId
                ELSE
                    INSERT INTO PortAssignments (PortID, UserID) VALUES (@portId, @userId)";
            using var c = Conn();
            c.Execute(sql, new { portId, userId });
        }
        public void UnassignPort(int portId)
        {
            using var c = Conn();
            c.Execute("DELETE FROM PortAssignments WHERE PortID=@portId", new { portId });
        }

        /* ── Scraped Data (Import Data) ────────────────────────────────── */
        public IEnumerable<ScrapedRecord> GetScrapedData(int? userId, DateTime? importDate, string? country,
                                                         bool includeUseless = true)
        {
            const string sql = @"
                SELECT s.*, u.FullName AS AssignedUserName, c.Status AS CustomerStatus
                FROM ScrapedData s
                LEFT JOIN Users u ON u.UserID = s.AssignedUserID
                LEFT JOIN Vessels v ON v.IMO_Number = s.IMO_Number
                LEFT JOIN Companies c ON c.CompanyID = v.CompanyID
                WHERE (@userId IS NULL OR s.AssignedUserID = @userId)
                  AND (@importDate IS NULL OR s.ImportDate = @importDate)
                  AND (@country IS NULL OR s.Country = @country)
                  AND (@inclUseless = 1 OR s.IsUseless = 0)
                ORDER BY s.PortName, s.VesselName";
            using var c = Conn();
            return c.Query<ScrapedRecord>(sql, new
            { userId, importDate = importDate?.Date, country, inclUseless = includeUseless ? 1 : 0 });
        }

        public void InsertScrapedRows(IEnumerable<ScrapedRecord> rows)
        {
            const string sql = @"
                INSERT INTO ScrapedData
                    (VesselName, IMO_Number, PortID, PortName, Country, ArrivalDate, DepartureTime,
                     Origin, VesselStatus, DataSource, Deadweight, GrossTonnage, VesselBuilt,
                     VesselType, VesselSize, IsMatched, IsUseless, AssignedUserID, ImportDate)
                VALUES
                    (@VesselName, @IMO_Number, @PortID, @PortName, @Country, @ArrivalDate, @DepartureTime,
                     @Origin, @VesselStatus, @DataSource, @Deadweight, @GrossTonnage, @VesselBuilt,
                     @VesselType, @VesselSize, @IsMatched,
                     CASE WHEN @IMO_Number IS NOT NULL AND EXISTS (SELECT 1 FROM UselessVessels uv WHERE uv.IMO_Number=@IMO_Number) THEN 1 ELSE 0 END,
                     @AssignedUserID, @ImportDate)";
            using var c = Conn();
            c.Execute(sql, rows);
        }

        /// <summary>Marks a row useless and adds its IMO to the global ignore list (per V2).</summary>
        public void MarkUseless(int scrapeId, bool useless, int markedBy)
        {
            const string sql = @"
                UPDATE ScrapedData SET IsUseless=@useless WHERE ScrapeID=@scrapeId;
                IF @useless = 1
                BEGIN
                    DECLARE @imo VARCHAR(15) = (SELECT IMO_Number FROM ScrapedData WHERE ScrapeID=@scrapeId);
                    IF @imo IS NOT NULL AND NOT EXISTS (SELECT 1 FROM UselessVessels WHERE IMO_Number=@imo)
                        INSERT INTO UselessVessels (IMO_Number, MarkedBy) VALUES (@imo, @markedBy);
                END";
            using var c = Conn();
            c.Execute(sql, new { scrapeId, useless, markedBy });
        }

        public void SetScrapedIMO(int scrapeId, string imo)
        {
            using var c = Conn();
            c.Execute(@"UPDATE ScrapedData SET IMO_Number=@imo, IsMatched=1,
                        IsUseless = CASE WHEN EXISTS (SELECT 1 FROM UselessVessels WHERE IMO_Number=@imo) THEN 1 ELSE IsUseless END
                        WHERE ScrapeID=@scrapeId", new { scrapeId, imo });
        }

        /// <summary>Re-matches all unmatched scraped rows against registered vessels by name.</summary>
        public int AutoMatchScrapedRows(DateTime importDate)
        {
            const string sql = @"
                UPDATE s SET s.IMO_Number = v.IMO_Number, s.IsMatched = 1
                FROM ScrapedData s
                JOIN Vessels v ON v.VesselName = s.VesselName
                WHERE s.IMO_Number IS NULL AND s.ImportDate = @importDate;
                SELECT @@ROWCOUNT;";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, new { importDate = importDate.Date });
        }

        /// <summary>Auto Data: distribute today's unassigned scraped rows to users by their port assignments.</summary>
        public int DistributeData(DateTime importDate)
        {
            const string sql = @"
                UPDATE s SET s.AssignedUserID = pa.UserID
                FROM ScrapedData s
                JOIN Ports p  ON p.PortID = s.PortID OR (s.PortID IS NULL AND p.PortName = s.PortName)
                JOIN PortAssignments pa ON pa.PortID = p.PortID
                WHERE s.ImportDate = @importDate AND s.AssignedUserID IS NULL;
                SELECT @@ROWCOUNT;";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, new { importDate = importDate.Date });
        }

        /// <summary>Saves filtered (non-useless, matched-or-not) rows into the date-wise ArrivalLog history.</summary>
        public int SaveFilteredToArrivalLog(int userId, DateTime importDate)
        {
            const string sql = @"
                INSERT INTO ArrivalLog (IMO_Number, PortName, Country, ArrivalDate, IsTagged, EnteredBy)
                SELECT s.IMO_Number, s.PortName, s.Country, @importDate, 0, @userId
                FROM ScrapedData s
                WHERE s.ImportDate = @importDate
                  AND s.IsUseless = 0
                  AND s.AssignedUserID = @userId
                  AND s.IMO_Number IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM ArrivalLog al
                                  WHERE al.IMO_Number = s.IMO_Number
                                    AND al.ArrivalDate = @importDate
                                    AND al.PortName = s.PortName);
                SELECT @@ROWCOUNT;";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, new { userId, importDate = importDate.Date });
        }

        public IEnumerable<DateTime> GetImportDates()
        {
            using var c = Conn();
            return c.Query<DateTime>("SELECT DISTINCT ImportDate FROM ScrapedData ORDER BY ImportDate DESC");
        }

        /* ── Arrival Log / Reports ─────────────────────────────────────── */
        public IEnumerable<ArrivalLog> GetArrivals(DateTime? date, string? country, bool excludeTagged = false,
                                                   string? search = null, bool regularOnly = false, string? portName = null)
        {
            const string sql = @"
                SELECT * FROM vw_ArrivalDetail
                WHERE (@date IS NULL OR ArrivalDate = @date)
                  AND (@country IS NULL OR Country = @country)
                  AND (@portName IS NULL OR PortName = @portName)
                  AND (@exclTagged = 0 OR IsTagged = 0)
                  AND (@search IS NULL OR VesselName LIKE @like OR IMO_Number LIKE @like OR CompanyName LIKE @like)
                  AND (@regOnly = 0 OR CustomerStatus = 'Regular')
                ORDER BY CompanyName, VesselName";
            using var c = Conn();
            return c.Query<ArrivalLog>(sql, new
            {
                date = date?.Date, country, portName,
                exclTagged = excludeTagged ? 1 : 0,
                search, like = $"%{search}%",
                regOnly = regularOnly ? 1 : 0
            });
        }

        public IEnumerable<ArrivalLog> GetVesselHistory(string imo)
        {
            using var c = Conn();
            return c.Query<ArrivalLog>(
                "SELECT * FROM vw_ArrivalDetail WHERE IMO_Number=@imo ORDER BY ArrivalDate DESC", new { imo });
        }

        public void UpdateTagStatus(int logId, bool tagged)
        {
            using var c = Conn();
            c.Execute("UPDATE ArrivalLog SET IsTagged=@tagged WHERE LogID=@logId", new { tagged, logId });
        }

        public bool IsAsiaCountry(string? countryName)
        {
            if (string.IsNullOrWhiteSpace(countryName)) return false;
            using var c = Conn();
            return c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM Countries WHERE IsAsia=1 AND @n LIKE '%'+CountryName+'%'",
                new { n = countryName }) > 0;
        }

        /* ── Dashboard counters ────────────────────────────────────────── */
        public (int vessels, int companies, int regulars, int todayArrivals) GetDashboardCounts()
        {
            using var c = Conn();
            using var multi = c.QueryMultiple(@"
                SELECT COUNT(*) FROM Vessels;
                SELECT COUNT(*) FROM Companies;
                SELECT COUNT(*) FROM Companies WHERE Status='Regular';
                SELECT COUNT(*) FROM ArrivalLog WHERE ArrivalDate = CAST(GETDATE() AS DATE);");
            return (multi.ReadSingle<int>(), multi.ReadSingle<int>(), multi.ReadSingle<int>(), multi.ReadSingle<int>());
        }
        /// <summary>All active source URLs with their port + country — feeds the Python scraper ("Load Data").</summary>
        public IEnumerable<ScrapeSourceInfo> GetAllActiveSources(string? country = null)
        {
            const string sql = @"
                SELECT s.SourceID, s.SourceName, s.Url, s.PageParamPattern, s.StartPage, s.EndPage,
                       p.PortID, p.PortName, p.MaxPages, c.CountryName
                FROM PortSources s
                JOIN Ports p     ON p.PortID = s.PortID
                JOIN Countries c ON c.CountryID = p.CountryID
                WHERE s.IsActive = 1
                  AND (@country IS NULL OR c.CountryName = @country)
                ORDER BY c.CountryName, p.PortName, s.SourceName";
            using var c2 = Conn();
            return c2.Query<ScrapeSourceInfo>(sql, new { country });
        }
        public List<VesselType> GetAllVesselTypes()
        {
                using var conn = new SqlConnection(_cs);

                const string sql = @"
            SELECT
                TypeID,
                TypeName
            FROM VesselTypes
            ORDER BY TypeName";

                return conn.Query<VesselType>(sql).ToList();
        }

    }
}
