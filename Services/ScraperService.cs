using System.Diagnostics;
using System.Text.Json;
using ShippingManagement.Web.Data;
using ShippingManagement.Web.Models;

namespace ShippingManagement.Web.Services
{
    /// <summary>
    /// Runs the Python/Selenium scraper (Scripts/scraper.py) for the "Load Data" button:
    ///   1. Collects all active URLs from Ports Setup → Data Sources
    ///   2. Writes them as a config JSON and launches: python scraper.py config.json output.json
    ///   3. Reads the output JSON and inserts the rows into ScrapedData
    /// Requirements on the server: Python 3, `pip install selenium`, Google Chrome + chromedriver.
    /// Configure paths in appsettings.json → "Scraper".
    /// </summary>
    public class ScraperService
    {
        private readonly ShippingRepository _repo;
        private readonly IConfiguration _cfg;
        private readonly IWebHostEnvironment _env;

        public ScraperService(ShippingRepository repo, IConfiguration cfg, IWebHostEnvironment env)
        { _repo = repo; _cfg = cfg; _env = env; }

        public record ScrapeResult(bool Ok, string Message, int Inserted, int Sources);

        public ScrapeResult LoadData(DateTime importDate, string? country)
        {
            List<VesselType> vesselTypes = _repo.GetAllVesselTypes().ToList();
            var sources = _repo.GetAllActiveSources(country).ToList();
            if (sources.Count == 0)
                return new(false, "No active data-source URLs found. Add them in Ports Setup → Sources.", 0, 0);

            // 1 — write config for the python script
            string workDir = Path.Combine(Path.GetTempPath(), "ShippingScraper");
            Directory.CreateDirectory(workDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string configPath = Path.Combine(workDir, $"config_{stamp}.json");
            string outputPath = Path.Combine(workDir, $"myshiptracking_{stamp}.json");

            var config = new
            {
                sources = sources.Select(s => new
                {
                    sourceId = s.SourceID,
                    sourceName = s.SourceName,
                    portId = s.PortID,
                    portName = s.PortName,
                    country = s.CountryName,
                    url = s.Url,
                    pageParamPattern = s.PageParamPattern,
                    startPage = s.StartPage,
                    endPage = s.EndPage,
                    maxPages = s.MaxPages          // "first 50 pages" rule cap
                })
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            // 2 — run the python script
            // NOTE: use IsNullOrWhiteSpace, not ?? — an empty "" value in appsettings.json
            // is NOT null, so ?? would happily return it and File.Exists("") always fails.
            string python = Val(_cfg["Scraper:PythonPath"], "python");
            string script = Val(_cfg["Scraper:ScriptPath"],
                                Path.Combine(_env.ContentRootPath, "Scripts", "scraper.py"));
            int timeoutMin = int.TryParse(_cfg["Scraper:TimeoutMinutes"], out var t) ? t : 30;

            if (!Path.IsPathRooted(script))
                script = Path.Combine(_env.ContentRootPath, script);

            if (!File.Exists(script))
            {
                // help diagnose: also check the build-output copy of Scripts/scraper.py
                string alt = Path.Combine(AppContext.BaseDirectory, "Scripts", "scraper.py");
                if (File.Exists(alt)) script = alt;
                else return new(false,
                    $"Scraper script not found. Looked at: '{script}' and '{alt}'. " +
                    "Make sure Scripts/scraper.py exists in the project (Copy to Output Directory = Copy if newer) " +
                    "or set an absolute path in appsettings.json → Scraper:ScriptPath.", 0, sources.Count);
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                ArgumentList = { script, configPath, outputPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };

            string stderr;
            try
            {
                using var proc = Process.Start(psi)!;
                // read stderr on a background task while draining stdout — sequential
                // ReadToEnd() on both streams can deadlock when one pipe buffer fills
                var errTask = proc.StandardError.ReadToEndAsync();
                string _ = proc.StandardOutput.ReadToEnd();          // progress lines
                if (!proc.WaitForExit((int)TimeSpan.FromMinutes(timeoutMin).TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return new(false, $"Scraper timed out after {timeoutMin} minute(s). Reduce End Page / Max Pages on the sources.", 0, sources.Count);
                }
                stderr = errTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                    return new(false, $"Scraper failed (exit {proc.ExitCode}): {Truncate(stderr, 400)}", 0, sources.Count);
            }
            catch (Exception ex)
            {
                return new(false, $"Could not start Python ('{python}'). Install Python + Selenium + ChromeDriver, " +
                                  $"or fix Scraper:PythonPath in appsettings.json. Error: {ex.Message}", 0, sources.Count);
            }

            // 3 — load the JSON output the script produced and import it
            if (!File.Exists(outputPath))
                return new(false, "Scraper finished but produced no output file. " + Truncate(stderr, 300), 0, sources.Count);

            List<ScrapedJsonRow>? rows;
            try
            {
                rows = JsonSerializer.Deserialize<List<ScrapedJsonRow>>(
                    File.ReadAllText(outputPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return new(false, $"Could not parse scraper output JSON: {ex.Message}", 0, sources.Count);
            }
            if (rows is null || rows.Count == 0)
                return new(true, "Scrape completed but no recent vessels matched the recency filter.", 0, sources.Count);

            var records = new List<ScrapedRecord>();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.VesselName)) continue;
                string? imo = SanitizeImo(r.IMO_Number);                    // junk ('---','0','') -> null
                imo ??= SanitizeImo(_repo.LookupIMOByVesselName(r.VesselName.Trim()));   // auto IMO detection by name
                bool exists = vesselTypes.Any(v =>
                    string.Equals(v.TypeName, r.VesselType,
                  StringComparison.OrdinalIgnoreCase));

                if (exists)
                {
                    records.Add(new ScrapedRecord
                    {
                        VesselName = r.VesselName.Trim(),
                        IMO_Number = imo,
                        IsMatched = _repo.GetVesselByIMO(imo) != null,//imo is not null,
                        PortID = r.PortID,
                        PortName = r.PortName ?? "",
                        Country = r.Country ?? "",
                        ArrivalDate = r.ArrivalDate,
                        Origin = r.Origin,
                        VesselStatus = r.VesselStatus,
                        VesselType = r.VesselType,
                        DataSource = r.DataSource ?? "Scraper",
                        ImportDate = importDate.Date
                    });
                }
                
            }

            _repo.InsertScrapedRows(records);   // useless IMOs auto-flagged on insert
            return new(true,
                $"Load Data complete: {records.Count} row(s) imported from {sources.Count} source URL(s). " +
                "Run Auto Data to distribute them to users.", records.Count, sources.Count);
        }

        /// <summary>A real IMO is exactly 7 digits — anything else ('---', '0', blanks) becomes null
        /// so it can never collide in dedupe or match the global UselessVessels list.</summary>
        private static string? SanitizeImo(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return digits.Length == 7 ? digits : null;
        }

        private static string Val(string? configured, string fallback) =>
            string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

        private static string Truncate(string s, int len) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s[..len] + "…");

        /// <summary>Shape of one row in the scraper's output JSON.</summary>
        private sealed class ScrapedJsonRow
        {
            public string VesselName { get; set; } = "";
            public string? IMO_Number { get; set; }
            public string? VesselType { get; set; }
            public string? Origin { get; set; }
            public string? VesselStatus { get; set; }
            public string? ArrivalDate { get; set; }
            public int? PortID { get; set; }
            public string? PortName { get; set; }
            public string? Country { get; set; }
            public string? DataSource { get; set; }
        }
    }
}
