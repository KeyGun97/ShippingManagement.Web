using System.Text.Json;

namespace ShippingManagement.Web.Services
{
    /// <summary>
    /// Tracks ONE "Fetch Data" scrape job at a time and turns it into a live
    /// percentage + estimated-time-remaining for the Import Data loading overlay.
    ///
    /// How progress is measured (real, not fake):
    ///   • Each Python scraper writes a tiny sidecar file next to its output —
    ///     output_myshiptracking.progress.json / output_vesseltracker.progress.json —
    ///     containing { done, total, phase } which it rewrites after every source.
    ///   • This service reads both sidecars on each poll and combines them into
    ///     a single done/total fraction across all sources.
    ///   • Percent model: 4% launch warm-up → 4%..96% proportional to sources
    ///     completed → 96% while importing rows → 100% on completion.
    ///   • No time estimate is shown: MyShipTracking pages (seconds each) and
    ///     VesselTracker ports (much slower) share one unit pool, so a blended
    ///     ETA was structurally misleading ("5 sec left" while dozens of slow
    ///     ports remained). The overlay shows the live vessels-found count
    ///     instead — real, always accurate, and more meaningful to the user.
    /// </summary>
    public class ScrapeProgressService
    {
        private readonly object _lock = new();

        // ── job state ──────────────────────────────────────────────────────
        private bool _running;
        private DateTime _startedUtc;
        private string _dataDir = "";
        private string _phase = "";          // human text, e.g. "Scraping sources"
        private double _lastPercent;         // monotonic — never goes backwards

        // final outcome (kept until consumed by the page banner after reload)
        private bool _hasResult;
        private bool _resultOk;
        private string _resultMessage = "";
        private int _resultInserted;

        private static readonly string[] SidecarFiles =
        {
            "output_myshiptracking.progress.json",
            "output_vesseltracker.progress.json",
            "output_marinevesseltraffic.progress.json",
        };

        public bool IsRunning { get { lock (_lock) return _running; } }

        /// <summary>Claim the job slot. False = a scrape is already running.</summary>
        public bool TryStart()
        {
            lock (_lock)
            {
                if (_running) return false;
                _running = true;
                _startedUtc = DateTime.UtcNow;
                _phase = "Preparing scrape";
                _lastPercent = 0;
                _hasResult = false;
                _resultMessage = "";
                return true;
            }
        }

        /// <summary>Called by ScraperService once it knows where the sidecars live.
        /// Also deletes stale sidecars from the previous run so the overlay never
        /// shows last run's 100%.</summary>
        public void BeginRun(string dataDir)
        {
            string dir = dataDir ?? "";
            lock (_lock)
            {
                _dataDir = dir;
                _phase = "Launching scrapers";
            }
            if (dir.Length == 0) return;
            foreach (var f in SidecarFiles)
            {
                try { File.Delete(Path.Combine(dir, f)); } catch { /* best effort */ }
            }
        }

        public void SetPhase(string phase)
        {
            lock (_lock) _phase = phase;
        }

        /// <summary>Job finished (success or failure). The overlay snaps to 100%,
        /// and the result banner is stored until the next page render consumes it.</summary>
        public void Complete(bool ok, string message, int inserted)
        {
            lock (_lock)
            {
                _running = false;
                _lastPercent = 100;
                _phase = ok ? "Completed" : "Failed";
                _hasResult = true;
                _resultOk = ok;
                _resultMessage = message ?? "";
                _resultInserted = inserted;
            }
        }

        /// <summary>One-shot: hand the final banner text to the page after reload.</summary>
        public bool TryConsumeResult(out bool ok, out string message)
        {
            lock (_lock)
            {
                ok = _resultOk; message = _resultMessage;
                if (!_hasResult || _running) return false;
                _hasResult = false;
                return true;
            }
        }

        /// <summary>Polled by the overlay (GET /ImportData/LoadDataProgress).</summary>
        public object Snapshot()
        {
            bool running; DateTime started; string dataDir, phase;
            bool hasResult; bool resultOk; string resultMsg; int inserted;
            lock (_lock)
            {
                running = _running; started = _startedUtc; dataDir = _dataDir;
                phase = _phase;
                hasResult = _hasResult; resultOk = _resultOk; resultMsg = _resultMessage;
                inserted = _resultInserted;
            }

            if (!running)
            {
                return new
                {
                    running = false,
                    done = hasResult,
                    ok = resultOk,
                    message = resultMsg,
                    inserted,
                    percent = hasResult ? 100 : 0,
                    rowsFound = hasResult ? inserted : 0,
                    elapsedSeconds = 0d,
                    phase,
                    sourcesDone = 0,
                    sourcesTotal = 0,
                    siteDetail = "",
                };
            }

            // ── combine both sidecar files into one done/total fraction ──────
            int doneSum = 0, totalSum = 0, rowsSum = 0;
            var siteBits = new List<string>();
            string? scraperPhase = null;
            foreach (var f in SidecarFiles)
            {
                var s = ReadSidecar(Path.Combine(dataDir, f));
                if (s is null) continue;
                doneSum += s.Done; totalSum += s.Total; rowsSum += s.Rows;
                string site = f.Contains("marinevesseltraffic") ? "MarineVesselTraffic"
                              : f.Contains("vesseltracker") ? "VesselTracker"
                              : "MyShipTracking";
                if (s.Total > 0) siteBits.Add($"{site} {s.Done}/{s.Total}");
                if (!string.IsNullOrWhiteSpace(s.Phase) && s.Done < s.Total)
                    scraperPhase ??= s.Phase;
            }

            double elapsed = (DateTime.UtcNow - started).TotalSeconds;

            // Percent model: 4% warm-up + 92% × source fraction, capped at 96
            // until Complete() snaps it to 100 (the final DB import is quick).
            double frac = totalSum > 0 ? (double)doneSum / totalSum : 0;
            double pct = totalSum > 0 ? 4 + 92 * frac : Math.Min(4, elapsed / 3.0);
            pct = Math.Min(96, pct);

            // Monotonic: the bar never moves backwards between polls.
            double percent;
            lock (_lock)
            {
                _lastPercent = Math.Max(_lastPercent, pct);
                percent = _lastPercent;
            }

            string phaseText = doneSum >= totalSum && totalSum > 0
                ? "Finishing up — importing rows"
                : scraperPhase is not null
                    ? Capitalize(scraperPhase)
                    : phase;

            return new
            {
                running = true,
                done = false,
                ok = (bool?)null,
                message = "",
                inserted = 0,
                percent = Math.Round(percent, 1),
                rowsFound = rowsSum,
                elapsedSeconds = Math.Round(elapsed),
                phase = phaseText,
                sourcesDone = doneSum,
                sourcesTotal = totalSum,
                siteDetail = string.Join("  ·  ", siteBits),
            };
        }

        private sealed record Sidecar(int Done, int Total, int Rows, string? Phase);

        private static Sidecar? ReadSidecar(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                int done = root.TryGetProperty("done", out var d) ? d.GetInt32() : 0;
                int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                int rows = root.TryGetProperty("rows", out var r) ? r.GetInt32() : 0;
                string? ph = root.TryGetProperty("phase", out var p) ? p.GetString() : null;
                return new(done, total, rows, ph);
            }
            catch { return null; }   // mid-write / malformed → skip this poll
        }

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}