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
                      (#/ Angular route → data loads via JS; usually
                       behind a login)

config.json (written by the app from Ports Setup → Data Sources):
{
  "maxWorkers": 4,                 # optional; concurrent browsers cap (default 4)
  "sources": [
    {
      "sourceId": 1,
      "sourceName": "MyShipTracking",
      "portId": 3,
      "portName": "Houston",
      "country": "United States",
      "url": "https://myshiptracking.com/vessels?...&destination=Houston&visible=vname,imo,vtype,area,speed,destination,received",
      "pageParamPattern": "&page={page}",
      "startPage": 1, "endPage": 50, "maxPages": 50
    },
    {
      "sourceId": 2,
      "sourceName": "VesselTracker Houston",
      "portId": 3,
      "portName": "Houston",
      "country": "United States",
      "url": "https://cockpit.vesseltracker.com/#/cockpit/ports/portDetails/904/expected"
      # --- optional vesseltracker overrides (see parse_vesseltracker) ---
      # "parser": "vesseltracker",          # force the parser (else auto by host)
      # "username": "...", "password": "...",   # or env VESSELTRACKER_USER / _PASS
      # "rowSelector": "table tbody tr",
      # "columns": {"VesselName":0,"IMO_Number":1,"VesselType":2,"ArrivalDate":3}
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
import time
from datetime import datetime, timedelta
from queue import Queue as _Queue, Empty as _QueueEmpty
from urllib.parse import urlparse

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.chrome.options import Options
from selenium.webdriver.support.ui import WebDriverWait
from selenium.common.exceptions import TimeoutException, WebDriverException


# Directory for diagnostic dumps (screenshot + HTML on a failed VesselTracker pull).
# Set in main() to the output file's folder so artifacts land next to output.json.
DIAG_DIR = None


def build_driver(headless: bool = True, implicit_wait: float = 0):
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
    # Implicit wait defaults to 0: we use SHORT explicit waits per page instead, so a
    # quiet port costs a few seconds rather than the old 30s-per-empty-lookup penalty.
    driver.implicitly_wait(implicit_wait)
    return driver


def is_recent(received: str) -> bool:
    """Keep only recent records — same rule as the original script."""
    return ("4 d" in received or "m" in received or "h" in received
            or "min" in received or "Now" in received)


# ── ETA window filter (skip vessels arriving more than N days out) ──────────
DEFAULT_MAX_ETA_DAYS = 10

# How long to wait for a myshiptracking results table to render before treating
# the port as empty. Lower = faster on quiet ports, but too low may miss slow
# pages. 6s is a safe balance; override per run with env SCRAPER_PAGE_WAIT.
PAGE_WAIT_SECONDS = float(os.environ.get("SCRAPER_PAGE_WAIT", "6") or 6)

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


def within_eta_window(eta_text, max_days: int = DEFAULT_MAX_ETA_DAYS) -> bool:
    """True if the ETA is within the next `max_days` (or is past / 'now' / unknown).
    Returns False ONLY when the value is confidently more than max_days in the
    future — that's the row we skip. Unparseable values are kept (never drop on
    uncertainty)."""
    if not eta_text:
        return True
    s = str(eta_text).strip().lower()
    if not s or s in ("now", "today", "n/a", "na", "-", "—"):
        return True
    now = datetime.now()
    horizon = now + timedelta(days=max_days)

    # relative durations
    if "tomorrow" in s:
        return max_days >= 1
    if any(u in s for u in ("min", "hour", "now")) or re.search(r'\b\d+\s*h\b', s):
        return True                                   # hours / minutes out
    m = re.search(r'(\d+)\s*(?:days?|d)\b', s)
    if m:
        return int(m[1]) <= max_days
    m = re.search(r'(\d+)\s*(?:weeks?|w)\b', s)
    if m:
        return int(m[1]) * 7 <= max_days
    if re.search(r'(\d+)\s*(?:months?|mon|year|yr)\b', s):
        return False                                  # months/years out → skip

    # absolute date
    dt = _parse_abs_date(s)
    if dt is None:
        return True                                   # unknown format → keep
    if dt < now - timedelta(days=1):
        return True                                   # already arrived / past
    return dt <= horizon


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
#  Parser 1 — MyShipTracking (original paginated HTML-table logic, unchanged)
# ════════════════════════════════════════════════════════════════════════
def parse_myshiptracking(driver, source: dict) -> list:
    rows_out = []
    start = int(source.get("startPage", 1) or 1)
    end = int(source.get("endPage", 1) or 1)
    max_pages = int(source.get("maxPages", 50) or 50)   # "first 50 pages" rule cap
    max_eta_days = int(source.get("maxEtaDays", DEFAULT_MAX_ETA_DAYS) or DEFAULT_MAX_ETA_DAYS)
    paged = bool((source.get("pageParamPattern") or "").strip())

    # ── Two-phase pagination (per spec) ────────────────────────────────
    # Phase 1: sweep the first `max_pages` pages (default 50) to capture bulk data.
    # Phase 2: then continue with the configured page sequence (startPage..endPage).
    # Pages are de-duplicated and visited in order.
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

    for page in page_sequence:
        url = page_url(source, page)
        try:
            driver.get(url)
            # Implicit wait is 0 now, so wait briefly & explicitly for the table to
            # render (ajax). A quiet port times out in ~PAGE_WAIT s, not 30 s.
            try:
                WebDriverWait(driver, PAGE_WAIT_SECONDS).until(
                    lambda d: d.find_elements(By.CSS_SELECTOR, "table tbody tr"))
            except TimeoutException:
                pass
            rows = driver.find_elements(By.CSS_SELECTOR, "table tbody tr")
        except Exception as ex:
            print(f"[warn] {source['sourceName']} page {page}: {ex}", file=sys.stderr)
            continue

        if not rows:                                  # empty page -> stop paging this source
            break

        page_hits = 0
        for row in rows:
            cols = row.find_elements(By.TAG_NAME, "td")
            if not cols or len(cols) < 7:
                continue

            received = cols[6].text.strip()
            #if not is_recent(received):
             #   continue
            # Skip vessels whose arrival/ETA is more than `max_eta_days` out.
            if not within_eta_window(received, max_eta_days):
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

        print(f"[info] {source['sourceName']} ({source['portName']}) page {page}: {page_hits} recent row(s)")

    return rows_out


# ════════════════════════════════════════════════════════════════════════
#  Parser 2 — VesselTracker cockpit SPA (cockpit.vesseltracker.com)
# ════════════════════════════════════════════════════════════════════════
#
#  The cockpit is an Angular app: the URL uses a '#/' fragment route, so the
#  arrivals grid is rendered by JavaScript *after* the page loads, and the
#  site requires a login. This parser therefore:
#     1. logs in (credentials from env VESSELTRACKER_USER / VESSELTRACKER_PASS,
#        or the source's "username"/"password" fields),
#     2. opens the port-details URL and waits for EITHER the grid OR a login wall,
#     3. AUTO-DETECTS the grid layout (plain table, ag-Grid, ngx-datatable, or an
#        ARIA role='grid') and maps columns by HEADER TEXT, so it keeps working
#        even if the column order changes,
#     4. lazy-scrolls to load all rows, then reads them.
#
#  If it still gets nothing, it saves a screenshot + the page HTML next to
#  output.json ([diag] line on stderr) so you can read the real markup. You can
#  override any of these per-source in config.json without editing this file:
#     username / password / loginUrl / userField / passField / submitButton
#     rowSelector / cellSelector / columns   (columns = {"VesselName":0,...})
#  Debug helpers (env): VT_HEADFUL=1 opens a visible browser.
# ════════════════════════════════════════════════════════════════════════

VT_DEFAULT_COLUMNS = {       # fallback output field -> 0-based cell index in a row
    "VesselName":  0,
    "IMO_Number":  1,
    "VesselType":  2,
    "ArrivalDate": 3,        # ETA column
    "Origin":      4,        # last/from port, if present
}

# Header-text synonyms used to auto-map columns regardless of their order, so the
# parser keeps working even if VesselTracker rearranges the grid.
VT_HEADER_SYNONYMS = {
    "IMO_Number":   ["imo"],
    "VesselName":   ["vessel name", "ship name", "name", "vessel", "ship"],
    "VesselType":   ["vessel type", "ship type", "type"],
    "ArrivalDate":  ["eta", "expected", "arrival", "date", "time"],
    "Origin":       ["last port", "from port", "from", "origin", "departure port"],
    "CallSign":     ["call sign", "callsign", "csign"],
    "VesselStatus": ["destination", "next port", "status", "to"],
}

# Candidate grid layouts, tried in order. Angular apps rarely use plain <table>;
# ag-Grid, ngx-datatable and ARIA-role grids are far more common.
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
    """Log in to the cockpit. Returns True only if we end up OFF the login wall.
    Selectors are overridable per-source (userField / passField / submitButton)."""
    user, pwd = _vt_credentials(source)
    name = source.get("sourceName", "VesselTracker")
    if not user or not pwd:
        print(f"[warn] {name}: no VesselTracker credentials set. "
              f"Set env VESSELTRACKER_USER / VESSELTRACKER_PASS (or 'username'/'password' "
              f"on the source). The cockpit requires login, so the grid will be empty "
              f"without them.", file=sys.stderr)
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
            print(f"[warn] {name}: could not find login fields "
                  f"(override userField/passField in the source config).", file=sys.stderr)
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
        print(f"[info] {name}: logged in to VesselTracker.")
        return True
    except (TimeoutException, WebDriverException) as ex:
        print(f"[warn] {name}: VesselTracker login failed or timed out. Verify "
              f"credentials and userField/passField/submitButton selectors. ({ex})",
              file=sys.stderr)
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
        print(f"[diag] {source.get('sourceName')}: {reason} "
              f"Saved screenshot + HTML to '{base}.png' / '{base}.html' — open them to "
              f"read the real grid markup and set rowSelector/cellSelector/columns "
              f"(or login selectors) in the source config.", file=sys.stderr)
    except Exception as ex:
        print(f"[diag] {source.get('sourceName')}: could not write diagnostics: {ex}",
              file=sys.stderr)


def parse_vesseltracker(driver, source: dict) -> list:
    rows_out = []
    name = source.get("sourceName", "VesselTracker")
    max_eta_days = int(source.get("maxEtaDays", DEFAULT_MAX_ETA_DAYS) or DEFAULT_MAX_ETA_DAYS)
    user_cols = source.get("columns")                # explicit override wins if provided

    logged_in = vesseltracker_login(driver, source)

    try:
        driver.get(source["url"])
    except WebDriverException as ex:
        print(f"[error] {name}: could not open {source['url']}: {ex}", file=sys.stderr)
        return rows_out

    _dismiss_cookies(driver)
    # Multi-selector probing is slow under a long implicit wait; rely on explicit waits.
    driver.implicitly_wait(0)

    # Wait until EITHER real rows render OR we detect we're stuck on a login wall.
    try:
        WebDriverWait(driver, 45).until(
            lambda d: _looks_like_login(d) or _vt_find_grid(d)[0] is not None)
    except TimeoutException:
        pass

    if _looks_like_login(driver):
        msg = ("still on the login page after navigating — login did not succeed."
               if logged_in else "requires login but no/invalid credentials were provided.")
        print(f"[warn] {name}: {msg}", file=sys.stderr)
        _vt_dump_diag(driver, source, msg)
        return rows_out

    # Custom selectors from config take priority; otherwise auto-detect the layout.
    if source.get("rowSelector"):
        row_sel = source["rowSelector"]
        cell_sel = source.get("cellSelector", "td")
        columns = user_cols or VT_DEFAULT_COLUMNS
    else:
        rows0, cell_sel, columns = _vt_find_grid(driver)
        if rows0 is None:
            _vt_dump_diag(driver, source, "no recognizable data grid was found")
            return rows_out
        # recover the row selector that produced the best grid
        row_sel = next((rs for rs, cs, hs in VT_GRID_STRATEGIES if cs == cell_sel), "tbody tr")
        if user_cols:
            columns = user_cols

    rows = _vt_lazy_scroll(driver, row_sel, cell_sel)

    def cell(cells, field):
        idx = columns.get(field)
        if idx is None or idx >= len(cells):
            return None
        return (cells[idx].text or "").strip() or None

    skipped = 0
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
        eta = cell(cells, "ArrivalDate")              # ETA (expected arrivals)
        if not within_eta_window(eta, max_eta_days):  # skip arrivals > N days out
            skipped += 1
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
                      f"grid found ({len(rows)} row element(s)) but no vessel rows were "
                      f"extracted — column mapping is probably off.")

    print(f"[info] {name} ({source['portName']}) vesseltracker: {len(rows_out)} row(s)"
          + (f", {skipped} skipped (ETA > {max_eta_days}d)" if skipped else ""))
    return rows_out


# ════════════════════════════════════════════════════════════════════════
#  Per-source parse: uses a driver PASSED IN by the worker (driver is reused
#  across many sources, so we pay Chrome's startup cost once per worker, not
#  once per source — the single biggest speed-up for large source lists).
# ════════════════════════════════════════════════════════════════════════
def scrape_source(source: dict, driver) -> list:
    parser = detect_parser(source)
    try:
        if parser == "vesseltracker":
            return parse_vesseltracker(driver, source)
        return parse_myshiptracking(driver, source)
    except Exception as ex:
        print(f"[error] {source.get('sourceName', '?')} ({parser}): {ex}", file=sys.stderr)
        return []


def _worker(name: str, task_queue, results: list, lock):
    """One persistent browser processes sources from the shared queue until empty."""
    driver = None
    try:
        driver = build_driver()
    except Exception as ex:
        print(f"[error] worker {name}: could not start Chrome: {ex}", file=sys.stderr)
        return
    try:
        while True:
            try:
                source = task_queue.get_nowait()
            except _QueueEmpty:
                break
            try:
                rows = scrape_source(source, driver)
            except Exception as ex:
                print(f"[error] {source.get('sourceName', '?')}: {ex}", file=sys.stderr)
                rows = []
            finally:
                task_queue.task_done()
            # Reset implicit wait between sources (VesselTracker sets it to 0).
            try:
                driver.implicitly_wait(0)
            except WebDriverException:
                pass
            with lock:
                results.append((source, rows))
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
    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    sources = config.get("sources", [])
    # Global ETA cutoff (days). A source may override with its own "maxEtaDays".
    global_max_eta = int(config.get("maxEtaDays", DEFAULT_MAX_ETA_DAYS) or DEFAULT_MAX_ETA_DAYS)
    for s in sources:
        s.setdefault("maxEtaDays", global_max_eta)

    # Concurrent browsers. Each is a full Chrome (~150-300 MB RAM), so scale to the
    # server: 8 is a good default; bump via config "maxWorkers" or env SCRAPER_WORKERS.
    default_workers = int(os.environ.get("SCRAPER_WORKERS", "8") or 8)
    max_workers = int(config.get("maxWorkers", default_workers) or default_workers)
    max_workers = max(1, min(max_workers, len(sources) or 1))

    # ── Fill a shared queue, then run N persistent browsers against it ────────
    task_queue = _Queue()
    for s in sources:
        task_queue.put(s)

    raw_results, lock = [], _threading.Lock()
    threads = []
    for i in range(max_workers):
        th = _threading.Thread(target=_worker,
                               args=(str(i + 1), task_queue, raw_results, lock),
                               daemon=True)
        th.start()
        threads.append(th)
    for th in threads:
        th.join()

    # ── MERGE + de-duplicate across all sources/pages ─────────────────────────
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

    print(json.dumps({"total": len(vessels), "sources": len(sources), "workers": max_workers}))


if __name__ == "__main__":
    main()
