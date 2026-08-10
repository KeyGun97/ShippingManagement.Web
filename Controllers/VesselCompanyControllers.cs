using Microsoft.AspNetCore.Mvc;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Models;
using ShippingManagement.Web.Services;

namespace ShippingManagement.Web.Controllers
{
    /* ════════════════════ VESSEL REGISTRATION ════════════════════ */
    public class VesselsController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;
        public VesselsController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        public IActionResult Index(string? q, int? companyId, string? companyName, int? typeId, string? country, bool regularOnly = false, string? port = null,
                                   int page = 1, int pageSize = 50)
        {
            // Allow filtering by company NAME (typed into the text box, or carried from the
            // Companies → fleet-count link). If an id wasn't supplied, resolve it from the name.
            Company? company = null;
            if (companyId is not null)
                company = _repo.GetCompanyByID(companyId.Value);
            else if (!string.IsNullOrWhiteSpace(companyName))
            {
                company = _repo.GetCompanyByName(companyName.Trim());
                companyId = company?.CompanyID;
            }
            // Show the resolved company name in the text box (falls back to whatever was typed).
            companyName = company?.CompanyName ?? companyName;

            // Cached, lightweight lookups. GetCompanyNameList() replaces the old
            // GetAllCompanies() call which queried vw_CompanyFleet (GROUP BY join
            // over the whole Vessels table) on every page load just to fill a datalist.
            ViewBag.Companies = _repo.GetCompanyNameList();
            ViewBag.Types = _repo.GetVesselTypes().ToList();
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.Ports = _repo.GetDistinctPortNames().ToList();
            ViewBag.Q = q; ViewBag.CompanyId = companyId; ViewBag.CompanyName = companyName; ViewBag.TypeId = typeId;
            ViewBag.Country = country; ViewBag.RegularOnly = regularOnly; ViewBag.Port = port;

            // Paged: only one screen of rows leaves the database (was: ALL vessels,
            // ALL columns, on every load — the main cause of the slow page).
            var (rows, total) = _repo.SearchVesselsPaged(string.IsNullOrWhiteSpace(q) ? null : q,
                                           companyId, country, typeId, regularOnly, NullIfEmpty(port),
                                           page, pageSize);
            ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Total = total;
            ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
            return View(rows);
        }

        [HttpGet]
        public IActionResult Register(string? imo = null, string? name = null,
                                      string? port = null, string? country = null,
                                      string? vesselType = null, string? origin = null,
                                      string? returnUrl = null)
        {
            var types = _repo.GetVesselTypes().ToList();
            ViewBag.Types = types;
            ViewBag.Companies = _repo.GetCompanyNameList().ToList();   // dropdown only — no fleet aggregate needed
            // Where to go back to after Save — e.g. the Import Data page that linked here.
            ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;

            Vessel model = imo is not null
                ? _repo.GetVesselByIMO(imo) ?? new Vessel { IMO_Number = imo, VesselName = name ?? "" }
                : new Vessel { VesselName = name ?? "" };

            // Pre-fill from imported/scraped details (e.g. opened from Import Data → IMO link).
            // Only fill blanks so an already-registered vessel's saved data is never overwritten.
            if (string.IsNullOrWhiteSpace(model.VesselName) && !string.IsNullOrWhiteSpace(name))
                model.VesselName = name!.Trim();
            if (string.IsNullOrWhiteSpace(model.Port) && !string.IsNullOrWhiteSpace(port))
                model.Port = port.Trim();
            if (string.IsNullOrWhiteSpace(model.Country) && !string.IsNullOrWhiteSpace(country))
                model.Country = country.Trim();
            if (model.VesselTypeID is null && !string.IsNullOrWhiteSpace(vesselType))
            {
                var match = types.FirstOrDefault(t =>
                    string.Equals(t.TypeName, vesselType.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null) { model.VesselTypeID = match.TypeID; model.VesselType = match.TypeName; }
            }
            // 'Origin' has no column on Vessel — surface it in the form as read-only info.
            ViewBag.Origin = origin;
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Register(Vessel vessel, string? returnUrl = null, string? companyName = null)
        {
            /* ── MANDATORY-FIELD VALIDATION ──────────────────────────────────────
               A vessel must never reach the database with a blank IMO Number or no
               Company — those are the incomplete records that pollute the reports.
               On failure the form is RE-RENDERED (not redirected) so nothing the
               user typed is lost, and every missing field is named and highlighted. */
            var missing = new List<string>();

            // The company box is a free-text datalist; resolve a typed name to its ID
            // so "I typed it but the hidden ID never got set" is not a false rejection.
            if (vessel.CompanyID is null or <= 0 && !string.IsNullOrWhiteSpace(companyName))
                vessel.CompanyID = _repo.GetCompanyByName(companyName.Trim())?.CompanyID;

            string imoDigits = new string((vessel.IMO_Number ?? "").Where(char.IsDigit).ToArray());

            if (string.IsNullOrWhiteSpace(vessel.IMO_Number))
                missing.Add("IMO Number");
            else if (imoDigits.Length != 7)
                missing.Add("IMO Number (must be exactly 7 digits)");

            if (string.IsNullOrWhiteSpace(vessel.VesselName))
                missing.Add("Vessel Name");

            if (vessel.CompanyID is null or <= 0)
                missing.Add("Company Name");

            if (missing.Count > 0)
            {
                ViewBag.Types = _repo.GetVesselTypes().ToList();
                ViewBag.Companies = _repo.GetCompanyNameList().ToList();   // dropdown only — no fleet aggregate needed
                ViewBag.ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
                ViewBag.MissingFields = missing;      // drives the red field highlighting
                ViewBag.TypedCompanyName = companyName;
                TempData["Error"] = "The vessel was NOT saved. Please complete: " + string.Join(", ", missing) + ".";
                return View(vessel);
            }

            vessel.IMO_Number = vessel.IMO_Number.Trim();
            string trimVesssel = vessel.VesselName.Replace(" ", "");
            string Vesseldotedname = vessel.VesselName.Replace(" ", ".");
            vessel.GenerateEmail = "master." + trimVesssel + "@amosconnect.com\nmaster@" + trimVesssel + ".amosconnect.com\n" + trimVesssel + "@skyfile.com\nmaster." + trimVesssel + "@fleetmail.inmarsat.com\n" + trimVesssel + "@gtmailplus.com\n" + trimVesssel + "@speedmailplus.com\n" + trimVesssel + "@shipmail.net\n" + trimVesssel + "@ctmail.1749.cn\n" + Vesseldotedname + "@gtships.com\n" + trimVesssel + "@gtships.com\n" + trimVesssel + "@infinitymail.eu\n" + trimVesssel + "@om-email.net";
            _repo.SaveVessel(vessel);
            TempData["Ok"] = $"Vessel '{vessel.VesselName}' (IMO {vessel.IMO_Number}) saved.";
            // Came from Import Data (or anywhere else that passed returnUrl) → go back there.
            if (Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl!);
            return RedirectToAction(nameof(Index), new { q = vessel.IMO_Number });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Delete(string imo, string? q = null, int? companyId = null,
                                    int? typeId = null, string? country = null,
                                    bool regularOnly = false, string? port = null)
        {
            if (string.IsNullOrWhiteSpace(imo))
            {
                TempData["Error"] = "No vessel specified to delete.";
                return RedirectToAction(nameof(Index), new { q, companyId, typeId, country, regularOnly, port });
            }

            var vessel = _repo.GetVesselByIMO(imo.Trim());
            if (vessel is null)
            {
                TempData["Error"] = $"Vessel with IMO {imo} was not found (already deleted?).";
            }
            else
            {
                try
                {
                    _repo.DeleteVessel(imo.Trim());
                    TempData["Ok"] = $"Vessel '{vessel.VesselName}' (IMO {vessel.IMO_Number}) deleted.";
                }
                catch (Exception ex)
                {
                    // e.g. a FK constraint referencing this vessel — show a friendly
                    // message instead of a 500.
                    TempData["Error"] = $"Could not delete vessel '{vessel.VesselName}': {ex.Message}";
                }
            }
            // Return to the same filtered list the user was viewing.
            return RedirectToAction(nameof(Index), new { q, companyId, typeId, country, regularOnly, port });
        }

        /// <summary>AJAX: IMO + Tab → fetch existing vessel data (V2 workflow).</summary>
        [HttpGet]
        public IActionResult FetchByImo(string imo)
        {
            var v = _repo.GetVesselByIMO(imo?.Trim() ?? "");
            if (v is null) return Json(new { ok = false, message = "No record — enter details manually." });
            return Json(new { ok = true, vessel = v });
        }

        /// <summary>AJAX: Company name → autofill (read-only) details or signal Add Company.</summary>
        [HttpGet]
        public IActionResult FetchCompany(string name)
        {
            var c = _repo.GetCompanyByName(name?.Trim() ?? "");
            if (c is null) return Json(new { ok = false, message = "Company not found — use Add Company." });
            return Json(new { ok = true, company = c });
        }

        public IActionResult ExportExcel(string? q, int? companyId, int? typeId, string? country, bool regularOnly = false, string? port = null)
        {
            var rows = _repo.SearchVessels(string.IsNullOrWhiteSpace(q) ? null : q,
                                           companyId, country, typeId, regularOnly, NullIfEmpty(port)).ToList();
            var bytes = _export.VesselsExcel(rows);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"Vessels_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public IActionResult ExportCsv(string? q, int? companyId, int? typeId, string? country, bool regularOnly = false, string? port = null)
        {
            var rows = _repo.SearchVessels(string.IsNullOrWhiteSpace(q) ? null : q,
                                           companyId, country, typeId, regularOnly, NullIfEmpty(port)).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.VesselsCsv(rows)),
                        "text/csv", $"Vessels_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        /// <summary>Print-friendly (PDF via browser print) vessel-wise report.</summary>
        public IActionResult Print(string? q, int? companyId, int? typeId, string? country, bool regularOnly = false, string? port = null)
        {
            var rows = _repo.SearchVessels(string.IsNullOrWhiteSpace(q) ? null : q,
                                           companyId, country, typeId, regularOnly, NullIfEmpty(port)).ToList();
            ViewBag.Title = "Vessel-Wise Report";
            ViewBag.Headers = new[] { "S.No", "Vessel", "IMO #", "Type", "Call Sign", "Company", "Port", "Country", "Terms", "Status" };
            ViewBag.Rows = rows.Select((v, i) => new[]
            {
                (i + 1).ToString(), v.VesselName, v.IMO_Number, v.VesselType ?? "", v.CallSign ?? "",
                v.CompanyName ?? "", v.Port ?? "", v.Country ?? "", v.Terms ?? "", v.Status
            }).ToList();
            ViewBag.RegularFlags = rows.Select(v => v.CustomerStatus == "Regular").ToList();
            return View("ReportPrint");
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /* ════════════════════ COMPANY REGISTRATION & FLEET ════════════════════ */
    public class CompaniesController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;
        public CompaniesController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        public IActionResult Index(string? q, bool regularOnly = false, int page = 1, int pageSize = 50)
        {
            ViewBag.Q = q; ViewBag.RegularOnly = regularOnly;
            // Paged: only one screen of companies is fetched, and fleet counts are
            // computed for those rows only (was: aggregate over ALL vessels, every load).
            var (rows, total) = _repo.GetCompaniesPaged(
                string.IsNullOrWhiteSpace(q) ? null : q, regularOnly, page, pageSize);
            ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Total = total;
            ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
            return View(rows);
        }

        public IActionResult Details(int id)
        {
            var co = _repo.GetCompanyByID(id);
            if (co is null) return NotFound();
            ViewBag.Vessels = _repo.GetVesselsByCompany(id).ToList();
            return View(co);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Save(Company company, string? returnTo = null)
        {
            if (string.IsNullOrWhiteSpace(company.CompanyName))
                TempData["Error"] = "Company Name is required.";
            else
            {
                _repo.SaveCompany(company);
                TempData["Ok"] = $"Company '{company.CompanyName}' saved.";
            }
            return Redirect(returnTo ?? Url.Action(nameof(Index))!);
        }

        /// <summary>AJAX add-company used by the Vessel Registration modal.</summary>
        [HttpPost]
        public IActionResult SaveAjax([FromForm] Company company)
        {
            if (string.IsNullOrWhiteSpace(company.CompanyName))
                return Json(new { ok = false, message = "Company Name is required." });
            var id = _repo.SaveCompany(company);
            return Json(new { ok = true, companyId = id, companyName = company.CompanyName });
        }

        /// <summary>
        /// Delete a company that was added by mistake or is no longer needed.
        /// Protected on three levels:
        ///   1. [RequireAdmin] — the global session filter rejects non-Admin roles
        ///      outright, so the action is unreachable for a normal user even by URL.
        ///   2. The Admin must re-enter their own login password to confirm.
        ///   3. A company that still owns vessels is refused unless the Admin
        ///      explicitly opts to unlink the fleet first (the vessels are kept).
        /// </summary>
        [HttpPost, RequireAdmin, ValidateAntiForgeryToken]
        public IActionResult Delete(int id, string? adminPassword, bool unlinkVessels = false,
                                    string? returnTo = null)
        {
            string Back() => returnTo ?? Url.Action(nameof(Index))!;

            var company = _repo.GetCompanyByID(id);
            if (company is null)
            {
                TempData["Error"] = "That company no longer exists (already deleted?).";
                return Redirect(Back());
            }

            // ── Confirm the Administrator's own password ──
            var username = HttpContext.Session.GetString(SessionKeys.Username) ?? "";
            var admin = _repo.GetUserByUsername(username);
            if (admin is null || admin.Role != "Admin")
            {
                TempData["Error"] = "Only an Administrator can delete a company.";
                return Redirect(Back());
            }
            if (string.IsNullOrEmpty(adminPassword) ||
                !PasswordHasher.Verify(adminPassword, admin.PasswordHash))
            {
                TempData["Error"] = $"Incorrect password — '{company.CompanyName}' was NOT deleted.";
                return Redirect(Back());
            }

            // ── Fleet guard ──
            int fleet = _repo.GetCompanyVesselCount(id);
            if (fleet > 0 && !unlinkVessels)
            {
                TempData["Error"] =
                    $"'{company.CompanyName}' still has {fleet} vessel(s) registered to it. " +
                    "Re-open the delete dialog and tick \"Also unlink its vessels\" to confirm — " +
                    "the vessels are kept, only their company link is cleared.";
                return Redirect(Back());
            }

            try
            {
                int unlinked = _repo.DeleteCompany(id, unlinkVessels);
                TempData["Ok"] = $"Company '{company.CompanyName}' deleted." +
                                 (unlinked > 0 ? $" {unlinked} vessel(s) were unlinked and kept." : "");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Could not delete '{company.CompanyName}': {ex.Message}";
            }
            return Redirect(Back());
        }

        /// <summary>V2: Status button beside each company — Regular / Non-Regular toggle.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult SetStatus(int id, string status, string? returnTo = null)
        {
            _repo.SetCompanyStatus(id, status == "Regular" ? "Regular" : "Non-Regular");
            TempData["Ok"] = "Customer status updated.";
            return Redirect(returnTo ?? Url.Action(nameof(Index))!);
        }

        public IActionResult ExportExcel(string? q, bool regularOnly = false)
        {
            var rows = _repo.GetAllCompanies(string.IsNullOrWhiteSpace(q) ? null : q, regularOnly).ToList();
            return File(_export.CompaniesExcel(rows),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"CompanyFleet_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public IActionResult ExportCsv(string? q, bool regularOnly = false)
        {
            var rows = _repo.GetAllCompanies(string.IsNullOrWhiteSpace(q) ? null : q, regularOnly).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.CompaniesCsv(rows)),
                "text/csv", $"CompanyFleet_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        /// <summary>Print-friendly (PDF via browser print) company fleet report.</summary>
        public IActionResult Print(string? q, bool regularOnly = false)
        {
            var rows = _repo.GetAllCompanies(string.IsNullOrWhiteSpace(q) ? null : q, regularOnly).ToList();
            ViewBag.Title = "Company Fleet Report";
            ViewBag.Headers = new[] { "S.No", "Company", "Address", "Country", "General Email", "Website", "Telephone", "Fleet Size", "Status" };
            ViewBag.Rows = rows.Select((c, i) => new[]
            {
                (i + 1).ToString(), c.CompanyName, c.Address ?? "", c.Country ?? "", c.GeneralEmail ?? "",
                c.Website ?? "", c.Telephone ?? "", c.FleetCount.ToString(), c.Status
            }).ToList();
            ViewBag.RegularFlags = rows.Select(c => c.Status == "Regular").ToList();
            return View("ReportPrint");
        }
    }

    /* ════════════════════ REGULAR CUSTOMERS DASHBOARD (V2) ════════════════════ */
    public class RegularCustomersController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly ExportService _export;
        public RegularCustomersController(ShippingRepository repo, ExportService export)
        { _repo = repo; _export = export; }

        public IActionResult Index()
        {
            var companies = _repo.GetAllCompanies(regularOnly: true).ToList();
            // One query for ALL regular customers' vessels, grouped in memory.
            // Was: one database round trip PER company (N+1) — the main cost of this page.
            var byCompany = _repo.GetVesselsOfRegularCompanies()
                                 .GroupBy(v => v.CompanyID ?? 0)
                                 .ToDictionary(g => g.Key, g => g.ToList());
            ViewBag.Vessels = companies.ToDictionary(
                c => c.CompanyID,
                c => byCompany.TryGetValue(c.CompanyID, out var list) ? list : new List<Vessel>());
            // recent arrivals for regular customers
            ViewBag.RecentArrivals = _repo.GetArrivals(null, null, regularOnly: true)
                                          .OrderByDescending(a => a.ArrivalDate).Take(100).ToList();
            return View(companies);
        }

        public IActionResult ExportExcel()
        {
            var rows = _repo.GetArrivals(null, null, regularOnly: true).ToList();
            return File(_export.DailyReportSingleSheet(rows, "Regular Customers"),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"RegularCustomers_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public IActionResult ExportCsv()
        {
            var rows = _repo.GetArrivals(null, null, regularOnly: true).ToList();
            return File(System.Text.Encoding.UTF8.GetBytes(_export.ArrivalsCsv(rows)),
                "text/csv", $"RegularCustomers_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        /// <summary>Print-friendly (PDF via browser print) regular-customer report.</summary>
        public IActionResult Print()
        {
            var rows = _repo.GetArrivals(null, null, regularOnly: true)
                            .OrderByDescending(a => a.ArrivalDate).ToList();
            ViewBag.Title = "Regular Customers Report";
            ViewBag.Headers = new[] { "Date", "IMO #", "Vessel", "Type", "Port", "Country", "Company", "Status" };
            ViewBag.Rows = rows.Select(a => new[]
            {
                a.ArrivalDate.ToString("yyyy-MM-dd"), a.IMO_Number ?? "", a.VesselName ?? "", a.VesselType ?? "",
                a.PortName, a.Country, a.CompanyName ?? "", a.Status ?? ""
            }).ToList();
            ViewBag.RegularFlags = rows.Select(_ => true).ToList();
            return View("ReportPrint");
        }
    }

    /* ════════════════════ VESSEL TYPES (Admin) ════════════════════ */
    [RequireAdmin]
    public class VesselTypesController : Controller
    {
        private readonly ShippingRepository _repo;
        public VesselTypesController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index() => View(_repo.GetVesselTypes().ToList());

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Add(string typeName)
        {
            if (!string.IsNullOrWhiteSpace(typeName)) _repo.AddVesselType(typeName.Trim());
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            _repo.DeleteVesselType(id);
            return RedirectToAction(nameof(Index));
        }
    }
}