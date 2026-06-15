using Microsoft.AspNetCore.Mvc;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Models;
using ShippingManagement.Web.Services;

namespace ShippingManagement.Web.Controllers
{
    /* ════════════════════ PORTS SETUP (Admin — Country → Port → Sources) ════════ */
    [RequireAdmin]
    public class PortsSetupController : Controller
    {
        private readonly ShippingRepository _repo;
        public PortsSetupController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index(int? countryId)
        {
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.CountryId = countryId;
            return View(_repo.GetPorts(countryId).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AddCountry(string countryName, bool isAsia = false)
        {
            if (!string.IsNullOrWhiteSpace(countryName)) _repo.AddCountry(countryName.Trim(), isAsia);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SavePort(Port port)
        {
            if (string.IsNullOrWhiteSpace(port.PortName) || port.CountryID <= 0)
                TempData["Error"] = "Port name and country are required.";
            else { _repo.SavePort(port); TempData["Ok"] = $"Port '{port.PortName}' saved."; }
            return RedirectToAction(nameof(Index), new { countryId = port.CountryID });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeletePort(int id, int? countryId)
        {
            _repo.DeletePort(id);
            return RedirectToAction(nameof(Index), new { countryId });
        }

        public IActionResult Sources(int portId)
        {
            var port = _repo.GetPorts().FirstOrDefault(p => p.PortID == portId);
            if (port is null) return NotFound();
            ViewBag.Port = port;
            return View(_repo.GetPortSources(portId).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveSource(PortSource source)
        {
            if (string.IsNullOrWhiteSpace(source.Url) || string.IsNullOrWhiteSpace(source.SourceName))
                TempData["Error"] = "Source name and URL are required.";
            else { _repo.SavePortSource(source); TempData["Ok"] = "Source saved."; }
            return RedirectToAction(nameof(Sources), new { portId = source.PortID });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult DeleteSource(int id, int portId)
        {
            _repo.DeletePortSource(id);
            return RedirectToAction(nameof(Sources), new { portId });
        }
    }

    /* ════════════════════ PORT ASSIGNMENTS (Admin) ════════════════════ */
    [RequireAdmin]
    public class PortAssignmentsController : Controller
    {
        private readonly ShippingRepository _repo;
        public PortAssignmentsController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index(int? countryId)
        {
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.Users = _repo.GetAllUsers().Where(u => u.IsActive).ToList();
            ViewBag.CountryId = countryId;
            // Ports list shows the assigned user's name beside each port → prevents duplicates (V2).
            return View(_repo.GetPorts(countryId).ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Assign(int portId, int userId, int? countryId)
        {
            if (userId <= 0) { _repo.UnassignPort(portId); TempData["Ok"] = "Port unassigned."; }
            else { _repo.AssignPort(portId, userId); TempData["Ok"] = "Port assigned."; }
            return RedirectToAction(nameof(Index), new { countryId });
        }

        /// <summary>V2 "Auto Data" button — distributes scraped rows to users by their port assignments.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult AutoData(DateTime? importDate, int? countryId)
        {
            var date = importDate ?? DateTime.Today;
            int matched = _repo.AutoMatchScrapedRows(date);
            int distributed = _repo.DistributeData(date);
            TempData["Ok"] = $"Auto Data complete for {date:yyyy-MM-dd}: {distributed} row(s) distributed to assigned users, {matched} IMO auto-match(es).";
            return RedirectToAction(nameof(Index), new { countryId });
        }
    }

    /* ════════════════════ IMPORT DATA ════════════════════ */
    public class ImportDataController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ScraperService _scraper;
        public ImportDataController(ShippingRepository repo, ScraperService scraper)
        { _repo = repo; _scraper = scraper; }

        /// <summary>
        /// V2 "Load Data" button (admin): collects every active URL from Ports Setup → Data Sources,
        /// runs the Python/Selenium scraper (Scripts/scraper.py) against them, then imports the
        /// resulting JSON file into ScrapedData so it appears in the Import Data table below.
        /// </summary>
        [HttpPost, RequireAdmin, ValidateAntiForgeryToken]
        public IActionResult LoadData(DateTime? date, string? country)
        {
            var d = (date ?? DateTime.Today).Date;
            var result = _scraper.LoadData(d, string.IsNullOrWhiteSpace(country) ? null : country);
            if (result.Ok) TempData["Ok"] = result.Message;
            else TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index), new { date = d, country });
        }

        public IActionResult Index(DateTime? date, string? country, bool showUseless = false, bool applyFilter = false)
        {
            var d = date ?? DateTime.Today;
            bool isAdmin = HttpContext.IsAdmin();
            // Users see ONLY rows distributed to them; Admin sees everything.
            int? userFilter = isAdmin ? null : HttpContext.CurrentUserId();
            // V2: when any filter is applied, useless rows are auto-excluded.
            bool includeUseless = showUseless && !applyFilter;

            ViewBag.Date = d; ViewBag.Country = country;
            ViewBag.ShowUseless = showUseless; ViewBag.ApplyFilter = applyFilter;
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.ImportDates = _repo.GetImportDates().ToList();
            return View(_repo.GetScrapedData(userFilter, d,
                string.IsNullOrWhiteSpace(country) ? null : country, includeUseless).ToList());
        }

        /// <summary>AJAX: toggle the V2 "Useless" button — row highlights in real time.</summary>
        [HttpPost]
        public IActionResult MarkUseless(int scrapeId, bool useless)
        {
            _repo.MarkUseless(scrapeId, useless, HttpContext.CurrentUserId());
            return Json(new { ok = true, scrapeId, useless });
        }

        /// <summary>AJAX: link a scraped row to an IMO (double-click → register → set IMO).</summary>
        [HttpPost]
        public IActionResult SetImo(int scrapeId, string imo)
        {
            var clean = new string((imo ?? "").Where(char.IsDigit).ToArray());
            if (clean.Length != 7) return Json(new { ok = false, message = "A valid IMO is exactly 7 digits." });
            _repo.SetScrapedIMO(scrapeId, clean);
            return Json(new { ok = true });
        }

        /// <summary>Saves the user's filtered, non-useless rows into date-wise ArrivalLog history.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SaveFiltered(DateTime date, int[]? selectedIds)
        {
            bool bySelection = selectedIds is { Length: > 0 };
            var (saved, unregistered) = _repo.SaveFilteredToArrivalLog(HttpContext.CurrentUserId(), date, selectedIds);
            TempData["Ok"] = bySelection
                ? $"{saved} of {selectedIds!.Length} selected row(s) saved to the {date:yyyy-MM-dd} history (useless/unmatched/duplicate rows skipped). Their status is now Saved."
                : $"{saved} record(s) saved to the {date:yyyy-MM-dd} history. Useless and unmatched rows were excluded.";
            if (unregistered > 0)
                TempData["Error"] = $"{unregistered} row(s) were NOT saved because their vessel is not registered in the database yet. " +
                                    "Open Vessels → Register (or double-click the IMO) to register them, then save again.";
            return RedirectToAction(nameof(Index), new { date });
        }

        /// <summary>Manual paste/CSV import of scraped rows (replaces in-app Selenium runs; see README).</summary>
        [HttpGet, RequireAdmin]
        public IActionResult Upload()
        {
            ViewBag.Ports = _repo.GetPorts().ToList();
            return View();
        }

        [HttpPost, RequireAdmin, ValidateAntiForgeryToken]
        public IActionResult Upload(int portId, DateTime importDate, string pastedRows, string dataSource = "Manual")
        {
            var port = _repo.GetPorts().FirstOrDefault(p => p.PortID == portId);
            if (port is null || string.IsNullOrWhiteSpace(pastedRows))
            {
                TempData["Error"] = "Select a port and paste at least one row.";
                return RedirectToAction(nameof(Upload));
            }

            var rows = new List<ScrapedRecord>();
            foreach (var line in pastedRows.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Expected columns (tab or comma): VesselName, IMO(optional), ArrivalDate, DepartureTime, Origin, Status
                var parts = line.Contains('\t') ? line.Split('\t') : line.Split(',');
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;
                string? imo = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;
                if (imo is not null)
                {
                    var digitsOnly = new string(imo.Where(char.IsDigit).ToArray());
                    imo = digitsOnly.Length == 7 ? digitsOnly : null;   // junk values ('---','0') -> null
                }
                imo ??= _repo.LookupIMOByVesselName(parts[0].Trim());   // auto IMO detection by vessel name (spec)
                rows.Add(new ScrapedRecord
                {
                    VesselName = parts[0].Trim(),
                    IMO_Number = imo,
                    IsMatched = _repo.GetVesselByIMO(imo) != null,//imo is not null,
                    PortID = port.PortID,
                    PortName = port.PortName,
                    Country = port.CountryName ?? "",
                    ArrivalDate = parts.Length > 2 ? parts[2].Trim() : null,
                    DepartureTime = parts.Length > 3 ? parts[3].Trim() : null,
                    Origin = parts.Length > 4 ? parts[4].Trim() : null,
                    VesselStatus = parts.Length > 5 ? parts[5].Trim() : null,
                    DataSource = dataSource,
                    ImportDate = importDate.Date
                });
            }
            _repo.InsertScrapedRows(rows);
            TempData["Ok"] = $"{rows.Count} row(s) imported for {port.PortName} ({importDate:yyyy-MM-dd}). Run Auto Data to distribute.";
            return RedirectToAction(nameof(Index), new { date = importDate });
        }
    }

    /* ════════════════════ DAILY REPORT ════════════════════ */
    public class DailyReportController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;
        public DailyReportController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        public IActionResult Index(DateTime? date, string? country, bool show = false)
        {
            var d = date ?? DateTime.Today;                 // default = today (spec)
            ViewBag.Date = d; ViewBag.Country = country; ViewBag.Show = show;
            ViewBag.Countries = _repo.GetCountries().ToList();
            var rows = show
                ? _repo.GetArrivals(d, string.IsNullOrWhiteSpace(country) ? null : country, excludeTagged: false).ToList()
                : new List<ArrivalLog>();
            return View(rows);
        }

        [HttpPost]
        public IActionResult ToggleTag(int logId, bool tagged)
        {
            _repo.UpdateTagStatus(logId, tagged);
            return Json(new { ok = true });
        }

        public IActionResult ExportSingle(DateTime date, string? country)
        {
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true).ToList();
            return Xlsx(_export.DailyReportSingleSheet(rows), $"DailyReport_{date:yyyyMMdd}.xlsx");
        }

        public IActionResult ExportTwoSheets(DateTime date, string? country)
        {
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true).ToList();
            return Xlsx(_export.DailyReportTwoSheets(rows), $"DailyReport_AsiaSplit_{date:yyyyMMdd}.xlsx");
        }

        /// <summary>Port-Wise report: one worksheet per port for the selected date.</summary>
        public IActionResult ExportPortWise(DateTime date, string? country)
        {
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true).ToList();
            return Xlsx(_export.PortWiseExcel(rows), $"PortWise_{date:yyyyMMdd}.xlsx");
        }

        public IActionResult ExportPortWiseCsv(DateTime date, string? country)
        {
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true)
                            .OrderBy(r => r.PortName).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.ArrivalsCsv(rows)),
                        "text/csv", $"PortWise_{date:yyyyMMdd}.csv");
        }

        /// <summary>Port-Wise PDF (browser print) — arrivals ordered by port.</summary>
        public IActionResult PortWisePrint(DateTime date, string? country)
        {
            ViewBag.Date = date; ViewBag.Country = country;
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true)
                            .OrderBy(r => r.PortName).ToList();
            return View("Print", rows);
        }

        public IActionResult ExportCsv(DateTime date, string? country)
        {
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.ArrivalsCsv(rows)),
                        "text/csv", $"DailyReport_{date:yyyyMMdd}.csv");
        }

        /// <summary>Print-friendly view (use the browser's Print → Save as PDF).</summary>
        public IActionResult Print(DateTime date, string? country)
        {
            ViewBag.Date = date; ViewBag.Country = country;
            return View(_repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true).ToList());
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private FileContentResult Xlsx(byte[] bytes, string name) =>
            File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    /* ════════════════════ MASTER DATA ════════════════════ */
    public class MasterDataController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;
        public MasterDataController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        public IActionResult Index(string? q, string? country, DateTime? date, bool all = false, bool regularOnly = false)
        {
            ViewBag.Q = q; ViewBag.Country = country; ViewBag.Date = date;
            ViewBag.All = all; ViewBag.RegularOnly = regularOnly;
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.CompanyCounts = _repo.GetAllCompanies().OrderByDescending(c => c.FleetCount).Take(20).ToList();

            List<ArrivalLog> rows;
            if (all)
                rows = _repo.GetArrivals(null, NullIfEmpty(country), search: NullIfEmpty(q), regularOnly: regularOnly).ToList();
            else if (date is not null || !string.IsNullOrWhiteSpace(q) || !string.IsNullOrWhiteSpace(country) || regularOnly)
                rows = _repo.GetArrivals(date, NullIfEmpty(country), search: NullIfEmpty(q), regularOnly: regularOnly).ToList();
            else
                rows = new List<ArrivalLog>();
            return View(rows);
        }

        /// <summary>IMO-based history tracking (spec).</summary>
        public IActionResult History(string imo)
        {
            ViewBag.Imo = imo;
            ViewBag.Vessel = _repo.GetVesselByIMO(imo);
            return View(_repo.GetVesselHistory(imo).ToList());
        }

        public IActionResult ExportExcel(string? q, string? country, DateTime? date, bool all = false, bool regularOnly = false)
        {
            var rows = _repo.GetArrivals(all ? null : date, NullIfEmpty(country),
                                         search: NullIfEmpty(q), regularOnly: regularOnly).ToList();
            return File(_export.DailyReportSingleSheet(rows, "Master Data"),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"MasterData_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
