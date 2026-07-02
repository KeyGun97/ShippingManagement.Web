using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

        // Session keys for the optional Excel-uploaded recipient list (overrides DB arrivals while present).
        private const string RecipKey = "AutoEmail.Recipients";
        private const string RecipFileKey = "AutoEmail.RecipientsFile";

        public IActionResult Index(DateTime? date, string? country, string category = "Generate", bool regularOnly = false)
        {
            var d = date ?? DateTime.Today;
            if (!EmailService.Categories.Contains(category)) category = "Generate";

            // If the user uploaded an Excel list, show that instead of querying the database.
            var uploaded = GetUploadedRecipients();
            List<ArrivalLog> rows;
            if (uploaded is not null)
            {
                foreach (var r in uploaded) r.ArrivalDate = d;   // align {Date} placeholder with the chosen date
                rows = uploaded;
                ViewBag.Uploaded = true;
                ViewBag.UploadFile = HttpContext.Session.GetString(RecipFileKey) ?? "uploaded file";
            }
            else
            {
                rows = _repo.GetArrivals(d, NullIfEmpty(country), excludeTagged: true, regularOnly: regularOnly).ToList();
                ViewBag.Uploaded = false;
            }

            ViewBag.Date = d;
            ViewBag.Country = country;
            ViewBag.Category = category;
            ViewBag.RegularOnly = regularOnly;
            ViewBag.Countries = _repo.GetCountries().ToList();
            ViewBag.Categories = EmailService.Categories;
            ViewBag.SmtpEnabled = _email.IsEnabled;
            ViewBag.Profiles = _email.Profiles;
            ViewBag.DefaultProfile = _email.ResolveProfile(null).Name;
            ViewBag.Templates = _repo.GetEmailTemplates().ToList();
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
                                  string subject, string body, bool regularOnly = false, bool htmlBody = false,
                                  string? profile = null,
                                  List<string>? to = null, List<string>? cc = null, List<string>? bcc = null)
        {
            if (!EmailService.Categories.Contains(category)) category = "General";

            // Prefer the Excel-uploaded list if one is loaded; otherwise read arrivals from the database.
            var uploaded = GetUploadedRecipients();
            var rows = uploaded ?? _repo.GetArrivals(date, NullIfEmpty(country), excludeTagged: true, regularOnly: regularOnly).ToList();
            if (uploaded is not null) foreach (var r in rows) r.ArrivalDate = date;

            var messages = new List<EmailMessage>();

            if (uploaded is not null)
            {
                // Uploaded-document flow (per requirement):
                //   To  = Confirm Email   CC = Call Sign   BCC = Generated Email
                // The Recipients table is editable, so apply any inline edits (parallel
                // arrays, one entry per row in table order) and persist them to the session
                // so they survive a validation redirect and the actual send.
                if (to is not null && to.Count == rows.Count)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        rows[i].ConfirmEmail = NullIfEmpty(to[i]?.Trim());
                        if (cc is not null && cc.Count == rows.Count) rows[i].CallSign = NullIfEmpty(cc[i]?.Trim());
                        if (bcc is not null && bcc.Count == rows.Count) rows[i].GenerateEmail = NullIfEmpty(bcc[i]?.Trim());
                    }
                    HttpContext.Session.SetString(RecipKey, JsonSerializer.Serialize(rows));
                }

                // Confirm Email is mandatory — if ANY recipient is missing it, stop the
                // whole send and tell the user which vessels to fix.
                var missing = rows
                    .Where(a => string.IsNullOrWhiteSpace(a.ConfirmEmail))
                    .ToList();

                if (missing.Count > 0)
                {
                    var names = string.Join(", ", missing
                        .Select(a => string.IsNullOrWhiteSpace(a.VesselName) ? "(unnamed vessel)" : a.VesselName)
                        .Take(10));
                    if (missing.Count > 10) names += ", …";
                    TempData["Error"] =
                        $"Cannot send: {missing.Count} uploaded recipient(s) have no Confirm Email " +
                        $"(used as the \u201CTo\u201D address). No emails were sent. Fix and re-upload: {names}";
                    return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
                }

                foreach (var a in rows)
                {
                    messages.Add(new EmailMessage
                    {
                        Category = category,
                        ToAddress = a.ConfirmEmail,    // To  = Confirm Email
                        CcAddress = a.CallSign,        // CC  = Call Sign   (see note in Index view)
                        BccAddress = a.GenerateEmail,   // BCC = Generated Email
                        Subject = Fill(subject, a),
                        Body = Fill(body, a),
                        IsHtml = htmlBody,
                        IMO_Number = a.IMO_Number,
                        VesselName = a.VesselName,
                        CompanyName = a.CompanyName
                    });
                }
            }
            else
            {
                // Database-arrivals flow — unchanged: one address per row for the chosen category.
                foreach (var a in rows)
                {
                    var addr = EmailService.CategoryAddress(a, category);
                    if (string.IsNullOrWhiteSpace(addr)) continue;   // skip rows with no address for this category
                    messages.Add(new EmailMessage
                    {
                        Category = category,
                        ToAddress = addr,
                        Subject = Fill(subject, a),
                        Body = Fill(body, a),
                        IsHtml = htmlBody,
                        IMO_Number = a.IMO_Number,
                        VesselName = a.VesselName,
                        CompanyName = a.CompanyName
                    });
                }
            }

            if (messages.Count == 0)
            {
                TempData["Error"] = $"No vessels on {date:yyyy-MM-dd} have a {category} email address on record.";
                return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
            }

            var via = _email.ResolveProfile(profile).Name;
            var (sent, failed, logged) = _email.SendBatch(messages, HttpContext.CurrentUserId(), profile);
            TempData["Ok"] = _email.IsEnabled
                ? $"{category} emails via {via}: {sent} sent, {failed} failed (out of {messages.Count})."
                : $"{logged} {category} email(s) recorded in the log (would send via {via}). SMTP is disabled — enable it in appsettings.json to send for real.";
            return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
        }

        /* ─────────── Excel upload: load recipients from a spreadsheet ───────────
           Accepts a .xlsx in the same shape as Exemple_Format.xlsx and maps its
           columns onto the recipient list. The parsed rows are kept in Session so
           the Send action emails exactly what's shown here. The Confirm Email
           column drives the Confirm category. */
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Upload(IFormFile? file, DateTime? date, string? country,
                                    string category = "Generate", bool regularOnly = false)
        {
            object routeVals() => new { date, country, category, regularOnly };

            if (file is null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an Excel (.xlsx) file to upload.";
                return RedirectToAction(nameof(Index), routeVals());
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".xlsx" && ext != ".xlsm")
            {
                TempData["Error"] = "Unsupported file type — please upload a .xlsx file (same format as Exemple_Format.xlsx).";
                return RedirectToAction(nameof(Index), routeVals());
            }

            try
            {
                using var stream = file.OpenReadStream();
                var list = ParseExcel(stream);

                if (list.Count == 0)
                {
                    TempData["Error"] = "No usable rows found. The sheet needs a header row with at least 'Vessel Name' and 'Confirm Email'.";
                    return RedirectToAction(nameof(Index), routeVals());
                }

                HttpContext.Session.SetString(RecipKey, JsonSerializer.Serialize(list));
                HttpContext.Session.SetString(RecipFileKey, Path.GetFileName(file.FileName));

                int withConfirm = list.Count(r => !string.IsNullOrWhiteSpace(r.ConfirmEmail));
                TempData["Ok"] = $"Loaded {list.Count} recipient(s) from \"{file.FileName}\" — {withConfirm} have a Confirm email.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Could not read the Excel file: " + ex.Message;
            }

            return RedirectToAction(nameof(Index), routeVals());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult ClearUpload(DateTime? date, string? country,
                                         string category = "Generate", bool regularOnly = false)
        {
            HttpContext.Session.Remove(RecipKey);
            HttpContext.Session.Remove(RecipFileKey);
            TempData["Ok"] = "Uploaded recipient list cleared — showing database arrivals again.";
            return RedirectToAction(nameof(Index), new { date, country, category, regularOnly });
        }

        private List<ArrivalLog>? GetUploadedRecipients()
        {
            var json = HttpContext.Session.GetString(RecipKey);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<List<ArrivalLog>>(json); }
            catch { return null; }
        }

        // Header aliases → ArrivalLog field. Headers are normalised (letters/digits only, lower-case)
        // so quirks like "Vessel Name ", "Caterin g Emails" or "Purchaser Email" still match.
        private static readonly (string Field, string[] Aliases)[] ColumnAliases =
        {
            ("VesselName",     new[]{ "vesselname", "vessel", "shipname" }),
            ("IMO",            new[]{ "imo", "imono", "imonumber" }),
            ("CallSign",       new[]{ "callsign" }),
            ("VesselType",     new[]{ "vesseltype", "type" }),
            ("ConfirmEmail",   new[]{ "confirmemail", "confirmemails", "confirm" }),
            ("PurchaseEmail",  new[]{ "purchaseremail", "purchaseemail", "purchaseremails", "purchaseemails", "purchaser", "purchase" }),
            ("CateringEmail",  new[]{ "cateringemail", "cateringemails", "catering" }),
            ("GenerateEmail",  new[]{ "generatedemail", "generateemail", "generatedemails", "generateemails", "generated", "generate" }),
            ("GeneralEmail",   new[]{ "generalemail", "generalemails", "general" }),
            ("CompanyName",    new[]{ "companyname", "company" }),
            ("CompanyAddress", new[]{ "address", "companyaddress" }),
            ("Telephone",      new[]{ "telephone", "tel", "phone", "contact" }),
        };

        private static List<ArrivalLog> ParseExcel(Stream stream)
        {
            var list = new List<ArrivalLog>();

            // Read the .xlsx straight from its ZIP package (OPC) with the framework's Zip reader.
            // This deliberately bypasses ClosedXML/OpenXML relationship validation, which throws
            // "A malformed URI was found in the document" when the sheet contains an odd hyperlink
            // (e.g. several emails joined with ';' that Excel turned into an invalid mailto link).
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var shared = ReadSharedStrings(zip);

            // First worksheet = lowest-numbered xl/worksheets/sheetN.xml
            var sheetEntry = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                         && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(NaturalSheetOrder)
                .FirstOrDefault();
            if (sheetEntry is null) return list;

            XDocument doc;
            using (var s = sheetEntry.Open()) doc = XDocument.Load(s);

            var rowEls = doc.Root?.Element(SsNs + "sheetData")?.Elements(SsNs + "row").ToList();
            if (rowEls is null || rowEls.Count < 2) return list;   // need header + at least one data row

            // Header row (first <row>) → field name → column letters (e.g. "C").
            var colMap = new Dictionary<string, string>();
            foreach (var c in rowEls[0].Elements(SsNs + "c"))
            {
                var norm = Normalize(CellText(c, shared));
                if (norm.Length == 0) continue;
                var col = ColLetters(c.Attribute("r")?.Value);
                foreach (var (field, aliases) in ColumnAliases)
                    if (!colMap.ContainsKey(field) && aliases.Contains(norm))
                        colMap[field] = col;
            }

            foreach (var rowEl in rowEls.Skip(1))
            {
                var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in rowEl.Elements(SsNs + "c"))
                    cells[ColLetters(c.Attribute("r")?.Value)] = CellText(c, shared);

                string? Field(string key) =>
                    colMap.TryGetValue(key, out var col) && cells.TryGetValue(col, out var v) ? Clean(v) : null;

                var vessel = Field("VesselName");
                var imo = Field("IMO");
                var confirm = Field("ConfirmEmail");
                var company = Field("CompanyName");

                // Skip blank / spacer rows.
                if (string.IsNullOrWhiteSpace(vessel) && string.IsNullOrWhiteSpace(imo) &&
                    string.IsNullOrWhiteSpace(confirm) && string.IsNullOrWhiteSpace(company))
                    continue;

                list.Add(new ArrivalLog
                {
                    VesselName = vessel,
                    IMO_Number = imo,
                    CallSign = Field("CallSign"),
                    VesselType = Field("VesselType"),
                    ConfirmEmail = confirm,
                    PurchaseEmail = Field("PurchaseEmail"),
                    CateringEmail = Field("CateringEmail"),
                    GenerateEmail = Field("GenerateEmail"),
                    GeneralEmail = Field("GeneralEmail"),
                    CompanyName = company,
                    CompanyAddress = Field("CompanyAddress"),
                    Telephone = Field("Telephone"),
                    PortName = "",          // not present in the sheet
                    Country = "",
                    ArrivalDate = DateTime.Today
                });
            }

            return list;
        }

        private static readonly XNamespace SsNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var result = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return result;

            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            if (doc.Root is null) return result;

            foreach (var si in doc.Root.Elements(SsNs + "si"))
                result.Add(SiText(si));
            return result;
        }

        // <si> holds either one <t>, or several <r><t> runs (rich text). Concatenate the text.
        private static string SiText(XElement si)
        {
            var single = si.Element(SsNs + "t");
            if (single is not null) return single.Value;

            var sb = new StringBuilder();
            foreach (var r in si.Elements(SsNs + "r"))
                sb.Append(r.Element(SsNs + "t")?.Value);
            return sb.ToString();
        }

        // Resolve a cell's text: shared string, inline string, or the raw value (numbers/dates).
        private static string? CellText(XElement c, List<string> shared)
        {
            var type = c.Attribute("t")?.Value;
            if (type == "s")
            {
                var v = c.Element(SsNs + "v")?.Value;
                return int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : null;
            }
            if (type == "inlineStr")
            {
                var inline = c.Element(SsNs + "is");
                return inline is not null ? SiText(inline) : null;
            }
            return c.Element(SsNs + "v")?.Value;   // "str" formula result or numeric
        }

        // "C12" → "C"
        private static string ColLetters(string? cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return "";
            int i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
            return cellRef.Substring(0, i).ToUpperInvariant();
        }

        // xl/worksheets/sheet1.xml → 1, sheet12.xml → 12 (so sheet2 sorts before sheet10).
        private static int NaturalSheetOrder(ZipArchiveEntry e)
        {
            var file = e.FullName[(e.FullName.LastIndexOf('/') + 1)..];
            var digits = new string(file.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : int.MaxValue;
        }

        private static string Normalize(string? s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // Trim and treat spreadsheet "no value" placeholders as empty.
        private static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.ToLowerInvariant() switch
            {
                "#n.a" or "#n/a" or "n/a" or "n.a" or "na" or "#na" or "-" => null,
                _ => t
            };
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

    /* ════════════════════ EMAIL TEMPLATES (manage reusable subject/body presets) ════════════════════
       Lets the user save named templates (optionally tied to a category) and pick them
       from a dropdown on the Auto Emails compose form. */
    public class EmailTemplatesController : Controller
    {
        private readonly ShippingRepository _repo;
        public EmailTemplatesController(ShippingRepository repo) => _repo = repo;

        public IActionResult Index(int? edit)
        {
            ViewBag.Categories = EmailService.Categories;
            ViewBag.Editing = edit is not null ? _repo.GetEmailTemplate(edit.Value) : null;
            return View(_repo.GetEmailTemplates().ToList());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Save(int templateId, string name, string? category,
                                  string subject, string body, bool isHtml = false)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                TempData["Error"] = "Name, Subject and Body are all required for a template.";
                return RedirectToAction(nameof(Index), templateId > 0 ? new { edit = templateId } : null);
            }

            // "General" is the catch-all category, so treat it as "no specific category".
            var cat = string.IsNullOrWhiteSpace(category) || category == "Any" ? null : category;

            var t = new EmailTemplate
            {
                TemplateID = templateId,
                Name = name.Trim(),
                Category = cat,
                Subject = subject,
                Body = body,
                IsHtml = isHtml
            };

            if (templateId > 0) { _repo.UpdateEmailTemplate(t); TempData["Ok"] = $"Template \"{t.Name}\" updated."; }
            else { _repo.AddEmailTemplate(t); TempData["Ok"] = $"Template \"{t.Name}\" added."; }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            _repo.DeleteEmailTemplate(id);
            TempData["Ok"] = "Template deleted.";
            return RedirectToAction(nameof(Index));
        }
    }
}