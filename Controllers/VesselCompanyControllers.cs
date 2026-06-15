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

        public IActionResult Index(string? q, int? companyId, int? typeId, string? country, bool regularOnly = false, string? port = null)
        {
            ViewBag.Companies = _repo.GetAllCompanies().ToList();
            ViewBag.Types = _repo.GetVesselTypes().ToList();
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.Ports = _repo.GetDistinctPortNames().ToList();
            ViewBag.Q = q; ViewBag.CompanyId = companyId; ViewBag.TypeId = typeId;
            ViewBag.Country = country; ViewBag.RegularOnly = regularOnly; ViewBag.Port = port;
            var rows = _repo.SearchVessels(string.IsNullOrWhiteSpace(q) ? null : q,
                                           companyId, country, typeId, regularOnly, NullIfEmpty(port)).ToList();
            return View(rows);
        }

        [HttpGet]
        public IActionResult Register(string? imo = null, string? name = null)
        {
            ViewBag.Types = _repo.GetVesselTypes().ToList();
            ViewBag.Companies = _repo.GetAllCompanies().ToList();
            Vessel model = imo is not null
                ? _repo.GetVesselByIMO(imo) ?? new Vessel { IMO_Number = imo, VesselName = name ?? "" }
                : new Vessel { VesselName = name ?? "" };
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Register(Vessel vessel)
        {
            if (string.IsNullOrWhiteSpace(vessel.IMO_Number) || string.IsNullOrWhiteSpace(vessel.VesselName))
            {
                TempData["Error"] = "IMO Number and Vessel Name are required (data is saved/keyed by IMO).";
                return RedirectToAction(nameof(Register), new { imo = vessel.IMO_Number });
            }
            vessel.IMO_Number = vessel.IMO_Number.Trim();
            _repo.SaveVessel(vessel);
            TempData["Ok"] = $"Vessel '{vessel.VesselName}' (IMO {vessel.IMO_Number}) saved.";
            return RedirectToAction(nameof(Index), new { q = vessel.IMO_Number });
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

        public IActionResult Index(string? q, bool regularOnly = false)
        {
            ViewBag.Q = q; ViewBag.RegularOnly = regularOnly;
            return View(_repo.GetAllCompanies(string.IsNullOrWhiteSpace(q) ? null : q, regularOnly).ToList());
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
            var details = companies.ToDictionary(
                c => c.CompanyID,
                c => _repo.GetVesselsByCompany(c.CompanyID).ToList());
            ViewBag.Vessels = details;
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
