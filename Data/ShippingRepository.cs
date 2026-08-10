using Dapper;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.SqlClient;
using ShippingManagement.Web.Models;
using System.Data;
using System.Diagnostics.Metrics;

namespace ShippingManagement.Web.Data
{
    public class ShippingRepository
    {
        private readonly string _cs;
        string server = $"{Environment.MachineName}";
        public ShippingRepository(IConfiguration cfg) =>
            //_cs = cfg.GetConnectionString("ShippingDB")
            _cs = $"Server={server};Database=ShippingDB;Trusted_Connection=True;TrustServerCertificate=True";

        private IDbConnection Conn() => new SqlConnection(_cs);

        /* ── Tiny in-process TTL cache for lookup lists ─────────────────
           The repository is registered as a singleton, so this cache is
           shared by every request. Lookup tables (types, countries, ports,
           company names) change rarely but were being re-queried on every
           page load. Short TTL + explicit invalidation on writes. */
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Expires, object Value)> _cache = new();

        private T Cached<T>(string key, TimeSpan ttl, Func<T> factory)
        {
            if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
                return (T)hit.Value;
            var value = factory()!;
            _cache[key] = (DateTime.UtcNow.Add(ttl), value);
            return value;
        }
        private void Invalidate(params string[] keys)
        {
            foreach (var k in keys) _cache.TryRemove(k, out _);
        }

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
                                                 int? typeId = null, bool regularOnly = false, string? port = null)
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
                  AND (@port IS NULL OR v.Port = @port)
                  AND (@regOnly = 0 OR c.Status = 'Regular')
                ORDER BY v.VesselName";
            using var c = Conn();
            return c.Query<Vessel>(sql, new
            {
                term,
                like = $"%{term}%",
                companyId,
                country,
                typeId,
                port,
                regOnly = regularOnly ? 1 : 0
            });
        }

        /// <summary>
        /// Paged vessel search for the grid. Unlike SearchVessels (kept for
        /// exports), this (a) pages with OFFSET/FETCH so only one screen of
        /// rows travels over the wire, and (b) selects ONLY the columns the
        /// grid shows — the six NVARCHAR(400) email columns plus Address
        /// were roughly 80% of the old payload and the grid never displays them.
        /// </summary>
        public (List<Vessel> Rows, int Total) SearchVesselsPaged(
            string? term, int? companyId = null, string? country = null,
            int? typeId = null, bool regularOnly = false, string? port = null,
            int page = 1, int pageSize = 50)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 50;

            const string where = @"
                WHERE (@term IS NULL OR v.VesselName LIKE @like OR v.IMO_Number LIKE @like)
                  AND (@companyId IS NULL OR v.CompanyID = @companyId)
                  AND (@country IS NULL OR v.Country = @country OR v.Port LIKE '%'+@country+'%')
                  AND (@typeId IS NULL OR v.VesselTypeID = @typeId)
                  AND (@port IS NULL OR v.Port = @port)
                  AND (@regOnly = 0 OR c.Status = 'Regular')";

            const string sql = @"
                SELECT v.IMO_Number, v.VesselName, v.VesselTypeID, v.CallSign,
                       v.CompanyID, v.Port, v.Country, v.Terms, v.Status,
                       vt.TypeName AS VesselType, c.CompanyName, c.Status AS CustomerStatus
                FROM Vessels v
                LEFT JOIN VesselTypes vt ON vt.TypeID = v.VesselTypeID
                LEFT JOIN Companies  c ON c.CompanyID = v.CompanyID"
                + where + @"
                ORDER BY v.VesselName
                OFFSET (@page-1)*@pageSize ROWS FETCH NEXT @pageSize ROWS ONLY;

                SELECT COUNT(*)
                FROM Vessels v
                LEFT JOIN Companies c ON c.CompanyID = v.CompanyID"
                + where + ";";

            using var c = Conn();
            using var multi = c.QueryMultiple(sql, new
            {
                term,
                like = $"%{term}%",
                companyId,
                country,
                typeId,
                port,
                regOnly = regularOnly ? 1 : 0,
                page,
                pageSize
            });
            var rows = multi.Read<Vessel>().ToList();
            var total = multi.ReadSingle<int>();
            return (rows, total);
        }

        /// <summary>
        /// Lightweight (CompanyID, CompanyName) list for filter datalists.
        /// The vessel page previously queried vw_CompanyFleet (a GROUP BY
        /// join over the whole Vessels table) on EVERY load just to fill a
        /// dropdown. Cached for 60s and invalidated when a company is saved.
        /// </summary>
        public IReadOnlyList<Company> GetCompanyNameList() =>
            Cached("companyNames", TimeSpan.FromSeconds(60), () =>
            {
                using var c = Conn();
                return (IReadOnlyList<Company>)c.Query<Company>(
                    "SELECT CompanyID, CompanyName FROM Companies ORDER BY CompanyName").ToList();
            });

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
                         @DeckEngEmail, @CateringEmail, @PurchaseEmail, @GeneralEmail, @Status)

                 UPDATE ScrapedData
                    SET IsMatched = 1
                    WHERE IMO_Number = @IMO_Number";
            using var c = Conn();
            c.Execute(sql, v);
        }

        public void DeleteVessel(string imo)
        {
            // ArrivalLog has a FK on Vessels(IMO_Number) with no cascade, so its rows
            // for this IMO must go first or the vessel delete fails. We remove the
            // vessel together with its arrival-history log, and un-match any scraped
            // rows for that IMO (SaveVessel sets IsMatched=1; deleting flips it back
            // to 0 so the record can be re-registered later). XACT_ABORT + a wrapping
            // transaction make the whole thing atomic — all or nothing.
            const string sql = @"
                SET XACT_ABORT ON;
                BEGIN TRAN;
                    DELETE FROM ArrivalLog WHERE IMO_Number = @imo;
                    DELETE FROM Vessels    WHERE IMO_Number = @imo;
                    UPDATE ScrapedData SET IsMatched = 0 WHERE IMO_Number = @imo;
                COMMIT;";
            using var c = Conn();
            c.Execute(sql, new { imo });
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
            // Kept for exports (which legitimately need every row). Rewritten off
            // vw_CompanyFleet so the fleet count is a per-row seek instead of a
            // GROUP BY over the entire Vessels table before filtering.
            const string sql = @"
                SELECT c.CompanyID, c.CompanyName, c.Address, c.Country, c.GeneralEmail,
                       c.Website, c.Telephone, c.Status,
                       (SELECT COUNT(*) FROM Vessels v WHERE v.CompanyID = c.CompanyID) AS FleetCount
                FROM Companies c
                WHERE (@term IS NULL OR c.CompanyName LIKE @like)
                  AND (@regOnly = 0 OR c.Status='Regular')
                ORDER BY c.CompanyName";
            using var c = Conn();
            return c.Query<Company>(sql, new { term, like = $"%{term}%", regOnly = regularOnly ? 1 : 0 });
        }

        /// <summary>
        /// Paged company list for the grid. vw_CompanyFleet aggregates the WHOLE
        /// Vessels table (GROUP BY over every row) before anything is filtered —
        /// that cost grows with the vessel count, not the company count, which is
        /// why the page slowed down. Here we page the Companies table FIRST, then
        /// count the fleet only for the ~50 rows on screen (one index seek each
        /// against IX_Vessels_Company).
        /// </summary>
        public (List<Company> Rows, int Total) GetCompaniesPaged(
            string? term = null, bool regularOnly = false, int page = 1, int pageSize = 50)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 50;

            const string sql = @"
                SELECT c.CompanyID, c.CompanyName, c.Address, c.Country, c.GeneralEmail,
                       c.Website, c.Telephone, c.Status,
                       (SELECT COUNT(*) FROM Vessels v WHERE v.CompanyID = c.CompanyID) AS FleetCount
                FROM Companies c
                WHERE (@term IS NULL OR c.CompanyName LIKE @like)
                  AND (@regOnly = 0 OR c.Status = 'Regular')
                ORDER BY c.CompanyName
                OFFSET (@page-1)*@pageSize ROWS FETCH NEXT @pageSize ROWS ONLY;

                SELECT COUNT(*)
                FROM Companies c
                WHERE (@term IS NULL OR c.CompanyName LIKE @like)
                  AND (@regOnly = 0 OR c.Status = 'Regular');";

            using var c = Conn();
            using var multi = c.QueryMultiple(sql, new
            {
                term,
                like = $"%{term}%",
                regOnly = regularOnly ? 1 : 0,
                page,
                pageSize
            });
            var rows = multi.Read<Company>().ToList();
            var total = multi.ReadSingle<int>();
            return (rows, total);
        }

        // Single-company lookups: query the base table with a scalar sub-select
        // instead of vw_CompanyFleet, so one company never triggers a GROUP BY
        // over the entire Vessels table.
        private const string OneCompanySql = @"
            SELECT c.CompanyID, c.CompanyName, c.Address, c.Country, c.GeneralEmail,
                   c.Website, c.Telephone, c.Status,
                   (SELECT COUNT(*) FROM Vessels v WHERE v.CompanyID = c.CompanyID) AS FleetCount
            FROM Companies c ";

        public Company? GetCompanyByID(int id)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<Company>(OneCompanySql + "WHERE c.CompanyID=@id", new { id });
        }

        public Company? GetCompanyByName(string name)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<Company>(OneCompanySql + "WHERE c.CompanyName=@name", new { name });
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
            var id = c.ExecuteScalar<int>(sql, co);
            Invalidate("companyNames");
            return id;
        }

        /// <summary>How many vessels are still linked to this company (blocks a careless delete).</summary>
        public int GetCompanyVesselCount(int companyId)
        {
            using var c = Conn();
            return c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM Vessels WHERE CompanyID=@companyId", new { companyId });
        }

        /// <summary>
        /// Admin-only delete for a company added by mistake / no longer needed.
        /// Vessels.CompanyID is a FK with no cascade, so a company that still owns
        /// vessels cannot simply be removed. When <paramref name="unlinkVessels"/> is
        /// true the fleet is detached first (CompanyID → NULL, the vessels themselves
        /// are kept); otherwise the delete only succeeds for an empty company.
        /// Returns the number of vessels that were unlinked.
        /// </summary>
        public int DeleteCompany(int companyId, bool unlinkVessels)
        {
            const string sql = @"
                SET XACT_ABORT ON;
                BEGIN TRAN;
                    DECLARE @unlinked INT = 0;
                    IF @unlink = 1
                    BEGIN
                        UPDATE Vessels SET CompanyID = NULL WHERE CompanyID = @companyId;
                        SET @unlinked = @@ROWCOUNT;
                    END
                    DELETE FROM Companies WHERE CompanyID = @companyId;
                COMMIT;
                SELECT @unlinked;";
            using var c = Conn();
            return c.ExecuteScalar<int>(sql, new { companyId, unlink = unlinkVessels ? 1 : 0 });
        }

        public void SetCompanyStatus(int companyId, string status)
        {
            using var c = Conn();
            c.Execute("UPDATE Companies SET Status=@status WHERE CompanyID=@companyId",
                      new { status, companyId });
        }

        /// <summary>
        /// Top N companies by fleet size (Import Data sidebar). Previously the
        /// controller loaded EVERY company with its fleet count and sorted in
        /// memory just to show 20 — now the database returns 20 rows.
        /// </summary>
        public IEnumerable<Company> GetTopCompaniesByFleet(int top = 20) =>
            Cached($"topFleet{top}", TimeSpan.FromSeconds(60), () =>
            {
                using var c = Conn();
                return c.Query<Company>(@"
                    SELECT TOP (@top) c.CompanyID, c.CompanyName, c.Status,
                           COUNT(v.IMO_Number) AS FleetCount
                    FROM Companies c
                    LEFT JOIN Vessels v ON v.CompanyID = c.CompanyID
                    GROUP BY c.CompanyID, c.CompanyName, c.Status
                    ORDER BY COUNT(v.IMO_Number) DESC, c.CompanyName", new { top }).ToList();
            });

        /// <summary>
        /// All vessels belonging to Regular-status companies, in ONE query.
        /// The Regular Customers page used to call GetVesselsByCompany() once
        /// per company (classic N+1) — 200 regular customers meant 200 round
        /// trips. Caller groups the result by CompanyID.
        /// </summary>
        public IEnumerable<Vessel> GetVesselsOfRegularCompanies()
        {
            const string sql = @"
                SELECT v.IMO_Number, v.VesselName, v.VesselTypeID, v.CallSign,
                       v.CompanyID, v.Port, v.Country, v.Terms, v.Status,
                       vt.TypeName AS VesselType, c.CompanyName, c.Status AS CustomerStatus
                FROM Vessels v
                INNER JOIN Companies c ON c.CompanyID = v.CompanyID AND c.Status = 'Regular'
                LEFT JOIN VesselTypes vt ON vt.TypeID = v.VesselTypeID
                ORDER BY c.CompanyName, v.VesselName";
            using var c = Conn();
            return c.Query<Vessel>(sql);
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
        public IEnumerable<VesselType> GetVesselTypes() =>
            Cached("vesselTypes", TimeSpan.FromSeconds(60), () =>
            {
                using var c = Conn();
                return c.Query<VesselType>("SELECT * FROM VesselTypes ORDER BY TypeName").ToList();
            });
        public void AddVesselType(string name)
        {
            using var c = Conn();
            c.Execute("IF NOT EXISTS (SELECT 1 FROM VesselTypes WHERE TypeName=@name) INSERT INTO VesselTypes (TypeName) VALUES (@name)", new { name });
            Invalidate("vesselTypes");
        }
        public void DeleteVesselType(int id)
        {
            using var c = Conn();
            c.Execute("DELETE FROM VesselTypes WHERE TypeID=@id AND NOT EXISTS (SELECT 1 FROM Vessels WHERE VesselTypeID=@id)", new { id });
        }

        /* ── Countries / Ports / Sources (Ports Setup) ─────────────────── */
        public IEnumerable<CountryItem> GetCountries() =>
            Cached("countries", TimeSpan.FromSeconds(60), () =>
            {
                using var c = Conn();
                return c.Query<CountryItem>("SELECT * FROM Countries ORDER BY CountryName").ToList();
            });
        public void AddCountry(string name, bool isAsia)
        {
            using var c = Conn();
            c.Execute("IF NOT EXISTS (SELECT 1 FROM Countries WHERE CountryName=@name) INSERT INTO Countries (CountryName, IsAsia) VALUES (@name, @isAsia)", new { name, isAsia });
            Invalidate("countries");
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
            var id = c.ExecuteScalar<int>(sql, p);
            Invalidate("portNames");
            return id;
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
        public void SavePortSource(PortSource s)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM PortSources WHERE SourceID=@SourceID AND @SourceID > 0)
                    UPDATE PortSources SET PortID=@PortID, SourceName=@SourceName, Url=@Url, PageParamPattern=@PageParamPattern,
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
                SELECT s.*, u.FullName AS AssignedUserName, c.Status AS CustomerStatus,
                       c.CompanyName AS CompanyName, v.CompanyID AS VesselCompanyID
                FROM ScrapedData s
                LEFT JOIN Users u ON u.UserID = s.AssignedUserID
                LEFT JOIN Vessels v ON v.IMO_Number = s.IMO_Number
                LEFT JOIN Companies c ON c.CompanyID = v.CompanyID
                WHERE
                  s.VesselType IN (select distinct temp.TypeName from VesselTypes temp)
                  AND (@userId IS NULL OR s.AssignedUserID = @userId)
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
            // Skips rows that already exist with the same IMO + Port + Country (Load Data dedupe).
            // Rows without an IMO are deduped by VesselName + Port + Country instead.
            // The "useless" auto-flag only fires for VALID 7-digit IMOs so junk values
            // ('---', '0', blanks) shared by many rows can never mass-flag records.
            const string sql = @"
                INSERT INTO ScrapedData
                    (VesselName, IMO_Number, PortID, PortName, Country, ArrivalDate, DepartureTime,
                     Origin, VesselStatus, DataSource, Deadweight, GrossTonnage, VesselBuilt,
                     VesselType, VesselSize, IsMatched, IsUseless, AssignedUserID, ImportDate)
                SELECT
                    @VesselName, @IMO_Number, @PortID, @PortName, @Country, @ArrivalDate, @DepartureTime,
                     @Origin, @VesselStatus, @DataSource, @Deadweight, @GrossTonnage, @VesselBuilt,
                     @VesselType, @VesselSize, @IsMatched,
                     CASE WHEN @IMO_Number IS NOT NULL AND @IMO_Number NOT LIKE '%[^0-9]%' 
                     AND EXISTS (SELECT 1 FROM UselessVessels uv WHERE uv.IMO_Number=@IMO_Number)
                          THEN 1 ELSE 0 END,
                            @AssignedUserID, @ImportDate
                     WHERE NOT EXISTS (
                    SELECT 1 FROM ScrapedData d
                    WHERE d.PortName = @PortName
                      AND d.Country  = @Country
                      AND ((@IMO_Number IS NOT NULL AND d.IMO_Number = @IMO_Number)
                        OR (@IMO_Number IS NULL     AND d.IMO_Number IS NULL AND d.VesselName = @VesselName))
                      AND d.ImportDate = @ImportDate
                        AND @VesselType IN (select distinct temp.TypeName from VesselTypes temp)
                        AND @VesselType is not null )";
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
        /// <summary>
        /// Saves the user's filtered rows into the date-wise ArrivalLog history.
        /// A row is only allowed through when it carries BOTH mandatory pieces of
        /// information — an IMO Number, and a registered vessel that is linked to a
        /// Company. Everything else is counted and reported back so the Import Data
        /// page can tell the user exactly what is missing instead of silently
        /// dropping incomplete records.
        /// </summary>
        public (int Saved, int Duplicates, int Unregistered, int NoImo, int NoCompany) SaveFilteredToArrivalLog(
            int userId, DateTime importDate, IEnumerable<int>? selectedIds = null)
        {
            var ids = selectedIds?.Distinct().ToArray() ?? Array.Empty<int>();
            bool bySelection = ids.Length > 0;
            // setting date to delete data which is older then 3 days from import date so that the scrappeddata should be empted time to time as its not nessaccary to store data in scrappeddata table
            DateTime prevDate = importDate.AddDays(-3);
            // ── Why a table-valued parameter and not "ScrapeID IN @ids" ──────────
            // Dapper expands IN @ids into IN (@ids0, @ids1, … @idsN) — one SQL
            // parameter per id. SQL Server caps a request at 2100 parameters, so a
            // selection of ~2100+ rows threw:
            //     "The incoming request has too many parameters."
            // A TVP sends the ids as ONE parameter (a streamed rowset), so the
            // selection size no longer matters — 2,000 or 200,000 behaves the same.
            // It is also faster: the ids arrive as an indexed table the optimizer
            // can seek and join against, instead of a giant OR-list it must expand.
            //
            // Requires the dbo.IntIdList type — run Database/Migration_IntIdList.sql once.
            var idTable = new DataTable();
            idTable.Columns.Add("Id", typeof(int));

            foreach (var id in ids) idTable.Rows.Add(id);

            const string sql = @"
                SET NOCOUNT ON;
                SET XACT_ABORT ON;   -- any error rolls the whole thing back

                BEGIN TRANSACTION;

                DECLARE @savedRows TABLE (ScrapeID INT PRIMARY KEY);

                -- The candidate set the user asked to save (selection, or their whole
                -- assignment). IMO is normalised here ONCE: blank/whitespace becomes
                -- NULL so a '' IMO can't be counted as both 'no IMO' and 'unregistered'
                -- further down.
                DECLARE @candidates TABLE (ScrapeID INT PRIMARY KEY, IMO_Number VARCHAR(15));
                INSERT INTO @candidates (ScrapeID, IMO_Number)
                SELECT s.ScrapeID, NULLIF(LTRIM(RTRIM(s.IMO_Number)), '')
                FROM ScrapedData s
                WHERE s.ImportDate = @importDate
                  AND s.IsUseless = 0
                  AND ((@bySelection = 1 AND EXISTS (SELECT 1 FROM @ids i WHERE i.Id = s.ScrapeID))
                    OR (@bySelection = 0 AND s.AssignedUserID = @userId));

                INSERT INTO @savedRows (ScrapeID)
                SELECT c.ScrapeID
                FROM @candidates c
                WHERE c.IMO_Number IS NOT NULL
                  -- ArrivalLog.IMO_Number has an FK to Vessels: only rows whose vessel
                  -- is REGISTERED can be saved, otherwise the insert violates the FK.
                  AND EXISTS (SELECT 1 FROM Vessels v
                              WHERE v.IMO_Number = c.IMO_Number
                                AND v.CompanyID IS NOT NULL);

                -- Mandatory-field breakdown, so the UI can name what is missing.
                -- noImo / unregistered / noCompany are mutually exclusive and, together
                -- with @savedRows, account for every candidate. NOTE that the returned
                -- Saved is the ArrivalLog INSERT count, which is lower than
                -- COUNT(@savedRows) whenever a row was already in history — @duplicates
                -- below measures exactly that gap, so a 2,000 - row batch reconciles:
                -- candidates = Saved + Duplicates + NoImo + Unregistered + NoCompany
                DECLARE @noImo INT = (
                    SELECT COUNT(*) FROM @candidates c WHERE c.IMO_Number IS NULL);

            DECLARE @unregistered INT = (
                SELECT COUNT(*) FROM @candidates c
                WHERE c.IMO_Number IS NOT NULL
                      AND NOT EXISTS(SELECT 1 FROM Vessels v WHERE v.IMO_Number = c.IMO_Number));

            DECLARE @noCompany INT = (
                SELECT COUNT(*) FROM @candidates c
                WHERE c.IMO_Number IS NOT NULL
                      AND EXISTS(SELECT 1 FROM Vessels v
                                  WHERE v.IMO_Number = c.IMO_Number AND v.CompanyID IS NULL));

            INSERT INTO ArrivalLog(IMO_Number, PortName, Country, ArrivalDate, IsTagged, EnteredBy)
                SELECT s.IMO_Number, s.PortName, s.Country, @importDate, 0, @userId
                FROM ScrapedData s
                JOIN @savedRows sr ON sr.ScrapeID = s.ScrapeID
                WHERE NOT EXISTS(SELECT 1 FROM ArrivalLog al
                                  WHERE al.IMO_Number = s.IMO_Number
                                    AND al.ArrivalDate = @importDate
                                    AND al.PortName = s.PortName);

            DECLARE @inserted INT = @@ROWCOUNT;

            --Rows that were fully valid but already present in ArrivalLog for this
            -- date + port.Without this the user sees an unexplained shortfall
            --(1,800 of 2,100 saved) and assumes something broke.

            DECLARE @duplicates INT = (SELECT COUNT(*) FROM @savedRows) -@inserted;

            --status change in ScrapedData: these vessels are now in master data history
            UPDATE s SET s.IsSaved = 1
                FROM ScrapedData s JOIN @savedRows sr ON sr.ScrapeID = s.ScrapeID;

            COMMIT TRANSACTION;

            SELECT @inserted AS Inserted, @duplicates AS Duplicates,
                   @unregistered AS Unregistered,
                   @noImo AS NoImo, @noCompany AS NoCompany;
            ";

            using var c = Conn();
            var r = c.QuerySingle<(int Inserted, int Duplicates, int Unregistered, int NoImo, int NoCompany)>(sql, new
            {
                userId,
                importDate = importDate.Date,
                bySelection = bySelection ? 1 : 0,
                // One parameter regardless of how many ids — an empty TVP is legal,
                // so the old "new[] { -1 }" placeholder is no longer needed.
                ids = idTable.AsTableValuedParameter("dbo.IntIdList")
            }, commandTimeout: 300);
            DeletepreviousImportData(prevDate);
            return r;
        }

        public IEnumerable<DateTime> GetImportDates()
        {
            using var c = Conn();
            return c.Query<DateTime>("SELECT DISTINCT ImportDate FROM ScrapedData ORDER BY ImportDate DESC");
        }

        /* ── Arrival Log / Reports ─────────────────────────────────────── */
        //Change this is ShippingRepository-> GetArrivals
        public IEnumerable<ArrivalLog> GetArrivals(DateTime? date, string? country, bool excludeTagged = false,
                                                   string? search = null, bool regularOnly = false, string? portName = null,
                                                   DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            // @date keeps the original single-day behaviour for existing callers.
            // @dateFrom / @dateTo enable an (inclusive) date-range search for the Daily Report.
            // The upper bound is exclusive-next-midnight rather than "<= @dateTo" so the
            // range stays correct even if ArrivalDate ever carries a time component —
            // "<= 2026-07-24" would silently drop every row stamped later that day.
            //const string sql = @"
            //    SELECT * FROM vw_ArrivalDetail
            //    WHERE (@date IS NULL OR ArrivalDate = @date)
            //      AND (@dateFrom IS NULL OR ArrivalDate >= @dateFrom)
            //      AND (@dateTo IS NULL OR ArrivalDate < DATEADD(day, 1, @dateTo))
            //      AND (@country IS NULL OR LTRIM(RTRIM(Country)) = LTRIM(RTRIM(@country)))
            //      AND (@portName IS NULL OR PortName = @portName)
            //      AND (@exclTagged = 0 OR IsTagged = 0)
            //      AND (@search IS NULL OR VesselName LIKE @like OR IMO_Number LIKE @like OR CompanyName LIKE @like)
            //      AND (@regOnly = 0 OR CustomerStatus = 'Regular')
            //    ORDER BY ArrivalDate, CompanyName, VesselName";
            const string sql = @"WITH InRange AS (
                            SELECT *,
                                   COUNT(*) OVER (PARTITION BY IMO_Number) AS OccurrenceCount
                            FROM   vw_ArrivalDetail
                            WHERE   (@dateFrom IS NULL OR ArrivalDate >= @dateFrom)
                                      AND (@dateTo IS NULL OR ArrivalDate < DATEADD(day, 1, @dateTo))
                                      AND (@country IS NULL OR LTRIM(RTRIM(Country)) = LTRIM(RTRIM(@country)))
                                      AND (@portName IS NULL OR PortName = @portName)
                                      AND (@exclTagged = 0 OR IsTagged = 0)
                                      AND (@search IS NULL OR VesselName LIKE @like OR IMO_Number LIKE @like OR CompanyName LIKE @like)
                                      AND (@regOnly = 0 OR CustomerStatus = 'Regular')
                            )
                            SELECT r.*
                            FROM   InRange r
                            WHERE  OccurrenceCount = 1 AND (@date IS NULL OR ArrivalDate = @date)
                            ORDER BY ArrivalDate, VesselName";
            using var c = Conn();
            return c.Query<ArrivalLog>(sql, new
            {
                date = dateTo?.Date,
                dateFrom = dateFrom?.Date,
                dateTo = dateTo?.Date,
                country,
                portName,
                exclTagged = excludeTagged ? 1 : 0,
                search,
                like = $" %{search}%",
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

        /// Bulk set IsTagged for many rows in one round-trip (used by "Tag duplicates"
        /// on the Daily Report). Returns the number of rows updated.
        public int SetTagStatus(IEnumerable<int> logIds, bool tagged)
        {
            var ids = (logIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (ids.Count == 0) return 0;

            // Same 2100-parameter trap as SaveFilteredToArrivalLog: "LogID IN @ids"
            // becomes one parameter per id, so "Tag duplicates" over a wide date range
            // would fail once it matched 2100+ rows. TVP = one parameter, any size.
            var idTable = new DataTable();
            idTable.Columns.Add("Id", typeof(int));
            foreach (var id in ids) idTable.Rows.Add(id);

            using var c = Conn();
            return c.Execute(@"
                UPDATE al SET al.IsTagged = @tagged
                FROM ArrivalLog al
                JOIN @ids i ON i.Id = al.LogID",
                new { tagged, ids = idTable.AsTableValuedParameter("dbo.IntIdList") },
                commandTimeout: 300);
        }

        public bool IsAsiaCountry(string? countryName)
        {
            if (string.IsNullOrWhiteSpace(countryName)) return false;
            using var c = Conn();
            return c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM Countries WHERE IsAsia=1 AND @n LIKE '%'+CountryName+'%'",
                new { n = countryName }) > 0;
        }

        /* ── Auto Emails ───────────────────────────────────────────────── */
        public void InsertEmailLog(EmailLog e)
        {
            const string sql = @"
                INSERT INTO EmailLog (Category, ToAddress, Subject, Body, IMO_Number, VesselName,
                                      CompanyName, Status, ErrorText, SentBy, SentVia)
                VALUES (@Category, @ToAddress, @Subject, @Body, @IMO_Number, @VesselName,
                        @CompanyName, @Status, @ErrorText, @SentBy, @SentVia)";
            using var c = Conn();
            c.Execute(sql, e);
        }

        public IEnumerable<EmailLog> GetEmailLog(int top = 100)
        {
            using var c = Conn();
            return c.Query<EmailLog>(
                "SELECT TOP (@top) * FROM EmailLog ORDER BY SentAt DESC", new { top });
        }

        /* ── Email Templates (reusable subject/body presets) ───────────── */
        public IEnumerable<EmailTemplate> GetEmailTemplates(string? category = null)
        {
            using var c = Conn();
            return c.Query<EmailTemplate>(@"
                SELECT * FROM EmailTemplates
                WHERE (@category IS NULL OR Category IS NULL OR Category = @category)
                ORDER BY Name", new { category });
        }

        public EmailTemplate? GetEmailTemplate(int id)
        {
            using var c = Conn();
            return c.QueryFirstOrDefault<EmailTemplate>(
                "SELECT * FROM EmailTemplates WHERE TemplateID=@id", new { id });
        }

        public void AddEmailTemplate(EmailTemplate t)
        {
            const string sql = @"
                INSERT INTO EmailTemplates (Name, Category, Subject, Body, IsHtml)
                VALUES (@Name, @Category, @Subject, @Body, @IsHtml)";
            using var c = Conn();
            c.Execute(sql, t);
        }

        public void UpdateEmailTemplate(EmailTemplate t)
        {
            const string sql = @"
                UPDATE EmailTemplates
                   SET Name=@Name, Category=@Category, Subject=@Subject,
                       Body=@Body, IsHtml=@IsHtml, UpdatedAt=SYSUTCDATETIME()
                 WHERE TemplateID=@TemplateID";
            using var c = Conn();
            c.Execute(sql, t);
        }

        public void DeleteEmailTemplate(int id)
        {
            using var c = Conn();
            c.Execute("DELETE FROM EmailTemplates WHERE TemplateID=@id", new { id });
        }

        /// <summary>Distinct port names (for Port filters and Port-Wise reports).</summary>
        public IEnumerable<string> GetDistinctPortNames() =>
            Cached("portNames", TimeSpan.FromSeconds(60), () =>
            {
                using var c = Conn();
                return c.Query<string>(@"
                    SELECT PortName FROM Ports
                    UNION
                    SELECT DISTINCT PortName FROM ArrivalLog WHERE PortName IS NOT NULL
                    ORDER BY PortName").ToList();
            });

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
        public void DeletepreviousImportData(DateTime prevDate)
        {
            using var conn = new SqlConnection(_cs);
            const string sql = @"delete from ScrapedData  where ImportDate <= @prevDate";
            conn.Execute(sql, new { prevDate });
        }
    }
}