using ShippingManagement.Web.Data;
using ShippingManagement.Web.Models;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
        public string name = "";
        public string imoMain = "";
        public ScraperService(ShippingRepository repo, IConfiguration cfg, IWebHostEnvironment env)
        { _repo = repo; _cfg = cfg; _env = env; }

        public record ScrapeResult(bool Ok, string Message, int Inserted, int Sources);

        public ScrapeResult LoadData(DateTime importDate, string? country)
        {
            var sources = _repo.GetAllActiveSources(country).ToList();
            if (sources.Count == 0)
                return new(false, "No active data-source URLs found. Add them in Ports Setup → Sources.", 0, 0);

            // Split the active sources by which website they belong to. A source is
            // treated as VesselTracker when its name or URL mentions "vesseltracker";
            // everything else goes to the MyShipTracking (paginated) scraper.
            var vtSources = sources.Where(IsVesselTracker).ToList();
            var mstSources = sources.Where(s => !IsVesselTracker(s)).ToList();

            // ETA window (days). Rows whose arrival/ETA is more than this many days
            // in the future are dropped by the scraper. Configurable via
            // appsettings.json → Scraper:MaxEtaDays (defaults to 10).
            int maxDays = int.TryParse(_cfg["Scraper:MaxEtaDays"], out var md) && md > 0 ? md : 10;
            int timeoutMin = int.TryParse(_cfg["Scraper:TimeoutMinutes"], out var t) ? t : 15;
            // VesselTracker logs in and visits every port, so it's inherently slower
            // than the paginated MyShipTracking scrape — give it its own budget.
            int vtTimeoutMin = int.TryParse(_cfg["Scraper:VesselTrackerTimeoutMinutes"], out var vtt) && vtt > 0
                               ? vtt : Math.Max(timeoutMin, 20);

            // Concurrency per site. MyShipTracking opens one browser process per
            // worker (heavier); VesselTracker shares one browser and opens a tab per
            // worker (lighter), so it defaults lower.
            int mstWorkers = int.TryParse(_cfg["Scraper:MaxWorkers"], out var mw) && mw > 0 ? mw : 8;
            int vtWorkers = int.TryParse(_cfg["Scraper:VesselTrackerWorkers"], out var vw) && vw > 0 ? vw : 4;

            // ── Everything lives in a fixed folder inside the project (NOT temp), with
            //    fixed filenames so each run overwrites the previous one. Configurable
            //    via appsettings.json → Scraper:DataDir (defaults to <project>/ScraperData).
            string dataDir = Val(_cfg["Scraper:DataDir"],
                                 Path.Combine(_env.ContentRootPath, "ScraperData"));
            if (!Path.IsPathRooted(dataDir))
                dataDir = Path.Combine(_env.ContentRootPath, dataDir);
            Directory.CreateDirectory(dataDir);

            string mstConfigPath = Path.Combine(dataDir, "config_myshiptracking.json");
            string vtConfigPath = Path.Combine(dataDir, "config_vesseltracker.json");
            string mstOutputPath = Path.Combine(dataDir, "output_myshiptracking.json");
            string vtOutputPath = Path.Combine(dataDir, "output_vesseltracker.json");
            string mergedPath = Path.Combine(dataDir, "results.json");   // single file → scraped table

            // 1 — write a config file per website (each filtered to its own sources).
            WriteConfig(mstConfigPath, maxDays, mstWorkers, mstSources);
            WriteConfig(vtConfigPath, maxDays, vtWorkers, vtSources);

            // Resolve python + both script paths.
            string python = Val(_cfg["Scraper:PythonPath"], "python");
            string? mstScript = ResolveScript(_cfg["Scraper:ScriptPath"], "scraper.py");
            string? vtScript = ResolveScript(_cfg["Scraper:VesselTrackerScriptPath"], "vesseltracker_scraper.py");

            if (mstSources.Count > 0 && mstScript is null)
                return new(false, "MyShipTracking script (Scripts/scraper.py) not found. " +
                    "Ensure it exists and is set to Copy to Output Directory.", 0, sources.Count);
            if (vtSources.Count > 0 && vtScript is null)
                return new(false, "VesselTracker script (Scripts/vesseltracker_scraper.py) not found. " +
                    "Ensure it exists and is set to Copy to Output Directory.", 0, sources.Count);

            // 2 — launch BOTH scripts in parallel, each writing its own output file.
            //     (Two processes must not write the SAME file at once — that corrupts
            //     it — so we merge their outputs afterwards into one results.json.)
            var runs = new List<Task<ScraperRun>>();
            if (mstSources.Count > 0)
                runs.Add(Task.Run(() => RunScraper("MyShipTracking", python, mstScript!,
                                                   mstConfigPath, mstOutputPath, timeoutMin)));
            if (vtSources.Count > 0)
                runs.Add(Task.Run(() => RunScraper("VesselTracker", python, vtScript!,
                                                   vtConfigPath, vtOutputPath, vtTimeoutMin, extraArgs: new[] { "--headless" })));

            ScraperRun[] outcomes;
            try { outcomes = Task.WhenAll(runs).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                return new(false, $"Could not start Python ('{python}'). Install Python and the scraper " +
                                  $"dependencies, or fix Scraper:PythonPath in appsettings.json. Error: {ex.Message}",
                                  0, sources.Count);
            }

            // 3 — merge each successful scraper's output into ONE combined list, then
            //     persist it as results.json (the single JSON that feeds the table).
            var rows = new List<ScrapedJsonRow>();
            var seenKeys = new HashSet<string>();
            var problems = new List<string>();

            foreach (var run in outcomes)
            {
                if (!run.Ok) { problems.Add($"{run.Site}: {Truncate(run.Error, 200)}"); continue; }
                foreach (var r in run.Rows)
                {
                    if (string.IsNullOrWhiteSpace(r.VesselName)) continue;
                    // de-duplicate across the two sites by (IMO|name)+PortID, mirroring
                    // the per-site dedupe the python scrapers already do internally.
                    string key = $"{(string.IsNullOrWhiteSpace(r.IMO_Number) ? r.VesselName : r.IMO_Number)!.Trim()}|{r.PortID}";
                    if (!seenKeys.Add(key)) continue;
                    rows.Add(r);
                }
            }

            try
            {
                File.WriteAllText(mergedPath,
                    JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal: import still proceeds from the in-memory merge */ }

            // If BOTH scrapers failed (and there was nothing to import), surface the error.
            if (rows.Count == 0)
            {
                if (problems.Count > 0)
                    return new(false, "Scrape failed. " + string.Join("  |  ", problems), 0, sources.Count);
                return new(true, "Scrape completed but no recent vessels matched the recency filter.", 0, sources.Count);
            }

            var records = new List<ScrapedRecord>();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.VesselName)) continue;
                string? imo = string.IsNullOrWhiteSpace(r.IMO_Number) ? null : r.IMO_Number.Trim();
                imo ??= _repo.LookupIMOByVesselName(r.VesselName.Trim());   // auto IMO detection by name
                if (!string.IsNullOrEmpty(r.VesselName.Trim()) && r.VesselName.Trim().Contains("\nIMO:"))
                {
                    string[] parts = r.VesselName.Trim().Split('\n');

                    name = parts[0].Trim();

                    int colonIndex = parts[1].IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        imoMain = parts[1].Substring(colonIndex + 1).Trim();
                        // You may want to use imoNumber here, e.g., assign to 'imo'
                    }
                }
                else
                {
                    name = r.VesselName.Trim();
                    imoMain = imo;
                }
                if (DateTime.TryParse(r.ArrivalDate, out DateTime date))
                {
                    bool isWithinLast10Days =
                    Convert.ToDateTime(r.ArrivalDate).Date >= importDate.AddDays(-10) &&
                    Convert.ToDateTime(r.ArrivalDate).Date <= importDate;
                    if (isWithinLast10Days)
                    {
                        records.Add(new ScrapedRecord
                        {
                            VesselName = name,
                            IMO_Number = imoMain,
                            IsMatched = _repo.GetVesselByIMO(imoMain) != null,
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
                else
                {
                    records.Add(new ScrapedRecord
                    {
                        VesselName = name,
                        IMO_Number = imoMain,
                        IsMatched = _repo.GetVesselByIMO(imoMain) != null,
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
                $"Scrapping completed Now " +
                "Run Auto Data to distribute them to users.", records.Count, sources.Count);
        }

        private static string Val(string? configured, string fallback) =>
            string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

        private static string Truncate(string s, int len) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s[..len] + "…");

        // Python prints the exception type + message at the END of a traceback, so
        // when a scraper fails we surface the TAIL of stderr, not the head.
        private static string Tail(string s, int len) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : "…" + s[^len..]);

        /// <summary>True when a source belongs to VesselTracker (by source name or URL).</summary>
        private static bool IsVesselTracker(ScrapeSourceInfo s) =>
            (s.SourceName?.Replace(" ", "").Contains("vesseltracker", StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.Url?.Contains("vesseltracker", StringComparison.OrdinalIgnoreCase) ?? false);

        /// <summary>Write one per-site config file (overwrites any existing one).</summary>
        private static void WriteConfig(string path, int maxDays, int maxWorkers, List<ScrapeSourceInfo> sources)
        {
            var config = new
            {
                maxDays,
                maxWorkers,
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
            File.WriteAllText(path, JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Resolve a script path from config or the default Scripts/&lt;fileName&gt;.
        /// Checks the content root first, then the build-output copy. Returns null
        /// if the script can't be found anywhere.
        /// </summary>
        private string? ResolveScript(string? configured, string fileName)
        {
            string script = Val(configured, Path.Combine(_env.ContentRootPath, "Scripts", fileName));
            if (!Path.IsPathRooted(script))
                script = Path.Combine(_env.ContentRootPath, script);
            if (File.Exists(script)) return script;

            string alt = Path.Combine(AppContext.BaseDirectory, "Scripts", fileName);
            return File.Exists(alt) ? alt : null;
        }

        /// <summary>Outcome of one scraper process: parsed rows plus any error text.</summary>
        private sealed record ScraperRun(string Site, bool Ok, string Error, List<ScrapedJsonRow> Rows);

        /// <summary>
        /// Run one python scraper as: python &lt;script&gt; &lt;config&gt; &lt;output&gt; [extraArgs...],
        /// wait up to timeoutMin, and parse its output JSON. Safe to call from
        /// multiple threads in parallel — each call uses its own config/output paths.
        /// </summary>
        private ScraperRun RunScraper(string site, string python, string script,
                                      string configPath, string outputPath, int timeoutMin,
                                      string[]? extraArgs = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The Python scrapers emit UTF-8 (they reconfigure their streams),
                // so decode their output as UTF-8 rather than the OS default code
                // page — otherwise symbols/accents in captured logs become mojibake.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add(outputPath);
            foreach (var a in extraArgs ?? Array.Empty<string>())
                psi.ArgumentList.Add(a);

            string stderr;
            try
            {
                using var proc = Process.Start(psi)!;
                // Drain both pipes concurrently — sequential ReadToEnd() can deadlock
                // when one pipe buffer fills.
                var errTask = proc.StandardError.ReadToEndAsync();
                var outTask = proc.StandardOutput.ReadToEndAsync();   // progress lines
                if (!proc.WaitForExit((int)TimeSpan.FromMinutes(timeoutMin).TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return new(site, false,
                        $"timed out after {timeoutMin} minute(s). Reduce End Page / Max Pages on the sources.",
                        new());
                }
                stderr = errTask.GetAwaiter().GetResult();
                _ = outTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                    return new(site, false, $"exit {proc.ExitCode}: {Tail(stderr, 700)}", new());
            }
            catch (Exception ex)
            {
                return new(site, false, ex.Message, new());
            }

            if (!File.Exists(outputPath))
                return new(site, false, "finished but produced no output file. " + Tail(stderr, 500), new());

            try
            {
                var parsed = JsonSerializer.Deserialize<List<ScrapedJsonRow>>(
                    File.ReadAllText(outputPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new(site, true, "", parsed ?? new());
            }
            catch (Exception ex)
            {
                return new(site, false, $"could not parse output JSON: {ex.Message}", new());
            }
        }

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