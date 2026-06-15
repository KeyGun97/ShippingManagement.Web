using System.Text;
using ClosedXML.Excel;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Models;

namespace ShippingManagement.Web.Services
{
    /// <summary>
    /// Excel / CSV exports (ClosedXML — same library as the legacy WinForms app).
    /// Asia / Non-Asia split is driven by the Countries.IsAsia flag, with a
    /// keyword fallback matching the legacy ExcelExportService behaviour.
    /// </summary>
    public class ExportService
    {
        private readonly ShippingRepository _repo;
        public ExportService(ShippingRepository repo) => _repo = repo;

        private static readonly string[] ArrivalHeaders =
        {
            "S.No","Vessel Name","IMO #","Vessel Type","Call Sign","Port","Country","Arrival Date",
            "Company Name","Company Address","Company Country","Customer Status","Fleet Email",
            "Confirm Email","Purchase Email","Catering Email","Generate Email","Deck/Eng Email",
            "General Email","Telephone","Website","Terms","Status"
        };

        private static object?[] ArrivalRow(int i, ArrivalLog a) => new object?[]
        {
            i, a.VesselName, a.IMO_Number, a.VesselType, a.CallSign, a.PortName, a.Country,
            a.ArrivalDate.ToString("yyyy-MM-dd"), a.CompanyName, a.CompanyAddress, a.CompanyCountry,
            a.CustomerStatus, a.CompanyEmail, a.ConfirmEmail, a.PurchaseEmail, a.CateringEmail,
            a.GenerateEmail, a.DeckEngEmail, a.GeneralEmail, a.Telephone, a.Website, a.Terms, a.Status
        };

        /* ── Daily report: single sheet ─────────────────────────────────── */
        public byte[] DailyReportSingleSheet(IList<ArrivalLog> rows, string sheetName = "Daily Report")
        {
            using var wb = new XLWorkbook();
            WriteSheet(wb.Worksheets.Add(Trunc(sheetName)), rows);
            return ToBytes(wb);
        }

        /* ── Daily report: Asia / Non-Asia two sheets ───────────────────── */
        public byte[] DailyReportTwoSheets(IList<ArrivalLog> rows)
        {
            var asia    = new List<ArrivalLog>();
            var nonAsia = new List<ArrivalLog>();
            foreach (var r in rows)
            {
                var region = r.CompanyCountry ?? r.CompanyAddress;
                (IsAsia(region) ? asia : nonAsia).Add(r);
            }
            using var wb = new XLWorkbook();
            WriteSheet(wb.Worksheets.Add("Asia Customers"), asia);
            WriteSheet(wb.Worksheets.Add("Non-Asia Customers"), nonAsia);
            return ToBytes(wb);
        }

        /* ── Generic exports ────────────────────────────────────────────── */
        public byte[] CompaniesExcel(IList<Company> rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Companies");
            string[] headers = { "S.No","Company Name","Address","Country","General Email","Website","Telephone","Fleet Size","Status" };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            StyleHeader(ws, headers.Length);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                object?[] vals = { i + 1, r.CompanyName, r.Address, r.Country, r.GeneralEmail, r.Website, r.Telephone, r.FleetCount, r.Status };
                for (int c = 0; c < vals.Length; c++) ws.Cell(i + 2, c + 1).Value = XLCellValue.FromObject(vals[c]);
                if (r.Status == "Regular")
                    ws.Range(i + 2, 1, i + 2, vals.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#cfe2ff");
            }
            ws.Columns().AdjustToContents();
            return ToBytes(wb);
        }

        public byte[] VesselsExcel(IList<Vessel> rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Vessels");
            string[] headers = { "S.No","Vessel Name","IMO #","Type","Call Sign","Company","Customer Status","Port","ETA","Country",
                                 "Confirm Email","Purchase Email","Catering Email","Generate Email","Deck/Eng Email","General Email",
                                 "Phone","Terms","Status" };
            for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            StyleHeader(ws, headers.Length);
            for (int i = 0; i < rows.Count; i++)
            {
                var v = rows[i];
                object?[] vals = { i + 1, v.VesselName, v.IMO_Number, v.VesselType, v.CallSign, v.CompanyName, v.CustomerStatus,
                                   v.Port, v.ETA, v.Country, v.ConfirmEmail, v.PurchaseEmail, v.CateringEmail, v.GenerateEmail,
                                   v.DeckEngEmail, v.GeneralEmail, v.PhoneNo, v.Terms, v.Status };
                for (int c = 0; c < vals.Length; c++) ws.Cell(i + 2, c + 1).Value = XLCellValue.FromObject(vals[c]);
                if (v.CustomerStatus == "Regular")
                    ws.Range(i + 2, 1, i + 2, vals.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#cfe2ff");
            }
            ws.Columns().AdjustToContents();
            return ToBytes(wb);
        }

        public string ArrivalsCsv(IList<ArrivalLog> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", ArrivalHeaders.Select(Csv)));
            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine(string.Join(",", ArrivalRow(i + 1, rows[i]).Select(v => Csv(v?.ToString()))));
            return sb.ToString();
        }

        public string CompaniesCsv(IList<Company> rows)
        {
            string[] headers = { "S.No", "Company Name", "Address", "Country", "General Email", "Website", "Telephone", "Fleet Size", "Status" };
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Csv)));
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                object?[] v = { i + 1, r.CompanyName, r.Address, r.Country, r.GeneralEmail, r.Website, r.Telephone, r.FleetCount, r.Status };
                sb.AppendLine(string.Join(",", v.Select(x => Csv(x?.ToString()))));
            }
            return sb.ToString();
        }

        public string VesselsCsv(IList<Vessel> rows)
        {
            string[] headers = { "S.No", "Vessel Name", "IMO #", "Type", "Call Sign", "Company", "Customer Status", "Port", "ETA", "Country",
                                 "Confirm Email", "Purchase Email", "Catering Email", "Generate Email", "Deck/Eng Email", "General Email", "Phone", "Terms", "Status" };
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Csv)));
            for (int i = 0; i < rows.Count; i++)
            {
                var v = rows[i];
                object?[] vals = { i + 1, v.VesselName, v.IMO_Number, v.VesselType, v.CallSign, v.CompanyName, v.CustomerStatus,
                                   v.Port, v.ETA, v.Country, v.ConfirmEmail, v.PurchaseEmail, v.CateringEmail, v.GenerateEmail,
                                   v.DeckEngEmail, v.GeneralEmail, v.PhoneNo, v.Terms, v.Status };
                sb.AppendLine(string.Join(",", vals.Select(x => Csv(x?.ToString()))));
            }
            return sb.ToString();
        }

        /* ── Port-Wise report: one worksheet per port ───────────────────── */
        public byte[] PortWiseExcel(IList<ArrivalLog> rows)
        {
            using var wb = new XLWorkbook();
            var groups = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.PortName) ? "(no port)" : r.PortName)
                             .OrderBy(g => g.Key);
            if (!groups.Any())
                WriteSheet(wb.Worksheets.Add("Port-Wise"), rows);
            else
                foreach (var g in groups)
                    WriteSheet(wb.Worksheets.Add(Trunc(SafeSheet(g.Key))), g.ToList());
            return ToBytes(wb);
        }

        private static string SafeSheet(string s)
        {
            foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']' }) s = s.Replace(ch, ' ');
            return string.IsNullOrWhiteSpace(s) ? "Sheet" : s;
        }

        /* ── Internals ──────────────────────────────────────────────────── */
        private void WriteSheet(IXLWorksheet ws, IList<ArrivalLog> rows)
        {
            for (int c = 0; c < ArrivalHeaders.Length; c++) ws.Cell(1, c + 1).Value = ArrivalHeaders[c];
            StyleHeader(ws, ArrivalHeaders.Length);
            for (int i = 0; i < rows.Count; i++)
            {
                var vals = ArrivalRow(i + 1, rows[i]);
                for (int c = 0; c < vals.Length; c++) ws.Cell(i + 2, c + 1).Value = XLCellValue.FromObject(vals[c]);
                if (rows[i].CustomerStatus == "Regular")   // V2: regular-customer rows colour-coded in exports
                    ws.Range(i + 2, 1, i + 2, vals.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#cfe2ff");
            }
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
        }

        private static void StyleHeader(IXLWorksheet ws, int cols)
        {
            var rng = ws.Range(1, 1, 1, cols);
            rng.Style.Font.Bold = true;
            rng.Style.Fill.BackgroundColor = XLColor.FromHtml("#1b3a5c");
            rng.Style.Font.FontColor = XLColor.White;
        }

        private bool IsAsia(string? region)
        {
            if (string.IsNullOrWhiteSpace(region)) return false;
            if (_repo.IsAsiaCountry(region)) return true;
            // legacy keyword fallback (city names found in addresses)
            string[] kw = { "KARACHI","LAHORE","ISLAMABAD","PESHAWAR","QUETTA","DUBAI","SHANGHAI",
                            "SINGAPORE","MUMBAI","HONG KONG","TOKYO","BUSAN","JEDDAH" };
            var up = region.ToUpperInvariant();
            return kw.Any(k => up.Contains(k));
        }

        private static string Trunc(string s) => s.Length > 31 ? s[..31] : s;
        private static byte[] ToBytes(XLWorkbook wb) { using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray(); }
        private static string Csv(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
