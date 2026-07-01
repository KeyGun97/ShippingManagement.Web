"""
Shipping Management — web scraper
Called by the web app's "Fetch Data" button (ImportData module).

Usage:
    python scraper.py <config.json> <output.json>

Architecture
------------
MyShipTrackingScraper — paginated HTML-table parser, no login required.
Builds ONE URL per page in the configured Start..End range
(e.g. Start=1, End=3  ->  &page=1, &page=2, &page=3) and scrapes each.

Sources are scraped in parallel across a small browser pool
(see scrape_myshiptracking_sources / _mst_worker).

Date window
-----------
A record is kept only when its ETA / arrival value is within `maxDays`
(default 10) days of today — in EITHER direction. Anything confidently more
than `maxDays` in the future, or more than `maxDays` days in the past, is
dropped. This applies to relative values ("7 d", "8 h, 57 min" — treated as
"received/last-seen X ago") and absolute timestamps ("2025-11-03 19:42")
alike. Only genuinely unparseable values are kept rather than dropped.

config.json (written by the app from Ports Setup -> Data Sources):
{
  "maxWorkers": 4,                 # optional; concurrent browsers (default 8)
  "maxDays": 10,                   # optional; ETA window in days (default 10)
  "sources": [
    {
      "sourceId": 1,
      "sourceName": "MyShipTracking",
      "portId": 3, "portName": "Houston", "country": "United States",
      "url": "https://www.myshiptracking.com/vessels?...&pp=50",
      "pageParamPattern": "&page={page}",   # optional; default is "&page={page}"
      "startPage": 1, "endPage": 3, "maxPages": 50
    }
  ]
}

output.json: flat list of scraped vessel rows the app imports into ScrapedData.
"""

import json
import os
import re
import sys
import threading as _threading
from datetime import datetime, timedelta
from pathlib import Path
from queue import Queue as _Queue, Empty as _QueueEmpty

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.chrome.options import Options
from selenium.webdriver.support.ui import WebDriverWait
from selenium.common.exceptions import TimeoutException, WebDriverException


# Default ETA window (days). A run may override via config "maxDays"/"maxEtaDays".
DEFAULT_MAX_DAYS = 10

# How long to wait for a myshiptracking results table to render before treating
# the page as empty. Lower = faster on quiet ports. Override with SCRAPER_PAGE_WAIT.
PAGE_WAIT_SECONDS = float(os.environ.get("SCRAPER_PAGE_WAIT", "6") or 6)


# ════════════════════════════════════════════════════════════════════════
#  Browser
# ════════════════════════════════════════════════════════════════════════
def build_driver(headless: bool = True, implicit_wait: float = 0):
    chrome_options = Options()
    # Set SCRAPER_HEADFUL=1 to watch the browser locally while debugging selectors.
    headful = os.environ.get("SCRAPER_HEADFUL", "").strip().lower() in ("1", "true", "yes")
    if headless and not headful:
        chrome_options.add_argument("--headless=new")   # Run in background
    chrome_options.add_argument("--disable-gpu")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--disable-dev-shm-usage")   # safer under concurrency
    chrome_options.add_argument("--window-size=1920,1080")
    chrome_options.add_argument("--lang=en-US")
    # ── Speed flags: skip images/ads/extensions, don't wait for the whole page ──
    chrome_options.add_argument("--blink-settings=imagesEnabled=false")
    chrome_options.add_argument("--disable-extensions")
    chrome_options.add_argument("--disable-background-networking")
    chrome_options.add_argument("--disable-default-apps")
    chrome_options.add_argument("--disable-sync")
    chrome_options.add_argument("--mute-audio")
    # "eager" = return as soon as the DOM is ready (don't block on images/sub-resources).
    chrome_options.page_load_strategy = "eager"
    chrome_options.add_experimental_option("prefs", {
        "profile.managed_default_content_settings.images": 2,   # block images
        "profile.default_content_setting_values.notifications": 2,
    })
    chrome_options.add_argument(                            # look like a normal browser
        "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36")
    driver = webdriver.Chrome(options=chrome_options)
    # Hard cap on a single navigation so one hung port can't stall the whole run.
    try:
        driver.set_page_load_timeout(35)
    except WebDriverException:
        pass
    driver.implicitly_wait(implicit_wait)
    return driver


# ════════════════════════════════════════════════════════════════════════
#  Date parsing helpers (shared)
# ════════════════════════════════════════════════════════════════════════
_MONTHS = {m: i for i, m in enumerate(
    ["", "jan", "feb", "mar", "apr", "may", "jun",
     "jul", "aug", "sep", "oct", "nov", "dec"])}


def _safe_dt(y, mo, d, hh=0, mm=0):
    try:
        return datetime(y, mo, d, hh, mm)
    except (ValueError, TypeError):
        return None


def _infer_year(mo, d):
    """No year given → assume this year, but roll to next year if that date is
    already well in the past (handles Dec→Jan wrap on 'expected arrivals')."""
    now = datetime.now()
    cand = _safe_dt(now.year, mo, d)
    if cand and cand < now - timedelta(days=60):
        return now.year + 1
    return now.year


def _parse_abs_date(s: str):
    """Best-effort absolute-date parse across common formats. Returns datetime or None."""
    # ISO: 2026-06-25 [14:30]
    m = re.search(r'(\d{4})-(\d{1,2})-(\d{1,2})(?:[ t](\d{1,2}):(\d{2}))?', s)
    if m:
        return _safe_dt(int(m[1]), int(m[2]), int(m[3]), int(m[4] or 0), int(m[5] or 0))
    # DMY with separators: 25/06/2026, 25.06.2026, 25-06-26
    m = re.search(r'\b(\d{1,2})[./-](\d{1,2})[./-](\d{2,4})\b', s)
    if m:
        y = int(m[3]); y += 2000 if y < 100 else 0
        return _safe_dt(y, int(m[2]), int(m[1]))
    # "25 jun 2026" / "25 jun"
    m = re.search(r'\b(\d{1,2})\s+([a-z]{3,})\.?(?:\s+(\d{4}))?', s)
    if m and m[2][:3] in _MONTHS:
        mo = _MONTHS[m[2][:3]]; d = int(m[1])
        return _safe_dt(int(m[3]) if m[3] else _infer_year(mo, d), mo, d)
    # "jun 25 2026" / "jun 25, 2026" / "jun 25"
    m = re.search(r'\b([a-z]{3,})\.?\s+(\d{1,2})(?:,?\s*(\d{4}))?', s)
    if m and m[1][:3] in _MONTHS:
        mo = _MONTHS[m[1][:3]]; d = int(m[2])
        return _safe_dt(int(m[3]) if m[3] else _infer_year(mo, d), mo, d)
    return None


# ════════════════════════════════════════════════════════════════════════
#  Base scraper — shared driver + the ETA/date window filter
# ════════════════════════════════════════════════════════════════════════
class BaseScraper:
    """Common behaviour for every per-website scraper class."""

    parser_name = "base"

    def __init__(self, driver, max_days: int = DEFAULT_MAX_DAYS):
        self.driver = driver
        self.max_days = int(max_days or DEFAULT_MAX_DAYS)

    # ── the single source of truth for "is this ETA within the window?" ──
    def within_date_window(self, value) -> bool:
        """Keep a row only when its ETA / arrival is within the next `max_days`.

        Returns False ONLY when the value is confidently MORE than `max_days`
        in the FUTURE — that's the row we drop. This now correctly handles
        absolute timestamps like "2025-11-03 19:42", not just relative values
        like "3 d". Already-arrived / past and unparseable values are kept
        (we never drop on uncertainty)."""
        max_days = self.max_days
        if value is None:
            return True
        s = str(value).strip().lower()
        if not s or s in ("n/a", "na", "-", "—"):
            return True
        if re.search(r'\bnow\b', s) or re.search(r'\btoday\b', s):
            return True                                   # "Now" / "Just now" / etc → always keep

        # ── relative durations ─────────────────────────────────────────
        if "tomorrow" in s:
            return max_days >= 1
        if any(u in s for u in ("min", "hour")) or re.search(r'\b\d+\s*h\b', s):
            return True                                   # hours / minutes out → keep
        m = re.search(r'(\d+)\s*(?:days?|d)\b', s)
        if m:
            return int(m[1]) <= max_days                  # "3 d" → keep, "15 d" → drop
        m = re.search(r'(\d+)\s*(?:weeks?|w)\b', s)
        if m:
            return int(m[1]) * 7 <= max_days
        if re.search(r'(\d+)\s*(?:months?|mon|years?|yr)\b', s):
            return False                                  # months / years out → drop

        # ── absolute date (e.g. "2025-11-03 19:42") ────────────────────
        dt = _parse_abs_date(s)
        if dt is None:
            return True                                   # unknown format → keep
        now = datetime.now()
        if dt < now - timedelta(days=max_days):
            return False                                  # more than N days in the past → drop
        return dt <= now + timedelta(days=max_days)       # future > max_days → drop


# ════════════════════════════════════════════════════════════════════════
#  MyShipTracking (paginated HTML table, no login)
# ════════════════════════════════════════════════════════════════════════
class MyShipTrackingScraper(BaseScraper):
    parser_name = "myshiptracking"

    @staticmethod
    def _page_url(url: str, pattern: str, page: int) -> str:
        """Return the URL for a given page number.

        • If a page-parameter pattern is supplied, it wins:
            - "{page}" inside the URL itself is substituted, OR
            - the pattern (e.g. "&page={page}" or "/p-{page}") is appended.
        • Otherwise the default "&page=N" (or "?page=N") is appended; an
          already-present page=N is replaced rather than duplicated."""
        pattern = (pattern or "").strip()
        if pattern:
            if "{page}" in url:
                return url.replace("{page}", str(page))
            return url + pattern.replace("{page}", str(page))
        # default behaviour
        if re.search(r'[?&]page=\d+', url):
            return re.sub(r'([?&]page=)\d+', lambda mm: mm.group(1) + str(page), url)
        sep = "&" if "?" in url else "?"
        return f"{url}{sep}page={page}"

    def build_page_urls(self, source: dict) -> list:
        """Turn the Start..End page range into one concrete URL per page.

        Example: url ".../vessels?...&pp=50", Start=1, End=3  ->
            [".../vessels?...&pp=50&page=1",
             ".../vessels?...&pp=50&page=2",
             ".../vessels?...&pp=50&page=3"]"""
        url = source["url"]
        print(url)
        start = int(source.get("startPage", 1) or 1)
        end = int(source.get("endPage", start) or start)
        if end < start:
            start, end = end, start
        # Safety cap: never scrape more than maxPages pages in one run.
        max_pages = int(source.get("maxPages", 0) or 0)
        if max_pages and (end - start + 1) > max_pages:
            end = start + max_pages - 1
        pattern = source.get("pageParamPattern")
        return [self._page_url(url, pattern, p) for p in range(start, end + 1)]

    def scrape(self, source: dict) -> list:
        rows_out = []
        name = source.get("sourceName", "MyShipTracking")
        urls = self.build_page_urls(source)
        print(f"[info] {name} ({source.get('portName')}) myshiptracking: "
              f"{len(urls)} page URL(s) to scrape "
              f"(pages {source.get('startPage')}..{source.get('endPage')}).")

        for page_no, url in enumerate(urls, start=int(source.get("startPage", 1) or 1)):
            try:
                self.driver.get(url)
                try:
                    WebDriverWait(self.driver, PAGE_WAIT_SECONDS).until(
                        lambda d: d.find_elements(By.CSS_SELECTOR, "table tbody tr"))
                except TimeoutException:
                    pass
                rows = self.driver.find_elements(By.CSS_SELECTOR, "table tbody tr")
            except Exception as ex:
                print(f"[warn] {name} page {page_no}: {ex}", file=sys.stderr)
                continue

            if not rows:                                  # empty page -> stop paging
                print(f"[info] {name} page {page_no}: empty — stopping pagination.")
                break

            page_hits, page_skipped = 0, 0
            for row in rows:
                cols = row.find_elements(By.TAG_NAME, "td")
                if not cols or len(cols) < 7:
                    continue

                received = cols[6].text.strip()           # arrival / last-seen column
                if not self.within_date_window(received):
                    page_skipped += 1
                    continue

                rows_out.append({
                    "VesselName":   cols[0].text.strip(),
                    "IMO_Number":   cols[1].text.strip() or None,
                    "VesselType":   cols[2].text.strip() or None,
                    "Origin":       cols[3].text.strip() or None,   # area
                    "VesselStatus": cols[5].text.strip() or None,   # destination column
                    "ArrivalDate":  received,
                    "PortID":       source["portId"],
                    "PortName":     source["portName"],
                    "Country":      source["country"],
                    "DataSource":   source["sourceName"],
                })
                page_hits += 1

            print(f"[info] {name} page {page_no}: {page_hits} kept"
                  + (f", {page_skipped} skipped (ETA > {self.max_days}d)" if page_skipped else ""))

        return rows_out


# ════════════════════════════════════════════════════════════════════════
#  MyShipTracking worker pool (concurrent; one persistent browser per worker)
# ════════════════════════════════════════════════════════════════════════
def _mst_worker(name: str, task_queue, results: list, lock, max_days: int):
    driver = None
    try:
        driver = build_driver()
    except Exception as ex:
        print(f"[error] worker {name}: could not start Chrome: {ex}", file=sys.stderr)
        return
    scraper = MyShipTrackingScraper(driver, max_days)
    try:
        while True:
            try:
                source = task_queue.get_nowait()
            except _QueueEmpty:
                break
            try:
                rows = scraper.scrape(source)
            except Exception as ex:
                print(f"[error] {source.get('sourceName', '?')}: {ex}", file=sys.stderr)
                rows = []
            finally:
                task_queue.task_done()
            with lock:
                results.append((source, rows))
    finally:
        if driver is not None:
            try:
                driver.quit()
            except Exception:
                pass


def scrape_myshiptracking_sources(sources: list, max_days: int, max_workers: int) -> list:
    """Scrape all MyShipTracking sources in parallel; return [(source, rows), ...]."""
    if not sources:
        return []
    task_queue = _Queue()
    for s in sources:
        task_queue.put(s)
    workers = max(1, min(max_workers, len(sources)))
    results, lock, threads = [], _threading.Lock(), []
    for i in range(workers):
        th = _threading.Thread(target=_mst_worker,
                               args=(str(i + 1), task_queue, results, lock, max_days),
                               daemon=True)
        th.start()
        threads.append(th)
    for th in threads:
        th.join()
    return results


# ════════════════════════════════════════════════════════════════════════
#  main
# ════════════════════════════════════════════════════════════════════════
def main():
    # Web app calls this as: python scraper.py <config.json> <output.json>
    # Running it with no args (e.g. straight from an IDE) falls back to
    # config.json / results.json sitting next to this script — so it never
    # hits a hard exit just because argv is empty.
    if len(sys.argv) >= 3:
        config_path, output_path = sys.argv[1], sys.argv[2]
    else:
        script_dir = Path(__file__).resolve().parent
        config_path = script_dir / "config_2.json"
        output_path = script_dir / "results.json"
        print(f"[info] No CLI args given — using defaults:\n"
              f"       config: {config_path}\n"
              f"       output: {output_path}", file=sys.stderr)

    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    sources = config.get("sources", [])
    max_days = int(config.get("maxDays", config.get("maxEtaDays", DEFAULT_MAX_DAYS))
                   or DEFAULT_MAX_DAYS)

    default_workers = int(os.environ.get("SCRAPER_WORKERS", "8") or 8)
    max_workers = int(config.get("maxWorkers", default_workers) or default_workers)

    print(f"[info] MyShipTracking: {len(sources)} source(s), "
          f"up to {max_workers} browser(s).")
    raw_results = scrape_myshiptracking_sources(sources, max_days, max_workers)

    # ── MERGE + de-duplicate across all sources/pages ─────────────────────
    vessels, seen = [], set()
    for _src, source_rows in raw_results:
        for rec in source_rows:
            key = (rec.get("IMO_Number") or rec["VesselName"], rec["PortID"])
            if key in seen:
                continue
            seen.add(key)
            vessels.append(rec)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(vessels, f, indent=4)

    print(json.dumps({"total": len(vessels),
                      "sources": len(sources),
                      "maxDays": max_days,
                      "workers": max_workers}))


if __name__ == "__main__":
    main()
