"""
Shipping Management — web scraper
Called by the web app's "Load Data" button (ImportData module).

Usage:
    python scraper.py <config.json> <output.json>

This version scrapes every configured source CONCURRENTLY (one isolated
Selenium driver per source — drivers are not thread-safe) and MERGES all
rows into a single de-duplicated output list. Two source "parsers" are
supported and chosen automatically from the URL host:

  • myshiptracking  — the original paginated HTML-table parser (default)
  • vesseltracker   — the cockpit SPA at cockpit.vesseltracker.com

DATE WINDOW (both parsers):
    Only rows whose date is TODAY or within the previous `daysBack` days
    (default 10) are kept. Future dates and anything older are dropped.
    Configure globally with "daysBack" or per-source. See within_date_window.

LOGGING:
    Every informative line, warning and error is written to a timestamped
    scraper_YYYYMMDD_HHMMSS.log next to output.json (and echoed to stderr).
    stdout stays clean for the final JSON status the app parses. Read the log
    to understand why a run came back empty / why a row was kept or skipped.

config.json (written by the app from Ports Setup → Data Sources):
{
  "maxWorkers": 4,                 # optional; concurrent browsers cap (default 4)
  "daysBack": 10,                  # optional; today + previous N days (default 10)
  "keepUnknownDates": true,        # optional; keep rows whose date can't be parsed
  "sources": [ ... ]
}

output.json: flat list of scraped vessel rows the app imports into ScrapedData.
"""

import json
import logging
import os
import re
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta
from urllib.parse import urlparse

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.chrome.options import Options
from selenium.webdriver.support.ui import WebDriverWait
from selenium.common.exceptions import TimeoutException, WebDriverException


# Directory for diagnostic dumps (screenshot + HTML on a failed VesselTracker pull).
# Set in main() to the output file's folder so artifacts land next to output.json.
DIAG_DIR = None

# ── Logging ────────────────────────────────────────────────────────────────
# logger writes timestamped lines to a .log file next to output.json and echoes
# an INFO-level summary to stderr. stdout is reserved for the final JSON status.
logger = logging.getLogger("scraper")
LOG_PATH = None


def setup_logging(log_dir: str) -> str:
    """Configure file + stderr logging. Returns the log file path."""
    global LOG_PATH
    logger.setLevel(logging.DEBUG)
    logger.handlers.clear()
    logger.propagate = False

    os.makedirs(log_dir or ".", exist_ok=True)
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    LOG_PATH = os.path.join(log_dir or ".", f"scraper_{ts}.log")

    fmt = logging.Formatter(
        "%(asctime)s.%(msecs)03d %(levelname)-7s [%(threadName)s] %(message)s",
        "%Y-%m-%d %H:%M:%S")

    fh = logging.FileHandler(LOG_PATH, encoding="utf-8")
    fh.setLevel(logging.DEBUG)            # full detail incl. per-row skips → file
    fh.setFormatter(fmt)
    logger.addHandler(fh)

    ch = logging.StreamHandler(sys.stderr)   # human-facing summary → console
    ch.setLevel(logging.INFO)
    ch.setFormatter(fmt)
    logger.addHandler(ch)

    logger.info("=== scraper run started ; log file: %s ===", LOG_PATH)
    return LOG_PATH


def build_driver(headless: bool = True):
    chrome_options = Options()
    # Set VT_HEADFUL=1 to watch the browser locally while debugging selectors.
    headful = os.environ.get("VT_HEADFUL", "").strip().lower() in ("1", "true", "yes")
    if headless and not headful:
        chrome_options.add_argument("--headless=new")   # Run in background
    chrome_options.add_argument("--disable-gpu")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--disable-dev-shm-usage")   # safer under concurrency
    chrome_options.add_argument("--window-size=1920,1080")
    chrome_options.add_argument("--lang=en-US")
    chrome_options.add_argument(                            # look like a normal browser
        "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36")
    driver = webdriver.Chrome(options=chrome_options)
    driver.implicitly_wait(30)
    return driver


def is_recent(received: str) -> bool:
    """Keep only recent records — same rule as the original script.
    (Legacy helper; the parsers now use within_date_window instead.)"""
    return ("4 d" in received or "m" in received or "h" in received
            or "min" in received or "Now" in received)


# ── ETA window filter (skip vessels arriving more than N days out) ──────────
DEFAULT_MAX_ETA_DAYS = 10

# ── Past-date window (keep today + previous N days, drop future / too old) ──
DEFAULT_DAYS_BACK = 10

_MONTHS = {m: i for i, m in enumerate(
    ["", "jan", "feb", "mar", "apr", "may", "jun",
     "jul", "aug", "sep", "oct", "nov", "dec"])}


def _safe_dt(y, mo, d, hh=0, mm=0):
    try:
        return datetime(y, mo, d, hh, mm)
    except (ValueError, TypeError):
        return None


def _infer_year(mo, d, now=None):
    """No year given → assume this year, but roll to next year if that date is
    already well in the past (handles Dec→Jan wrap on 'expected arrivals')."""
    now = now or datetime.now()
    cand = _safe_dt(now.year, mo, d)
    if cand and cand < now - timedelta(days=60):
        return now.year + 1
    return now.year


def _parse_abs_date(s: str, now=None):
    """Best-effort absolute-date parse across common formats. Returns datetime or None."""
    s = (s or "").strip().lower()
    if not s:
        return None
    # ISO: 2026-06-25 [14:30]  → matches "2026-04-28 03:28" and the PKT strings
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
        return _safe_dt(int(m[3]) if m[3] else _infer_year(mo, d, now), mo, d)
    # "jun 25 2026" / "jun 25, 2026" / "jun 25"
    m = re.search(r'\b([a-z]{3,})\.?\s+(\d{1,2})(?:,?\s*(\d{4}))?', s)
    if m and m[1][:3] in _MONTHS:
        mo = _MONTHS[m[1][:3]]; d = int(m[2])
        return _safe_dt(int(m[3]) if m[3] else _infer_year(mo, d, now), mo, d)
    return None


def within_eta_window(eta_text, max_days: int = DEFAULT_MAX_ETA_DAYS) -> bool:
    """LEGACY (no longer used by the parsers): True if the ETA is within the next
    `max_days`, or is past / 'now' / unknown. Kept for reference/back-compat."""
    if not eta_text:
        return True
    s = str(eta_text).strip().lower()
    if not s or s in ("now", "today", "n/a", "na", "-", "—"):
        return True
    now = datetime.now()
    horizon = now + timedelta(days=max_days)

    if "tomorrow" in s:
        return max_days >= 1
    if any(u in s for u in ("min", "hour", "now")) or re.search(r'\b\d+\s*h\b', s):
        return True
    m = re.search(r'(\d+)\s*(?:days?|d)\b', s)
    if m:
        return int(m[1]) <= max_days
    m = re.search(r'(\d+)\s*(?:weeks?|w)\b', s)
    if m:
        return int(m[1]) * 7 <= max_days
    if re.search(r'(\d+)\s*(?:months?|mon|year|yr)\b', s):
        return False

    dt = _parse_abs_date(s)
    if dt is None:
        return True
    if dt < now - timedelta(days=1):
        return True
    return dt <= horizon


def within_date_window(date_text, days_back: int = DEFAULT_DAYS_BACK,
                       keep_unknown: bool = True, now=None,
                       relative_is_past: bool = False) -> bool:
    """True if `date_text` is TODAY or within the previous `days_back` days.

    Window KEPT:    [start_of_day(today - days_back) .. end_of_day(today)]
    DROPPED:        anything in the FUTURE (after today) or older than days_back.

    Handles absolute datetimes from BOTH feeds:
        myshiptracking "2026-04-28 03:28"  /  vesseltracker "2026-06-23 12:00 PKT …"

    keep_unknown:
        True (default) → keep values we cannot parse (never drop on uncertainty).
        False          → drop unparseable values too.
    relative_is_past:
        True → also read bare durations like '4 d' / '2 h' / 'Now' as time elapsed
        since now (use for feeds that show "received X ago" style values). Absolute
        dates are still handled, so this is safe to leave on for myshiptracking.
    """
    if date_text is None:
        return keep_unknown
    s = str(date_text).strip().lower()
    if not s or s in ("n/a", "na", "-", "—", "unknown"):
        return keep_unknown

    now = now or datetime.now()
    today_end = now.replace(hour=23, minute=59, second=59, microsecond=0)
    window_start = (now - timedelta(days=days_back)).replace(
        hour=0, minute=0, second=0, microsecond=0)

    # explicit relative phrasing (universal)
    if s in ("now", "today"):
        return True
    if "yesterday" in s:
        return days_back >= 1
    if "tomorrow" in s:
        return False
    m = re.search(r'(\d+)\s*(min|minute|hour|hr|h|day|d|week|w|month|mon|year|yr)s?\s+ago', s)
    if m:
        n, unit = int(m[1]), m[2]
        if unit.startswith(("min", "hour", "hr", "h")):
            return True
        if unit.startswith(("day", "d")):
            return n <= days_back
        if unit.startswith(("week", "w")):
            return n * 7 <= days_back
        return False

    # bare "received X ago" durations (no 'ago'); opt-in via relative_is_past
    if relative_is_past:
        m = re.search(r'\b(\d+)\s*(min|minute|hour|hr|h|day|d|week|w|month|mon|year|yr)s?\b', s)
        if m:
            n, unit = int(m[1]), m[2]
            if unit.startswith(("min", "hour", "hr", "h")):
                return True
            if unit.startswith(("day", "d")):
                return n <= days_back
            if unit.startswith(("week", "w")):
                return n * 7 <= days_back
            return False

    # bare future relative durations on an "expected" page ("in 2 days")
    if re.search(r'\bin\s+\d+\s*(?:day|d|week|w|hour|h|month|year)', s):
        return False
    if any(u in s for u in ("min", "hour")) or re.search(r'\b\d+\s*h\b', s):
        return True

    # absolute date (the path that handles "2026-04-28 03:28")
    dt = _parse_abs_date(s, now=now)
    if dt is None:
        return keep_unknown
    return window_start <= dt <= today_end


def page_url(source: dict, page: int) -> str:
    url = source["url"]
    pattern = (source.get("pageParamPattern") or "").strip()
    if not pattern:
        return url                                  # single-page source
    token = pattern.replace("{page}", str(page))
    # pattern may be a query suffix ("&page={page}" / "?page={page}") or a path piece ("/p-{page}")
    if "{page}" in url:
        return url.replace("{page}", str(page))
    return url + token


def detect_parser(source: dict) -> str:
    """Pick a parser: explicit 'parser' field wins, else infer from the URL host."""
    explicit = (source.get("parser") or "").strip().lower()
    if explicit:
        return explicit
    host = (urlparse(source.get("url", "")).hostname or "").lower()
    if "vesseltracker.com" in host:
        return "vesseltracker"
    return "myshiptracking"


# ════════════════════════════════════════════════════════════════════════
#  Parser 1 — MyShipTracking (paginated HTML-table logic)
# ════════════════════════════════════════════════════════════════════════
def parse_myshiptracking(driver, source: dict) -> list:
    rows_out = []
    name = source.get("sourceName", "MyShipTracking")
    start = int(source.get("startPage", 1) or 1)
    end = int(source.get("endPage", 1) or 1)
    max_pages = int(source.get("maxPages", 50) or 50)   # "first 50 pages" rule cap
    days_back = int(source.get("daysBack", DEFAULT_DAYS_BACK) or DEFAULT_DAYS_BACK)
    keep_unknown = bool(source.get("keepUnknownDates", True))
    paged = bool((source.get("pageParamPattern") or "").strip())

    t0 = time.monotonic()
    logger.info("%s (%s): starting myshiptracking pull "
                "(window=today + previous %s day(s)).", name, source.get("portName"), days_back)

    # ── Two-phase pagination (per spec) ────────────────────────────────
    # Phase 1: sweep the first `max_pages` pages (default 50) to capture bulk data.
    # Phase 2: then continue with the configured page sequence (startPage..endPage).
    if not paged:
        page_sequence = [start]
    else:
        phase1 = list(range(1, max_pages + 1))                 # first 50 (or maxPages)
        phase2 = list(range(start, end + 1))                   # the defined sequence
        seen, page_sequence = set(), []
        for p in phase1 + phase2:
            if p not in seen:
                seen.add(p)
                page_sequence.append(p)

    skipped_window = 0
    for page in page_sequence:
        url = page_url(source, page)
        try:
            driver.get(url)
            rows = driver.find_elements(By.CSS_SELECTOR, "table tbody tr")
        except Exception as ex:
            logger.warning("%s page %s: %s", name, page, ex)
            continue

        if not rows:                                  # empty page -> stop paging this source
            logger.info("%s: page %s empty — stopping pagination.", name, page)
            break

        page_hits = 0
        for row in rows:
            cols = row.find_elements(By.TAG_NAME, "td")
            if not cols or len(cols) < 7:
                continue

            received = cols[6].text.strip()
            # Keep only today + previous `days_back` days. The value may be an
            # absolute datetime ("2026-04-28 03:28") or a "received X ago" string.
            if not within_date_window(received, days_back, keep_unknown,
                                      relative_is_past=True):
                skipped_window += 1
                logger.debug("%s: skip out-of-window: name=%r date=%r",
                             name, cols[0].text.strip(), received)
                continue

            rows_out.append({
                "VesselName":  cols[0].text.strip(),
                "IMO_Number":  cols[1].text.strip() or None,
                "VesselType":  cols[2].text.strip() or None,
                "Origin":      cols[3].text.strip() or None,   # area
                "VesselStatus": cols[5].text.strip() or None,  # destination column
                "ArrivalDate": received,
                # authoritative port/country come from the app's Ports table:
                "PortID":      source["portId"],
                "PortName":    source["portName"],
                "Country":     source["country"],
                "DataSource":  source["sourceName"],
            })
            page_hits += 1

        logger.info("%s (%s) page %s: %d in-window row(s)",
                    name, source['portName'], page, page_hits)

    logger.info("%s (%s) myshiptracking: %d row(s) kept "
                "[window-skipped=%d (outside today+prev %dd)] in %.1fs.",
                name, source['portName'], len(rows_out), skipped_window,
                days_back, time.monotonic() - t0)
    return rows_out


# ════════════════════════════════════════════════════════════════════════
#  Parser 2 — VesselTracker cockpit SPA (cockpit.vesseltracker.com)
# ════════════════════════════════════════════════════════════════════════
VT_DEFAULT_COLUMNS = {       # fallback output field -> 0-based cell index in a row
    "VesselName":  0,
    "IMO_Number":  1,
    "VesselType":  2,
    "ArrivalDate": 3,        # ETA column
    "Origin":      4,        # last/from port, if present
}

VT_HEADER_SYNONYMS = {
    "IMO_Number":   ["imo"],
    "VesselName":   ["vessel name", "ship name", "name", "vessel", "ship"],
    "VesselType":   ["vessel type", "ship type", "type"],
    "ArrivalDate":  ["eta", "expected", "arrival", "date", "time"],
    "Origin":       ["last port", "from port", "from", "origin", "departure port"],
    "CallSign":     ["call sign", "callsign", "csign"],
    "VesselStatus": ["destination", "next port", "status", "to"],
}

VT_GRID_STRATEGIES = [
    # (row_selector, cell_selector, header_cell_selector)
    ("table tbody tr",                              "td",                          "table thead th, table thead td"),
    (".ag-center-cols-container .ag-row, .ag-row",  ".ag-cell",                    ".ag-header-cell-text, .ag-header-cell"),
    ("datatable-body-row",                          "datatable-body-cell",         "datatable-header-cell"),
    ("[role='row']",                                "[role='gridcell'], [role='cell']", "[role='columnheader']"),
    ("tbody tr",                                    "td",                          "thead th"),
]


def _vt_credentials(source: dict):
    user = "waqar zai"
    pwd = "Wsc@786."
    return (user, pwd)


def _first_visible(driver, css_list):
    """Return the first displayed element matching any comma-separated selector."""
    for sel in [s.strip() for s in css_list.split(",") if s.strip()]:
        for el in driver.find_elements(By.CSS_SELECTOR, sel):
            try:
                if el.is_displayed():
                    return el
            except WebDriverException:
                continue
    return None


def _looks_like_login(driver) -> bool:
    """True if a password field is currently visible (i.e. we're on a login wall)."""
    return _first_visible(driver, "input[type='password']") is not None


def _dismiss_cookies(driver):
    """Best-effort click on a cookie/consent accept button (it can overlay the grid)."""
    xpaths = [
        "//button[contains(translate(., 'ACEPT', 'acept'), 'accept')]",
        "//button[contains(translate(., 'AGRE', 'agre'), 'agree')]",
        "//a[contains(translate(., 'ACEPT', 'acept'), 'accept')]",
    ]
    for xp in xpaths:
        try:
            for el in driver.find_elements(By.XPATH, xp):
                if el.is_displayed():
                    el.click()
                    return
        except WebDriverException:
            pass


def vesseltracker_login(driver, source: dict) -> bool:
    """Log in to the cockpit. Returns True only if we end up OFF the login wall."""
    user, pwd = _vt_credentials(source)
    name = source.get("sourceName", "VesselTracker")
    if not user or not pwd:
        logger.warning("%s: no VesselTracker credentials set. Set env VESSELTRACKER_USER "
                       "/ VESSELTRACKER_PASS (or 'username'/'password' on the source). "
                       "The cockpit requires login, so the grid will be empty without them.",
                       name)
        return False

    login_url = source.get("loginUrl", "https://cockpit.vesseltracker.com/")
    user_sel = source.get("userField",
                          "input[type='email'], input[name='username'], input[name='email'], "
                          "input[name='login'], input[type='text']")
    pass_sel = source.get("passField", "input[type='password']")
    submit_sel = source.get("submitButton",
                            "button[type='submit'], input[type='submit'], "
                            "button[name='login'], button.btn-primary")
    try:
        driver.get(login_url)
        _dismiss_cookies(driver)
        WebDriverWait(driver, 30).until(
            lambda d: _first_visible(d, pass_sel) is not None)
        u = _first_visible(driver, user_sel)
        p = _first_visible(driver, pass_sel)
        if not u or not p:
            logger.warning("%s: could not find login fields "
                           "(override userField/passField in the source config).", name)
            return False
        u.clear(); u.send_keys(user)
        p.clear(); p.send_keys(pwd)
        btn = _first_visible(driver, submit_sel)
        if btn:
            btn.click()
        else:
            from selenium.webdriver.common.keys import Keys
            p.send_keys(Keys.ENTER)
        # success == the password field is gone
        WebDriverWait(driver, 30).until(lambda d: not _looks_like_login(d))
        logger.info("%s: logged in to VesselTracker.", name)
        return True
    except (TimeoutException, WebDriverException) as ex:
        logger.warning("%s: VesselTracker login failed or timed out. Verify credentials "
                       "and userField/passField/submitButton selectors. (%s)", name, ex)
        return False


def _row_data_score(rows, cell_selector):
    """How many of these rows actually carry >=2 non-empty cells (real data rows)."""
    good = 0
    for r in rows[:40]:                              # sample is enough to rank a layout
        try:
            cells = r.find_elements(By.CSS_SELECTOR, cell_selector)
        except WebDriverException:
            continue
        if sum(1 for c in cells if (c.text or "").strip()) >= 2:
            good += 1
    return good


def _vt_auto_columns(driver, header_selector):
    """Map output fields to column indices using the grid's header text."""
    try:
        headers = driver.find_elements(By.CSS_SELECTOR, header_selector)
    except WebDriverException:
        headers = []
    texts = [(h.text or "").strip().lower() for h in headers]
    if not any(texts):
        return None
    mapping = {}
    used = set()
    for field, syns in VT_HEADER_SYNONYMS.items():
        for idx, txt in enumerate(texts):
            if idx in used or not txt:
                continue
            if any(s == txt for s in syns) or any(s in txt for s in syns):
                mapping[field] = idx
                used.add(idx)
                break
    return mapping or None


def _vt_find_grid(driver):
    """Try each candidate layout; return (rows, cell_selector, columns_map) for the
    one that yields the most real data rows, else (None, None, None)."""
    best = (None, None, None)
    best_score = 0
    for row_sel, cell_sel, head_sel in VT_GRID_STRATEGIES:
        try:
            rows = driver.find_elements(By.CSS_SELECTOR, row_sel)
        except WebDriverException:
            continue
        if not rows:
            continue
        score = _row_data_score(rows, cell_sel)
        if score > best_score:
            cols = _vt_auto_columns(driver, head_sel) or VT_DEFAULT_COLUMNS
            best = (rows, cell_sel, cols)
            best_score = score
    return best


def _vt_lazy_scroll(driver, row_selector, cell_selector, max_loops=40):
    """Scroll to trigger lazy-loading until the row count stops growing."""
    last = -1
    for _ in range(max_loops):
        rows = driver.find_elements(By.CSS_SELECTOR, row_selector)
        if len(rows) == last:
            break
        last = len(rows)
        try:
            driver.execute_script("window.scrollTo(0, document.body.scrollHeight);")
            if rows:
                driver.execute_script("arguments[0].scrollIntoView({block:'end'});", rows[-1])
        except WebDriverException:
            break
        time.sleep(1.0)                              # let the grid fetch the next page
    return driver.find_elements(By.CSS_SELECTOR, row_selector)


def _vt_dump_diag(driver, source, reason):
    """Save a screenshot + page HTML so the user can inspect the real DOM/login."""
    try:
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        safe = re.sub(r"[^A-Za-z0-9_-]+", "_", str(source.get("sourceName", "vt")))[:40]
        base = os.path.join(DIAG_DIR or ".", f"vt_diag_{safe}_{stamp}")
        try:
            driver.save_screenshot(base + ".png")
        except WebDriverException:
            pass
        with open(base + ".html", "w", encoding="utf-8") as f:
            f.write(driver.page_source or "")
        logger.warning("[diag] %s: %s Saved screenshot + HTML to '%s.png' / '%s.html' — "
                       "open them to read the real grid markup and set "
                       "rowSelector/cellSelector/columns (or login selectors).",
                       source.get("sourceName"), reason, base, base)
    except Exception as ex:
        logger.error("[diag] %s: could not write diagnostics: %s",
                     source.get("sourceName"), ex)


def parse_vesseltracker(driver, source: dict) -> list:
    rows_out = []
    name = source.get("sourceName", "VesselTracker")
    days_back = int(source.get("daysBack", DEFAULT_DAYS_BACK) or DEFAULT_DAYS_BACK)
    keep_unknown = bool(source.get("keepUnknownDates", True))
    user_cols = source.get("columns")                # explicit override wins if provided

    t0 = time.monotonic()
    logger.info("%s (%s): starting vesseltracker pull "
                "(window=today + previous %s day(s)).", name, source.get("portName"), days_back)

    logged_in = vesseltracker_login(driver, source)
    logger.info("%s: login step done (logged_in=%s) in %.1fs.",
                name, logged_in, time.monotonic() - t0)

    try:
        driver.get(source["url"])
    except WebDriverException as ex:
        logger.error("%s: could not open %s: %s", name, source["url"], ex)
        return rows_out

    _dismiss_cookies(driver)
    # Multi-selector probing is slow under a long implicit wait; rely on explicit waits.
    driver.implicitly_wait(0)

    # Wait until EITHER real rows render OR we detect we're stuck on a login wall.
    t_wait = time.monotonic()
    grid_found = False
    try:
        WebDriverWait(driver, 45).until(
            lambda d: _looks_like_login(d) or _vt_find_grid(d)[0] is not None)
        grid_found = _vt_find_grid(driver)[0] is not None
    except TimeoutException:
        logger.warning("%s: timed out (45s) waiting for the grid to render.", name)
    logger.info("%s: grid wait finished after %.1fs (grid_found=%s, login_wall=%s).",
                name, time.monotonic() - t_wait, grid_found, _looks_like_login(driver))

    if _looks_like_login(driver):
        msg = ("still on the login page after navigating — login did not succeed."
               if logged_in else "requires login but no/invalid credentials were provided.")
        logger.warning("%s: %s", name, msg)
        _vt_dump_diag(driver, source, msg)
        return rows_out

    if not grid_found:
        logger.warning("%s: no grid detected before timeout — likely a slow cold load; "
                       "a rerun usually warms the session.", name)

    # Custom selectors from config take priority; otherwise auto-detect the layout.
    if source.get("rowSelector"):
        row_sel = source["rowSelector"]
        cell_sel = source.get("cellSelector", "td")
        columns = user_cols or VT_DEFAULT_COLUMNS
        logger.info("%s: using configured rowSelector=%r cellSelector=%r.",
                    name, row_sel, cell_sel)
    else:
        rows0, cell_sel, columns = _vt_find_grid(driver)
        if rows0 is None:
            logger.warning("%s: no recognizable data grid was found.", name)
            _vt_dump_diag(driver, source, "no recognizable data grid was found")
            return rows_out
        # recover the row selector that produced the best grid
        row_sel = next((rs for rs, cs, hs in VT_GRID_STRATEGIES if cs == cell_sel), "tbody tr")
        if user_cols:
            columns = user_cols
        logger.info("%s: grid detected → rowSelector=%r cellSelector=%r columns=%s.",
                    name, row_sel, cell_sel, columns)

    rows = _vt_lazy_scroll(driver, row_sel, cell_sel)
    logger.info("%s: %d raw row element(s) after lazy-scroll.", name, len(rows))

    def cell(cells, field):
        idx = columns.get(field)
        if idx is None or idx >= len(cells):
            return None
        return (cells[idx].text or "").strip() or None

    skipped_window = 0
    for row in rows:
        try:
            cells = row.find_elements(By.CSS_SELECTOR, cell_sel)
        except WebDriverException:
            continue
        if not cells:
            continue
        vname = cell(cells, "VesselName")
        if not vname:
            continue
        eta = cell(cells, "ArrivalDate")              # date column
        # Keep only today + previous `days_back` days; drop future / too old.
        if not within_date_window(eta, days_back, keep_unknown):
            skipped_window += 1
            logger.debug("%s: skip out-of-window: name=%r date=%r", name, vname, eta)
            continue
        rows_out.append({
            "VesselName":   vname,
            "IMO_Number": cell(cells, "IMO_Number"),
            "VesselType":   cell(cells, "VesselType"),
            "Origin":       cell(cells, "Origin"),
            "VesselStatus": cell(cells, "VesselStatus"),
            "ArrivalDate":  eta,
            # authoritative port/country come from the app's Ports table:
            "PortID":       source["portId"],
            "PortName":     source["portName"],
            "Country":      source["country"],
            "DataSource":   source["sourceName"],
        })

    if not rows_out:
        _vt_dump_diag(driver, source,
                      f"grid found ({len(rows)} row element(s)) but no in-window vessel rows "
                      f"were extracted (window-skipped={skipped_window}). Column mapping or "
                      f"the date window may need adjusting.")

    logger.info("%s (%s) vesseltracker: %d row(s) kept "
                "[raw=%d, window-skipped=%d (outside today+prev %dd)] in %.1fs total.",
                name, source['portName'], len(rows_out), len(rows),
                skipped_window, days_back, time.monotonic() - t0)
    return rows_out


# ════════════════════════════════════════════════════════════════════════
#  Per-source worker: owns its OWN driver (Selenium is not thread-safe)
# ════════════════════════════════════════════════════════════════════════
def scrape_source(source: dict) -> list:
    # Name the worker thread after the source so log lines are attributable.
    try:
        threading.current_thread().name = str(source.get("sourceName", "source"))[:30]
    except Exception:
        pass
    parser = detect_parser(source)
    driver = None
    try:
        driver = build_driver()
        if parser == "vesseltracker":
            return parse_vesseltracker(driver, source)
        return parse_myshiptracking(driver, source)
    except Exception as ex:
        logger.exception("%s (%s): unhandled error: %s",
                         source.get('sourceName', '?'), parser, ex)
        return []
    finally:
        if driver is not None:
            try:
                driver.quit()
            except Exception:
                pass


def main():
    if len(sys.argv) < 3:
        print("Usage: python scraper.py <config.json> <output.json>", file=sys.stderr)
        sys.exit(2)

    config_path, output_path = sys.argv[1], sys.argv[2]
    global DIAG_DIR
    DIAG_DIR = os.path.dirname(os.path.abspath(output_path)) or "."
    setup_logging(DIAG_DIR)
    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    sources = config.get("sources", [])
    # Global past-date window (days). A source may override with its own "daysBack".
    global_days_back = int(config.get("daysBack", DEFAULT_DAYS_BACK) or DEFAULT_DAYS_BACK)
    global_keep_unknown = bool(config.get("keepUnknownDates", True))
    for s in sources:
        s.setdefault("daysBack", global_days_back)
        s.setdefault("keepUnknownDates", global_keep_unknown)
    # Cap concurrent browsers — each source spawns a full Chrome instance.
    max_workers = int(config.get("maxWorkers", 4) or 4)
    max_workers = max(1, min(max_workers, len(sources) or 1))
    logger.info("Loaded %d source(s); maxWorkers=%d; window=today + previous %d day(s).",
                len(sources), max_workers, global_days_back)

    # ── Fetch every source SIMULTANEOUSLY, then MERGE the results ──────────
    vessels, seen = [], set()
    with ThreadPoolExecutor(max_workers=max_workers) as pool:
        future_to_src = {pool.submit(scrape_source, s): s for s in sources}
        for fut in as_completed(future_to_src):
            src = future_to_src[fut]
            try:
                source_rows = fut.result()
            except Exception as ex:
                logger.exception("%s: worker failed: %s", src.get('sourceName', '?'), ex)
                continue
            dupes = 0
            for rec in source_rows:
                key = (rec.get("IMO_Number") or rec["VesselName"], rec["PortID"])
                if key in seen:                      # dedupe across sources/pages
                    dupes += 1
                    continue
                seen.add(key)
                vessels.append(rec)
            logger.info("%s: merged %d row(s) (%d duplicate(s) dropped).",
                        src.get('sourceName', '?'), len(source_rows) - dupes, dupes)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(vessels, f, indent=4)

    logger.info("=== run finished: %d vessel(s) written to %s ===", len(vessels), output_path)
    # stdout is reserved for the machine-readable status the app parses:
    print(json.dumps({"total": len(vessels), "sources": len(sources),
                      "workers": max_workers, "log": LOG_PATH}))


if __name__ == "__main__":
    main()