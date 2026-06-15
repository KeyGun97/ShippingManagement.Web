# Shipping Management System — Web Edition (V2)

ASP.NET Core 8 MVC + Razor + Dapper + MS SQL Server rebuild of the original WinForms
application, implementing the **V2 requirement document** in full.

---

## 1. Quick Start

### Prerequisites
- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2016+ (Express / Developer / LocalDB all fine)
- Visual Studio 2022 (17.8+) or `dotnet` CLI

### Steps
1. **Create the database** — run `Database/ShippingDB_Web.sql` in SSMS.
   It creates the `ShippingDB_Web` database, all tables/views, seeds the
   **36 vessel types** from the spec and a starter country list with Asia flags.

   > ⚠️ **Note:** your RAR contained only `ShippingDataBase.sql.lnk` — a Windows
   > *shortcut*, not the actual SQL file. The schema here was rebuilt from the old
   > application's repository code (`Shippingrepository.cs`) plus the V2 spec, and
   > is a superset of the original (Vessels, Companies, ArrivalLog, vw_ArrivalDetail
   > are all preserved with the same column names so old data can be migrated 1:1).

2. **Set the connection string** in `appsettings.json`:
   ```json
   "ConnectionStrings": { "ShippingDB": "Server=localhost;Database=ShippingDB_Web;Trusted_Connection=True;TrustServerCertificate=True;" }
   ```

3. **Run**
   ```
   dotnet restore
   dotnet run
   ```
   or open `ShippingManagement.Web.csproj` in Visual Studio and press F5.
   (NuGet restore needs internet access for: Dapper, Microsoft.Data.SqlClient, ClosedXML.)

4. **Log in** — a default administrator is auto-seeded on first run:
   | Username | Password |
   |---|---|
   | `admin` | `Admin@123` |
   Change it immediately via **Admin → Users → Reset Password**.

---

## 2. User Session Logic (applies to every component)

Per the requirement "*put User session logic on each component*":

- A **global `SessionAuthorizeFilter`** (registered in `Program.cs`) runs before **every**
  controller action in the app. No page, AJAX endpoint, or export can be reached
  without a valid logged-in session — there is no need to remember to decorate
  individual controllers.
- Session holds `UserID`, `Username`, `FullName`, `Role` (sliding **30-minute**
  timeout, configurable in `appsettings.json` → `Session:TimeoutMinutes`;
  HttpOnly cookie `.ShippingMgmt.Session`).
- Expired/missing session → redirect to `/Account/Login?returnUrl=…`;
  AJAX calls instead get **401 JSON** and the layout's global handler bounces the
  user to the login page.
- `[RequireAdmin]` additionally guards admin modules (Users, Ports Setup,
  Port Assignments, Vessel Types, Upload). Non-admins get an *Access Denied* page.
- `[AllowAnonymousSession]` is applied only to Login and the error page.
- Passwords are stored as **PBKDF2** hashes (100k iterations, per-user salt).
- Every saved arrival records `EnteredBy` (the session user) for auditing.

---

## 3. Module map vs. the V2 spec

| V2 Requirement | Where |
|---|---|
| Vessel registration keyed by IMO; **IMO + Tab** auto-fills existing data | Vessels → Register (AJAX `FetchByImo`) |
| Company autofill **read-only** + "Add Company" option | Register page company panel + modal |
| Confirm / Purchase / Catering emails everywhere incl. exports | Vessel form, Daily Report, Master Data, all Excel/CSV exports |
| Company form + **automatic fleet count** | Companies (count = live `COUNT(*)` per company, never typed) |
| **Status button** Regular / Non-Regular per company | Companies list + Details |
| Regular customers **highlighted blue system-wide** | `.regular-customer` CSS class on every grid + blue rows in Excel exports |
| Dedicated **Regular Customers** dashboard | Regular Customers menu |
| Search by company / vessel / IMO / port / country / type | Vessels, Master Data filters |
| **Ports Setup**: Country → Port → multiple URLs, pagination, first-50-pages rule | Admin → Ports Setup (+ Sources page, `MaxPages` default 50) |
| **Port Assignment**: assigned user shown beside port, duplicates impossible | Admin → Port Assignments (DB `UNIQUE` constraint on PortID) |
| **Auto Data** button distributing rows to users by assigned ports | Port Assignments / Import Data header |
| Per-user **Import Data** + **Useless** button (real-time highlight, IMO-based, auto-excluded when filtered) | Import Data (AJAX toggle; IMO goes to global `UselessVessels` list) |
| **Date-wise history saving** | "Save Filtered to History" → `ArrivalLog` |
| **Daily Report**: country + date (default today), view-only, single-sheet or **Asia / Non-Asia two-sheet** export by company address country | Daily Report (+ Print view) |
| **Master Data**: country-wise, company-wise counts, IMO history, vessel search, date filter, **ALL** option | Master Data (+ History page) |
| 36 vessel types | Seeded by SQL script; manageable in Admin → Vessel Types |
| Exports Excel / CSV / PDF | ClosedXML Excel, CSV download, PDF via the Print view (browser → *Print → Save as PDF*) |

---

## 4. Design decisions & caveats

- **Web scraping — "Load Data" button**: Admin → Import Data → **Load Data** runs the
  Python/Selenium scraper at `Scripts/scraper.py` (a generalized version of the original
  MyShipTracking script). Flow:
  1. The app collects **all active URLs** from Ports Setup → Data Sources
     (optionally filtered by the country selected on the page) and writes them as a
     config JSON, including each source's `{page}` pattern, Start/End page and the
     port's Max Pages cap (first-50-pages rule).
  2. It launches `python scraper.py config.json output.json`. The script scrapes each
     URL headlessly (Chrome), keeps only **recent** rows (`Now / min / h / m / 4 d` —
     same filter as the original script), and writes a combined JSON file.
  3. The app reads that JSON, auto-detects missing IMOs by vessel name, inserts the
     rows into `ScrapedData` (known-useless IMOs are auto-flagged), and the data
     appears in the Import Data table. Run **Auto Data** to distribute it to users.

  **Server prerequisites** for Load Data: Python 3 on PATH (or set
  `Scraper:PythonPath` in `appsettings.json`), `pip install selenium`, and Google
  Chrome — Selenium 4.6+ downloads the matching chromedriver automatically.
  `Scraper:TimeoutMinutes` (default 15) kills runaway scrapes. The manual
  **Upload Rows** page remains available as a fallback when scraping is blocked.
- **PDF export** uses the print-optimized view + browser "Save as PDF" — zero extra
  dependencies, identical layout to the on-screen report.
- This package was authored in an offline environment, so it ships as **source only**
  (no `bin/obj`); restore + build it once in Visual Studio. Only 3 NuGet packages
  are referenced.

## 5. Project structure
```
ShippingManagementWeb/
├─ Database/ShippingDB_Web.sql      ← run this first
├─ Program.cs                       ← session pipeline + admin seeding
├─ Infrastructure/                  ← session filter, attributes, PBKDF2 hasher
├─ Models/  Data/  Services/        ← POCOs, Dapper repository, ClosedXML exports
├─ Controllers/                     ← Account, Home, Users, Vessels, Companies,
│                                     RegularCustomers, VesselTypes, PortsSetup,
│                                     PortAssignments, ImportData, DailyReport, MasterData
├─ Views/                           ← Razor views per module
└─ wwwroot/css/site.css             ← theme + regular/useless highlighting
```
