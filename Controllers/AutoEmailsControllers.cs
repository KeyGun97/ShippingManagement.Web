using Microsoft.AspNetCore.Mvc;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
using ShippingManagement.Web.Models;
using ShippingManagement.Web.Services;

namespace ShippingManagement.Web.Controllers
{
    /* ════════════════════ AUTO EMAILS (front-page module) ════════════════════
       Sends category-based emails (Confirm / Purchase / Catering / Generate /
       Deck-Eng / General) to the addresses stored on each vessel, for a chosen
       date / country. Uses SMTP settings in appsettings.json → "Email".
       With Email:Enabled = false every message is recorded in the log as
       "Logged" so the workflow runs without real SMTP credentials. */
    public class AutoEmailsController : Controller
    {
        private readonly ShippingRepository _repo;
        private readonly EmailService _email;
        public AutoEmailsController(ShippingRepository repo, EmailService email)
        { _repo = repo; _email = email; }

        public IActionResult Index(DateTime? date, string? country, string category = "Confirm", bool regularOnly = false)
        {
            var d = date ?? DateTime.Today;
            if (!EmailService.Categories.Contains(category)) category = "Confirm";

            var rows = _repo.GetArrivals(d, NullIfEmpty(country), excludeTagged: true, regularOnly: regularOnly).ToList();

            ViewBag.Date = d;
            ViewBag.Country = country;
            ViewBag.Category = category;
            ViewBag.RegularOnly = regularOnly;
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.Categories = EmailService.Categories;
            ViewBag.SmtpEnabled = _email.IsEnabled;
            ViewBag.Log = _repo.GetEmailLog(50).ToList();
            ViewBag.DefaultSubject = $"{category} – {{Vessel}} (IMO {{IMO}}) arriving {{Port}}";
            ViewBag.DefaultBody =
                "Dear {Company},\n\n" +
                "This is regarding vessel {Vessel} (IMO {IMO}) arriving at {Port} on {Date}.\n\n" +
                "Please find our requirements attached / below.\n\n" +
                "Regards,\nOperations Team";
            return View(rows);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Send(DateTime date, string? country, string category,
                                  string subject, string body, bool regularOnly = false, bool htmlBody = false)
        {
            if (!EmailService.Categories.Contains(category)) category = "General";
            var rows = _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true, regularOnly: regularOnly).ToList();

            var messages = new List<EmailMessage>();
            foreach (var a in rows)
            {
                var addr = EmailService.CategoryAddress(a, category);
                if (string.IsNullOrWhiteSpace(addr)) continue;   // skip rows with no address for this category
                messages.Add(new EmailMessage
                {
                    Category   = category,
                    ToAddress  = addr,
                    Subject    = Fill(subject, a),
                    Body       = Fill(body, a),
                    IsHtml     = htmlBody,
                    IMO_Number = a.IMO_Number,
                    VesselName = a.VesselName,
                    CompanyName = a.CompanyName
                });
            }

            if (messages.Count == 0)
            {
                TempData["Error"] = $"No vessels on {date:yyyy-MM-dd} have a {category} email address on record.";
                return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
            }

            var (sent, failed, logged) = _email.SendBatch(messages, HttpContext.CurrentUserId());
            TempData["Ok"] = _email.IsEnabled
                ? $"{category} emails: {sent} sent, {failed} failed (out of {messages.Count})."
                : $"{logged} {category} email(s) recorded in the log. SMTP is disabled — enable it in appsettings.json to send for real.";
            return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
        }

        private static string Fill(string template, ArrivalLog a) => (template ?? "")
            .Replace("{Vessel}", a.VesselName ?? "")
            .Replace("{IMO}", a.IMO_Number ?? "")
            .Replace("{Port}", a.PortName ?? "")
            .Replace("{Country}", a.Country ?? "")
            .Replace("{Company}", a.CompanyName ?? "")
            .Replace("{Date}", a.ArrivalDate.ToString("yyyy-MM-dd"));

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
