using ShippingManagement.Web.Data;
using ShippingManagement.Web.Infrastructure;
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
        private readonly ScrapeProgressService _progress;
        public string name = "";
        public string imoMain = "";
        public ScraperService(ShippingRepository repo, IConfiguration cfg, IWebHostEnvironment env,
                              ScrapeProgressService progress)
        { _repo = repo; _cfg = cfg; _env = env; _progress = progress; }

        public record ScrapeResult(bool Ok, string Message, int Inserted, int Sources);

        public ScrapeResult LoadData(DateTime importDate, string? country)
        {
            var sources = _repo.GetAllActiveSources(country).ToList();
            if (sources.Count == 0)
                return new(false, "No active data-source URLs found. Add them in Ports Setup → Sources.", 0, 0);

            // Split the active sources by which website they belong to. A source is
            // treated as VesselTracker when its name or URL mentions "vesseltracker",
            // as MarineVesselTraffic when it mentions "marinevesseltraffic";
            // everything else goes to the MyShipTracking (paginated) scraper.
            var vtSources = sources.Where(IsVesselTracker).ToList();
            var mvtSources = sources.Where(IsMarineVesselTraffic).ToList();
            var mstSources = sources.Where(s => !IsVesselTracker(s) && !IsMarineVesselTraffic(s)).ToList();

            // ETA window (days). Rows whose arrival/ETA is more than this many days
            // in the future are dropped by the scraper. Configurable via
            // appsettings.json → Scraper:MaxEtaDays (defaults to 10).
            int maxDays = int.TryParse(_cfg["Scraper:MaxEtaDays"], out var md) && md > 0 ? md : 10;
            int timeoutMin = int.TryParse(_cfg["Scraper:TimeoutMinutes"], out var t) ? t : 15;
            // VesselTracker logs in and visits every port, so it's inherently slower
            // than the paginated MyShipTracking scrape — give it its own budget.
            int vtTimeoutMin = int.TryParse(_cfg["Scraper:VesselTrackerTimeoutMinutes"], out var vtt) && vtt > 0
                               ? vtt : Math.Max(timeoutMin, 60);
            // MarineVesselTraffic also logs in and walks every pagination page of
            // every port's EXPECTED tab, so give it a VesselTracker-sized budget.
            int mvtTimeoutMin = int.TryParse(_cfg["Scraper:MarineVesselTrafficTimeoutMinutes"], out var mvtt) && mvtt > 0
                                ? mvtt : Math.Max(timeoutMin, 60);

            // Concurrency per site. MyShipTracking opens one browser process per
            // worker (heavier); VesselTracker shares one browser and opens a tab per
            // worker (lighter), so it defaults lower.
            int mstWorkers = int.TryParse(_cfg["Scraper:MaxWorkers"], out var mw) && mw > 0 ? mw : 8;
            int vtWorkers = int.TryParse(_cfg["Scraper:VesselTrackerWorkers"], out var vw) && vw > 0 ? vw : 2;
            // MarineVesselTraffic shares one browser and opens a tab per worker (light).
            int mvtWorkers = int.TryParse(_cfg["Scraper:MarineVesselTrafficWorkers"], out var mvw) && mvw > 0 ? mvw : 2;

            // ── Everything lives in a fixed folder inside the project (NOT temp), with
            //    fixed filenames so each run overwrites the previous one. Configurable
            //    via appsettings.json → Scraper:DataDir (defaults to <project>/ScraperData).
            string dataDir = Val(_cfg["Scraper:DataDir"],
                                 Path.Combine(_env.ContentRootPath, "ScraperData"));
            if (!Path.IsPathRooted(dataDir))
                dataDir = Path.Combine(_env.ContentRootPath, dataDir);
            Directory.CreateDirectory(dataDir);

            // Live progress for the Import Data loading overlay: point the tracker
            // at this run's folder and clear the previous run's sidecar files.
            _progress.BeginRun(dataDir);

            string mstConfigPath = Path.Combine(dataDir, "config_myshiptracking.json");
            string vtConfigPath = Path.Combine(dataDir, "config_vesseltracker.json");
            string mvtConfigPath = Path.Combine(dataDir, "config_marinevesseltraffic.json");
            string mstOutputPath = Path.Combine(dataDir, "output_myshiptracking.json");
            string vtOutputPath = Path.Combine(dataDir, "output_vesseltracker.json");
            string mvtOutputPath = Path.Combine(dataDir, "output_marinevesseltraffic.json");
            string mergedPath = Path.Combine(dataDir, "results.json");   // single file → scraped table

            // 1 — write a config file per website (each filtered to its own sources).
            WriteConfig(mstConfigPath, maxDays, mstWorkers, mstSources);
            WriteConfig(vtConfigPath, maxDays, vtWorkers, vtSources);
            WriteConfig(mvtConfigPath, maxDays, mvtWorkers, mvtSources);

            // Resolve python + both script paths.
            string python = Val(_cfg["Scraper:PythonPath"], "python");
            string? mstScript = ResolveScript(_cfg["Scraper:ScriptPath"], "scraper.py");
            string? vtScript = ResolveScript(_cfg["Scraper:VesselTrackerScriptPath"], "vesseltracker_scraper.py");
            string? mvtScript = ResolveScript(_cfg["Scraper:MarineVesselTrafficScriptPath"], "marinevesseltraffic_scraper.py");

            if (mstSources.Count > 0 && mstScript is null)
                return new(false, "MyShipTracking script (Scripts/scraper.py) not found. " +
                    "Ensure it exists and is set to Copy to Output Directory.", 0, sources.Count);
            if (vtSources.Count > 0 && vtScript is null)
                return new(false, "VesselTracker script (Scripts/vesseltracker_scraper.py) not found. " +
                    "Ensure it exists and is set to Copy to Output Directory.", 0, sources.Count);
            if (mvtSources.Count > 0 && mvtScript is null)
                return new(false, "MarineVesselTraffic script (Scripts/marinevesseltraffic_scraper.py) not found. " +
                    "Ensure it exists and is set to Copy to Output Directory.", 0, sources.Count);

            // 2 — launch BOTH scripts in parallel, each writing its own output file.
            //     (Two processes must not write the SAME file at once — that corrupts
            //     it — so we merge their outputs afterwards into one results.json.)
            _progress.SetPhase($"Scraping {sources.Count} source(s)");

            // ── CPU throttle: ONE Windows Job Object shared by BOTH scraper
            //    processes hard-caps their COMBINED CPU (python + every Chrome
            //    child) at Scraper:MaxCpuPercent of total machine CPU (default
            //    70%), enforced by the kernel, and runs them at BelowNormal
            //    priority so IIS/SQL Server stay responsive during the scrape.
            //    Set MaxCpuPercent to 0 or 100 in appsettings.json to disable.
            int maxCpu = int.TryParse(_cfg["Scraper:MaxCpuPercent"], out var mcp) ? mcp : 70;
            using var cpuLimiter = ScraperCpuLimiter.Create(maxCpu);

            var runs = new List<Task<ScraperRun>>();
            if (mstSources.Count > 0)
                runs.Add(Task.Run(() => RunScraper("MyShipTracking", python, mstScript!,
                                                   mstConfigPath, mstOutputPath, timeoutMin, cpuLimiter)));
            if (vtSources.Count > 0)
                runs.Add(Task.Run(() => RunScraper("VesselTracker", python, vtScript!,
                                                   vtConfigPath, vtOutputPath, vtTimeoutMin, cpuLimiter,
                                                   extraArgs: new[] { "--headless" })));

            ScraperRun[] outcomes;
            try { outcomes = Task.WhenAll(runs).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                return new(false, $"Could not start Python ('{python}'). Install Python and the scraper " +
                                  $"dependencies, or fix Scraper:PythonPath in appsettings.json. Error: {ex.Message}",
                                  0, sources.Count);
            }

            // 2b — STAGE 2: MarineVesselTraffic runs ONLY AFTER MyShipTracking and
            //      VesselTracker have both finished (by design — it's the follow-up
            //      pass, and running it afterwards also keeps the peak browser count
            //      down). Its rows then join the same merge below.
            if (mvtSources.Count > 0)
            {
                _progress.SetPhase($"Scraping MarineVesselTraffic ({mvtSources.Count} source(s))");
                // Pass the site login (appsettings → Scraper:MvtEmail/MvtPassword)
                // to the script as env vars; machine-wide MVT_EMAIL/MVT_PASSWORD
                // env vars still work when these keys are left empty.
                var mvtEnv = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(_cfg["Scraper:MvtEmail"]))
                    mvtEnv["MVT_EMAIL"] = _cfg["Scraper:MvtEmail"]!.Trim();
                if (!string.IsNullOrWhiteSpace(_cfg["Scraper:MvtPassword"]))
                    mvtEnv["MVT_PASSWORD"] = _cfg["Scraper:MvtPassword"]!.Trim();

                // Cloudflare on this site rarely clears for a headless browser, so
                // MVT runs HEADED by default — but with the window parked OFF-SCREEN,
                // so nothing appears on the desktop during a scrape.
                // Scraper:MvtHeadless=true  -> true headless (low pass rate)
                // Scraper:MvtVisible=true   -> show the window (only needed for the
                //                              one-off run where a Cloudflare
                //                              checkbox has to be ticked by hand)
                bool mvtHeadless = bool.TryParse(_cfg["Scraper:MvtHeadless"], out var mh) && mh;
                bool mvtVisible = bool.TryParse(_cfg["Scraper:MvtVisible"], out var mv) && mv;
                mvtEnv["SCRAPER_FORCE_HEADLESS"] = mvtHeadless ? "1" : "0";   // overrides the default "1"
                mvtEnv["SCRAPER_MVT_VISIBLE"] = mvtVisible ? "1" : "0";

                var mvtRun = RunScraper("MarineVesselTraffic", python, mvtScript!,
                                        mvtConfigPath, mvtOutputPath, mvtTimeoutMin, cpuLimiter,
                                        extraArgs: mvtHeadless ? new[] { "--headless" } : null,
                                        extraEnv: mvtEnv);
                outcomes = outcomes.Append(mvtRun).ToArray();
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
                    Convert.ToDateTime(r.ArrivalDate).Date >= Convert.ToDateTime(importDate.AddDays(-10)).Date &&
                    Convert.ToDateTime(r.ArrivalDate).Date <= Convert.ToDateTime(importDate).Date;
                    if (!isWithinLast10Days || Convert.ToDateTime(r.ArrivalDate).Date == Convert.ToDateTime(importDate).Date)
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

            _progress.SetPhase("Importing rows into the database");
            _repo.InsertScrapedRows(records);   // useless IMOs auto-flagged on insert

            // ── Per-site outcome summary. Previously a site's failure was ONLY
            //    reported when BOTH sites returned nothing, so "MyShipTracking OK
            //    + VesselTracker crashed" looked like a clean success and the VT
            //    error was invisible. Now every run reports each site's result.
            var siteBits = new List<string>();
            foreach (var run in outcomes)
            {
                if (!run.Ok)
                    siteBits.Add($"{run.Site} FAILED: {Truncate(run.Error, 250)}");
                else if (run.Rows.Count == 0)
                    siteBits.Add($"{run.Site}: 0 rows — check ScraperData\\output_" +
                                 (run.Site == "VesselTracker" ? "vesseltracker"
                                  : run.Site == "MarineVesselTraffic" ? "marinevesseltraffic"
                                  : "myshiptracking") +
                                 ".log and the ScraperData\\debug folder");
                else
                    siteBits.Add($"{run.Site}: {run.Rows.Count} rows");
            }
            string siteSummary = siteBits.Count > 0 ? $" [{string.Join(" | ", siteBits)}]" : "";

            return new(true,
                $"Scrapping completed.{siteSummary} Now " +
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

        /// <summary>True when a source belongs to MarineVesselTraffic (by source name or URL).</summary>
        private static bool IsMarineVesselTraffic(ScrapeSourceInfo s) =>
            (s.SourceName?.Replace(" ", "").Contains("marinevesseltraffic", StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.Url?.Contains("marinevesseltraffic", StringComparison.OrdinalIgnoreCase) ?? false);

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
                                      ScraperCpuLimiter cpuLimiter,
                                      string[]? extraArgs = null,
                                      IDictionary<string, string>? extraEnv = null)
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

            // Web-app runs must NEVER pop a visible browser window on the server
            // (a leftover SCRAPER_HEADFUL=1 from a debugging session would make
            // all Selenium workers open on-screen as big blank white windows).
            // This overrides any machine-wide headful setting for THIS process.
            psi.EnvironmentVariables["SCRAPER_FORCE_HEADLESS"] = "1";
            psi.EnvironmentVariables["SCRAPER_HEADFUL"] = "0";
            foreach (var kv in extraEnv ?? new Dictionary<string, string>())
                psi.EnvironmentVariables[kv.Key] = kv.Value;

            string stderr;
            try
            {
                using var proc = Process.Start(psi)!;
                // Cap + deprioritise this process (and all its Chrome children)
                // via the run's shared job object — see LoadData.
                cpuLimiter.Attach(proc);
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