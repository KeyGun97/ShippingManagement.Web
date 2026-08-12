using System.Globalization;
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
        private readonly ScrapeProgressService _progress;
        public ImportDataController(ShippingRepository repo, ScraperService scraper,
                                    ScrapeProgressService progress)
        { _repo = repo; _scraper = scraper; _progress = progress; }

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

        /// <summary>
        /// AJAX version of "Fetch Data": claims the single job slot, kicks the
        /// scrape off on a background task and returns immediately. The page then
        /// shows the animated loading overlay and polls LoadDataProgress for the
        /// live percentage + ETA. (The classic LoadData POST above is kept as a
        /// no-JS fallback.)
        /// </summary>
        [HttpPost, RequireAdmin, ValidateAntiForgeryToken]
        public IActionResult LoadDataStart(DateTime? date, string? country)
        {
            if (!_progress.TryStart())
                return Json(new { ok = false, message = "A data fetch is already running — please wait for it to finish." });

            var d = (date ?? DateTime.Today).Date;
            string? c = string.IsNullOrWhiteSpace(country) ? null : country;

            _ = Task.Run(() =>
            {
                try
                {
                    var result = _scraper.LoadData(d, c);
                    _progress.Complete(result.Ok, result.Message, result.Inserted);
                }
                catch (Exception ex)
                {
                    _progress.Complete(false, "Scrape crashed: " + ex.Message, 0);
                }
            });

            return Json(new { ok = true });
        }

        /// <summary>Polled every couple of seconds by the loading overlay.</summary>
        [HttpGet, RequireAdmin]
        public IActionResult LoadDataProgress() => Json(_progress.Snapshot());

        public IActionResult Index(DateTime? date, string? country, bool showUseless = false, bool applyFilter = false)
        {
            // If a background "Fetch Data" job just finished, show its outcome
            // banner exactly once (the overlay reloads this page on completion).
            if (_progress.TryConsumeResult(out bool jobOk, out string jobMsg) && !string.IsNullOrEmpty(jobMsg))
            {
                if (jobOk) TempData["Ok"] = jobMsg; else TempData["Error"] = jobMsg;
                // Render a dark full-screen mask on THIS response so the reload
                // after the overlay doesn't flash a white page mid-transition.
                ViewBag.ScrapeJustFinished = true;
            }
            ViewBag.ScrapeRunning = _progress.IsRunning;

            // Same culture-proof recovery as the Daily Report: if model binding could
            // not read the ISO date the browser sent, parse the raw query value rather
            // than silently falling back to today.
            date ??= DailyReportController.ParseDate(Request.Query["date"].FirstOrDefault());

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
        [HttpPost]//, ValidateAntiForgeryToken]
        public IActionResult SaveFiltered(DateTime date, int[]? selectedIds)
        {
            bool bySelection = selectedIds is { Length: > 0 };
            var (saved, duplicates, unregistered, noImo, noCompany) =
                _repo.SaveFilteredToArrivalLog(HttpContext.CurrentUserId(), date, selectedIds);

            TempData["Ok"] = bySelection
                ? $"{saved} of {selectedIds!.Length} selected row(s) saved to the {date:yyyy-MM-dd} history (useless/unmatched/duplicate rows skipped). Their status is now Saved."
                : $"{saved} record(s) saved to the {date:yyyy-MM-dd} history. Useless and unmatched rows were excluded.";

            // MANDATORY-FIELD VALIDATION: name every reason a row was rejected so the
            // user knows exactly what to complete rather than losing records silently.
            var problems = new List<string>();
            if (duplicates > 0)
                problems.Add($"{duplicates} row(s) were already in the {date:yyyy-MM-dd} history " +
                             "for the same port and were not added again.");
            if (noImo > 0)
                problems.Add($"{noImo} row(s) have NO IMO Number — double-click the IMO cell to set it.");
            if (unregistered > 0)
                problems.Add($"{unregistered} row(s) have an IMO that is not registered yet — " +
                             "open Vessels → Register (or click the IMO) to register them.");
            if (noCompany > 0)
                problems.Add($"{noCompany} row(s) belong to a vessel with NO Company linked — " +
                             "open the vessel and set its Company Name before saving.");

            if (problems.Count > 0)
                TempData["Error"] = "Not every row was added. " + string.Join(" ", problems);

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
            var rejected = new List<string>();   // vessel names skipped for a missing/invalid IMO
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

                // MANDATORY FIELD: a vessel must not be imported without an IMO Number.
                // Blank / junk IMOs are what produce the empty vessel records the
                // software has been collecting, so they are rejected here at the door.
                if (string.IsNullOrWhiteSpace(imo))
                {
                    rejected.Add(parts[0].Trim());
                    continue;
                }

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
            if (rows.Count == 0)
            {
                TempData["Error"] = "Nothing was imported — none of the pasted rows had a valid IMO Number. " +
                                    "Add the IMO in the second column (7 digits) and paste again.";
                return RedirectToAction(nameof(Upload));
            }

            _repo.InsertScrapedRows(rows);
            TempData["Ok"] = $"{rows.Count} row(s) imported for {port.PortName} ({importDate:yyyy-MM-dd}). Run Auto Data to distribute.";

            if (rejected.Count > 0)
            {
                var names = string.Join(", ", rejected.Take(10));
                TempData["Error"] = $"{rejected.Count} row(s) were SKIPPED because they have no IMO Number: {names}" +
                                    (rejected.Count > 10 ? ", …" : "") +
                                    ". Add their IMO and import those rows again.";
            }

            return RedirectToAction(nameof(Index), new { date = importDate });
        }
    }

    /* ════════════════════ DAILY REPORT ════════════════════ */
    public class DailyReportController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;

        public bool export = true;
        public DailyReportController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        /// <summary>
        /// Duplicate policy for the automatic de-duplication that runs when the report
        /// is shown. TRUE  = tag EVERY copy of a duplicated vessel (the vessel is fully
        ///                   excluded from tag-excluded exports) — current behaviour.
        /// FALSE = keep the earliest copy untagged and tag only the extra copies.
        /// This is the single switch to flip if the policy ever changes.
        /// </summary>
        private const bool TagEveryDuplicateCopy = true;

        public IActionResult Index(DateTime? dateFrom, DateTime? dateTo, DateTime? date, string? country,
                                   bool show = false, bool showDuplicates = false)
        {
            // ── DATE FILTER FIX ──────────────────────────────────────────────────
            // If model binding failed (culture mismatch on the host), the parameter
            // arrives as null even though the user DID pick a date — and the old code
            // then silently fell back to "today", so the range filter appeared dead.
            // Re-parse the raw query values explicitly before deciding anything.
            bool suppliedFrom = HasQuery(nameof(dateFrom)) || HasQuery(nameof(date));
            bool suppliedTo = HasQuery(nameof(dateTo)) || HasQuery(nameof(date));

            dateFrom ??= ParseDate(RawQuery(nameof(dateFrom)));
            dateTo ??= ParseDate(RawQuery(nameof(dateTo)));
            date ??= ParseDate(RawQuery(nameof(date)));

            // Backward-compatible: a single ?date= still works and seeds both ends of the range.
            var from = dateFrom ?? date ?? DateTime.Today;          // default = today (spec)
            var to = dateTo ?? date ?? from;                    // default = same day (single-day report)
            if (to < from) (from, to) = (to, from);                 // tolerate reversed input

            // Tell the user when a date was supplied but could not be understood,
            // instead of quietly showing today's rows and looking broken.
            if ((suppliedFrom && dateFrom is null && date is null) ||
                (suppliedTo && dateTo is null && date is null))
            {
                ViewBag.DateWarning =
                    "One of the dates could not be read and was replaced with a default. " +
                    "Please re-pick the From / To dates and press Show Report again.";
            }

            country = NullIfEmpty(country)?.Trim();

            ViewBag.DateFrom = from; ViewBag.DateTo = to;
            ViewBag.Country = country; ViewBag.Show = show;
            ViewBag.ShowDuplicates = showDuplicates;
            ViewBag.Countries = _repo.GetCountries().ToList();

            // Read EVERY matching row from the database. Nothing is deleted — the duplicates
            // stay stored in ArrivalLog; we only decide whether to *display* them here.
            var allRows = show
                ? _repo.GetArrivals(null, string.IsNullOrWhiteSpace(country) ? null : country,
                                    excludeTagged: false, dateFrom: from, dateTo: to).ToList()
                : new List<ArrivalLog>();

            // A "duplicate" = same IMO + same Vessel name (case/whitespace-insensitive).
            static string DupKey(ArrivalLog a) =>
                $"{(a.IMO_Number ?? "").Trim()}|{(a.VesselName ?? "").Trim().ToUpperInvariant()}";

            // LogIDs that share their IMO+Vessel with at least one other row (for highlighting).
            var duplicateLogIds = allRows
                .GroupBy(DupKey)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(a => a.LogID))
                .ToHashSet();

            ViewBag.TotalCount = allRows.Count;                 // every stored row in range
            ViewBag.UniqueCount = allRows.GroupBy(DupKey).Count();
            ViewBag.DuplicateCount = duplicateLogIds.Count;         // rows that are part of a dup group
            ViewBag.DuplicateLogIds = duplicateLogIds;

            /* ── AUTOMATIC DE-DUPLICATION ────────────────────────────────────────
               As soon as the report is shown, every row belonging to a duplicate
               group (same IMO + vessel name) is tagged, so duplicated vessels are
               removed from every tag-excluded export without the user having to
               press the manual "Tag duplicates" button.
               Only rows that are NOT already tagged are written, so re-showing the
               same report is a no-op and costs no database round-trip.
               See TagEveryDuplicateCopy for the keep-first vs tag-all policy.      */
            int autoTagged = 0;
            if (show && duplicateLogIds.Count > 0)
            {
                var candidates = TagEveryDuplicateCopy
                    ? allRows.Where(a => duplicateLogIds.Contains(a.LogID))
                    : allRows.GroupBy(DupKey)
                             .Where(g => g.Count() > 1)
                             .SelectMany(g => g.Skip(1));   // keep the earliest copy untagged

                var toTag = candidates.Where(a => !a.IsTagged).Select(a => a.LogID).ToList();

                if (toTag.Count > 0)
                {
                    autoTagged = _repo.SetTagStatus(toTag, true);

                    // Reflect the write in the rows we are about to render, otherwise the
                    // page would show them as untagged until the next refresh.
                    var tagged = toTag.ToHashSet();
                    foreach (var a in allRows.Where(a => tagged.Contains(a.LogID)))
                        a.IsTagged = true;
                }
            }
            ViewBag.AutoTagged = autoTagged;   // how many rows this request just tagged

            // Default = unique view (one row per IMO+Vessel, keeping the first/earliest).
            // "Show duplicates" button = every row, duplicates included.
            var rows = showDuplicates
                ? allRows
                : allRows.GroupBy(DupKey).Select(g => g.First()).ToList();

            return View(rows);
        }

        [HttpPost]
        public IActionResult ToggleTag(int logId, bool tagged)
        {
            _repo.UpdateTagStatus(logId, tagged);
            return Json(new { ok = true });
        }

        /* Bulk-tag duplicate rows (same IMO + Vessel name, case/whitespace-insensitive)
           across the selected date range. EVERY copy in a duplicate group is tagged
           (no row is kept), so a duplicated vessel is fully excluded from tag-excluded
           exports. Detection mirrors DailyReportController.Index. */
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult TagDuplicates(DateTime dateFrom, DateTime? dateTo, string? country,
                                           bool showDuplicates = false)
        {
            var (from, to) = Range(dateFrom, dateTo);

            var allRows = _repo.GetArrivals(null, NullIfEmpty(country),
                                            excludeTagged: false, dateFrom: from, dateTo: to).ToList();

            static string DupKey(ArrivalLog a) =>
                $"{(a.IMO_Number ?? "").Trim()}|{(a.VesselName ?? "").Trim().ToUpperInvariant()}";

            var toTag = allRows
                .GroupBy(DupKey)
                .Where(g => g.Count() > 1)     // only groups that actually have duplicates
                .SelectMany(g => g)            // tag every copy in the group
                .Where(r => !r.IsTagged)       // skip rows already tagged (avoids redundant writes)
                .Select(r => r.LogID)
                .ToList();

            int n = _repo.SetTagStatus(toTag, true);
            TempData[n > 0 ? "Ok" : "Error"] = n > 0
                ? $"Tagged {n} duplicate row(s) — every copy of each duplicated vessel."
                : "No untagged duplicate rows to tag in this range.";

            return RedirectToAction(nameof(Index),
                new { dateFrom = from, dateTo = to, country, show = true, showDuplicates });
        }

        public IActionResult ExportSingle(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t,export:true).ToList();
            return Xlsx(_export.DailyReportSingleSheet(rows), $"DailyReport_{Stamp(f, t)}.xlsx");
        }

        public IActionResult ExportTwoSheets(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true).ToList();
            return Xlsx(_export.DailyReportTwoSheets(rows), $"DailyReport_AsiaSplit_{Stamp(f, t)}.xlsx");
        }

        /// <summary>Port-Wise report: one worksheet per port for the selected date range.</summary>
        public IActionResult ExportPortWise(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true).ToList();
            return Xlsx(_export.PortWiseExcel(rows), $"PortWise_{Stamp(f, t)}.xlsx");
        }

        public IActionResult ExportPortWiseCsv(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true)
                            .OrderBy(r => r.PortName).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.ArrivalsCsv(rows)),
                        "text/csv", $"PortWise_{Stamp(f, t)}.csv");
        }

        /// <summary>Port-Wise PDF (browser print) — arrivals ordered by port.</summary>
        public IActionResult PortWisePrint(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            ViewBag.DateFrom = f; ViewBag.DateTo = t; ViewBag.Country = country;
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true)
                            .OrderBy(r => r.PortName).ToList();
            return View("Print", rows);
        }

        public IActionResult ExportCsv(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            var rows = _repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.ArrivalsCsv(rows)),
                        "text/csv", $"DailyReport_{Stamp(f, t)}.csv");
        }

        /// <summary>Print-friendly view (use the browser's Print → Save as PDF).</summary>
        public IActionResult Print(DateTime dateFrom, DateTime? dateTo, string? country)
        {
            var (f, t) = Range(dateFrom, dateTo);
            ViewBag.DateFrom = f; ViewBag.DateTo = t; ViewBag.Country = country;
            return View(_repo.GetArrivals(null, NullIfEmpty(country), excludeTagged: true, dateFrom: f, dateTo: t, export: true).ToList());
        }

        private static (DateTime from, DateTime to) Range(DateTime from, DateTime? to)
        {
            var t = to ?? from;
            return t < from ? (t, from) : (from, t);
        }

        /* ── Culture-proof date handling ─────────────────────────────────────────
           Used to recover a date the default model binder could not parse because
           the host's regional format differs from the ISO value the browser sends. */
        private bool HasQuery(string key) => Request.Query.ContainsKey(key) &&
                                             !string.IsNullOrWhiteSpace(Request.Query[key].FirstOrDefault());
        private string? RawQuery(string key) => Request.Query[key].FirstOrDefault();

        internal static DateTime? ParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();

            // The formats a browser date picker or a hand-edited URL can realistically produce.
            string[] formats =
            {
                "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
                "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy",
                "MM/dd/yyyy", "M/d/yyyy", "d/M/yyyy",
                "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"
            };

            if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var exact)) return exact.Date;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var inv)) return inv.Date;
            if (DateTime.TryParse(raw, CultureInfo.CurrentCulture,
                                  DateTimeStyles.None, out var cur)) return cur.Date;
            return null;
        }
        private static string Stamp(DateTime f, DateTime t) =>
            f.Date == t.Date ? $"{f:yyyyMMdd}" : $"{f:yyyyMMdd}-{t:yyyyMMdd}";

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
            ViewBag.CompanyCounts = _repo.GetTopCompaniesByFleet(20).ToList();   // TOP 20 in SQL, not all companies sorted in memory

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