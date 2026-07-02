"""
VesselTracker Cockpit "Expected Arrivals" scraper
==================================================

Logs into cockpit.vesseltracker.com ONCE, then visits every port URL listed
in ports_config.json, scrapes the "Expected" table (Name/country, Type, ETA,
Received via, Owner, Operator/Manager), prints progress to the console in
real time, saves results incrementally, and logs out at the end.

Usage:
    playwright install chromium      # one-time browser install
    pip install -r requirements.txt
    python vesseltracker_scraper.py

Env vars (see .env):
    VT_EMAIL
    VT_PASSWORD

Notes for first run
--------------------
I don't have network access to cockpit.vesseltracker.com from where this
script was written, so the login-form selectors and table selectors below
are written defensively with several fallback strategies + debug artifacts
(screenshots / HTML dumps saved to ./debug/) rather than a single hardcoded
guess. If login or table extraction fails on the very first run, check the
./debug/ folder -- the saved screenshot + HTML will make it a 2-minute fix
to correct the one selector that doesn't match, and I can adjust the code
for you.
"""

import asyncio
import json
import logging
import os
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

from dotenv import load_dotenv
from playwright.async_api import async_playwright, Page, TimeoutError as PWTimeout

# ── Make the residual stderr output UTF-8 safe ────────────────────────────
# Progress logging now goes to a .log file (see below), but the fatal-error
# handler still writes one line to stderr for the web app to surface. On Windows
# a redirected stderr defaults to cp1252 and can't encode some symbols, so force
# UTF-8 (with replacement) to be safe.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# --------------------------------------------------------------------------
# Config
# --------------------------------------------------------------------------

load_dotenv()

# ── Paths ────────────────────────────────────────────────────────────────
# The web app calls this as:  python vesseltracker_scraper.py <config.json> <output.json>
# so it can point us at the per-site config + output it manages inside the
# project's ScraperData folder (overwritten every run). Flags such as
# --headless / --limit= are ignored when selecting the two positional paths.
# With no positional args we fall back to files next to this script, so it can
# still be run straight from an IDE for debugging.
_positional = [a for a in sys.argv[1:] if not a.startswith("-")]
SCRIPT_DIR = Path(__file__).resolve().parent
_default_paths_note = None
if len(_positional) >= 2:
    CONFIG_PATH = Path(_positional[0])
    OUTPUT_PATH = Path(_positional[1])
else:
    CONFIG_PATH = SCRIPT_DIR / "config_vesseltracker.json"
    OUTPUT_PATH = SCRIPT_DIR / "vesseltracker_results.json"
    _default_paths_note = (f"No CLI paths given — using defaults: "
                           f"config={CONFIG_PATH}, output={OUTPUT_PATH}")

BASE_DIR = OUTPUT_PATH.parent
DEBUG_DIR = BASE_DIR / "debug"
DEBUG_DIR.mkdir(parents=True, exist_ok=True)

# ── File logging ──────────────────────────────────────────────────────────
# All progress/diagnostics go to a .log file that sits alongside this run's
# config + results (same ScraperData folder), overwritten each run (mode="w").
# This replaces console printing, so nothing depends on the console code page.
LOG_PATH = OUTPUT_PATH.with_suffix(".log")
logger = logging.getLogger("vesseltracker")
logger.setLevel(logging.INFO)
logger.propagate = False
logger.handlers.clear()
_fh = logging.FileHandler(LOG_PATH, mode="w", encoding="utf-8")
_fh.setFormatter(logging.Formatter("[%(asctime)s] %(levelname)s %(message)s",
                                   datefmt="%Y-%m-%d %H:%M:%S"))
logger.addHandler(_fh)
if _default_paths_note:
    logger.info(_default_paths_note)

VT_EMAIL = "operations@worldshipchandler.com"#os.getenv("VT_EMAIL")
VT_PASSWORD ="Wsc@786." #os.getenv("VT_PASSWORD")

LOGIN_URL = "https://cockpit.vesseltracker.com/"
HEADLESS = "--headless" in sys.argv          # default: headed (visible), so you can watch it live
LIMIT = None
WORKERS = None                               # concurrent tabs; overrides config "maxWorkers"
_CLI_NAV_TIMEOUT = None                       # per-attempt navigation timeout (ms) from CLI
for arg in sys.argv:
    if arg.startswith("--limit="):
        LIMIT = int(arg.split("=", 1)[1])
    elif arg.startswith("--workers="):
        WORKERS = int(arg.split("=", 1)[1])
    elif arg.startswith("--nav-timeout="):
        _CLI_NAV_TIMEOUT = int(arg.split("=", 1)[1])

# How many ports to scrape at once (concurrent tabs sharing ONE logged-in
# session). Overridable per run via config "maxWorkers" or --workers=; env is
# the final fallback. Kept modest by default so we don't hammer the account.
DEFAULT_WORKERS = int(os.environ.get("VT_WORKERS", "4") or 4)

# Per-attempt page-navigation timeout (ms). cockpit.vesseltracker.com is a heavy
# Angular SPA that can be slow to respond, so this is generous — and navigations
# are also retried (see _goto_with_retries). Precedence: --nav-timeout= > config
# "navTimeoutMs" > this default (resolved in main()).
NAV_TIMEOUT_MS = 45_000
NAV_RETRIES = 3                 # attempts for the login navigation
GRID_STABLE_CHECKS = 3          # consecutive equal row-counts before we trust the grid is loaded
GRID_STABLE_INTERVAL_S = 0.6
GRID_MAX_WAIT_S = 20

EXPECTED_HEADERS = ["name", "country", "type", "eta", "received via", "owner", "operator", "manager"]


def log(msg: str):
    """Write a line to the run's .log file (in the ScraperData folder)."""
    logger.info(msg)


# --------------------------------------------------------------------------
# Login / logout
# --------------------------------------------------------------------------

async def dismiss_cookie_banner(page: Page):
    """
    Dismisses the 'Cookie preferences' consent modal that can appear on any
    page load (login page or a port page). Clicks 'Reject' since we don't
    need advertising/tracking cookies for scraping — only functional access.
    Non-fatal if it's not present.
    """
    try:
        reject_btn = page.locator('button:has-text("Reject")').first
        await reject_btn.wait_for(state="visible", timeout=3000)
        await reject_btn.click()
        await page.wait_for_timeout(300)
        log("   (dismissed cookie-preferences banner)")
        return
    except Exception:
        pass

    # Fallback: if for some reason "Reject" isn't there but "Accept All" is,
    # accept rather than get stuck behind the modal.
    try:
        accept_btn = page.locator('button:has-text("Accept All")').first
        await accept_btn.wait_for(state="visible", timeout=1500)
        await accept_btn.click()
        await page.wait_for_timeout(300)
        log("   (cookie banner: 'Reject' not found, clicked 'Accept All' instead)")
    except Exception:
        pass  # no banner present -- nothing to do


async def _goto_with_retries(page: Page, url: str, *, attempts: int = 2,
                             wait_until: str = "domcontentloaded",
                             label: str = "") -> bool:
    """Navigate to `url`, retrying on timeout / transient navigation errors.
    Returns True on success, False if every attempt fails. A single 30s goto is
    fragile against a slow SPA or a brief network hiccup, so we retry with a
    short linear backoff."""
    tag = f" ({label})" if label else ""
    for attempt in range(1, attempts + 1):
        try:
            await page.goto(url, wait_until=wait_until, timeout=NAV_TIMEOUT_MS)
            return True
        except PWTimeout:
            log(f"   ⚠ navigation timeout{tag}, attempt {attempt}/{attempts}"
                + (" — retrying..." if attempt < attempts else ""))
        except Exception as e:
            log(f"   ⚠ navigation error{tag}: {e} (attempt {attempt}/{attempts})")
        if attempt < attempts:
            await page.wait_for_timeout(2000 * attempt)  # 2s, 4s, ...
    return False


async def login(page: Page):
    if not VT_EMAIL or not VT_PASSWORD:
        raise RuntimeError("VT_EMAIL / VT_PASSWORD not set. Check your .env file.")

    log("Navigating to VesselTracker cockpit...")
    if not await _goto_with_retries(page, LOGIN_URL, attempts=NAV_RETRIES, label="login page"):
        await _dump_debug(page, "login_nav_timeout")
        raise RuntimeError(
            f"Could not load the login page {LOGIN_URL} after {NAV_RETRIES} attempts "
            f"({NAV_TIMEOUT_MS // 1000}s each). The site may be temporarily slow or "
            f"unreachable from this machine, or blocking headless access. Raise the "
            f"timeout via config 'navTimeoutMs' or --nav-timeout=, and check "
            f"debug/login_nav_timeout.png / .html."
        )

    # Give the Angular app a moment to render the login form (or redirect to it).
    await page.wait_for_timeout(1500)
    await dismiss_cookie_banner(page)

    # Try a series of plausible selectors for the email/username field.
    email_selectors = [
        'input[type="email"]',
        'input[name="email"]',
        'input[name="username"]',
        'input#email',
        'input#username',
        'input[placeholder*="mail" i]',
        'input[placeholder*="user" i]',
    ]
    password_selectors = [
        'input[type="password"]',
        'input[name="password"]',
        'input#password',
    ]
    submit_selectors = [
        'button[type="submit"]',
        'button:has-text("Log in")',
        'button:has-text("Login")',
        'button:has-text("Sign in")',
        'input[type="submit"]',
    ]

    email_field = await _first_visible(page, email_selectors, timeout=15000)
    if not email_field:
        await _dump_debug(page, "login_no_email_field")
        raise RuntimeError(
            "Could not find the email/username field on the login page. "
            "See debug/login_no_email_field.png and .html to identify the correct selector."
        )
    await email_field.fill(VT_EMAIL)

    password_field = await _first_visible(page, password_selectors, timeout=5000)
    if not password_field:
        await _dump_debug(page, "login_no_password_field")
        raise RuntimeError(
            "Could not find the password field on the login page. "
            "See debug/login_no_password_field.png and .html."
        )
    await password_field.fill(VT_PASSWORD)

    submit_btn = await _first_visible(page, submit_selectors, timeout=5000)
    if submit_btn:
        await submit_btn.click()
    else:
        # Fall back to pressing Enter in the password field.
        await password_field.press("Enter")

    # Wait for something that indicates a logged-in state: URL change away from
    # a login/auth page, or the password field disappearing.
    try:
        await page.wait_for_function(
            """() => !window.location.href.toLowerCase().includes('login')
                   && !window.location.href.toLowerCase().includes('auth')""",
            timeout=20000,
        )
    except PWTimeout:
        pass

    await page.wait_for_timeout(2000)

    if "login" in page.url.lower() or "auth" in page.url.lower():
        await _dump_debug(page, "login_failed")
        raise RuntimeError(
            "Login does not appear to have succeeded (still on a login/auth URL). "
            "See debug/login_failed.png and .html -- likely wrong credentials, an "
            "extra confirmation step (e.g. 'remember me' / MFA), or a selector mismatch."
        )

    log("Login successful.")


async def logout(page: Page):
    log("Logging out...")
    logout_selectors = [
        'button:has-text("Log out")',
        'button:has-text("Logout")',
        'a:has-text("Log out")',
        'a:has-text("Logout")',
        '[data-testid="logout"]',
    ]
    # Logout is often behind a user/profile menu -- try opening common menu triggers first.
    menu_triggers = [
        '[data-testid="user-menu"]',
        'button[aria-label*="account" i]',
        'button[aria-label*="profile" i]',
        '.user-menu, .profile-menu, .avatar',
    ]
    for trig in menu_triggers:
        try:
            el = page.locator(trig).first
            if await el.is_visible(timeout=1000):
                await el.click()
                await page.wait_for_timeout(500)
                break
        except Exception:
            continue

    btn = await _first_visible(page, logout_selectors, timeout=3000)
    if btn:
        await btn.click()
        await page.wait_for_timeout(1500)
        log("Logout clicked.")
    else:
        log("Could not find a logout control -- skipping (session will just expire naturally).")


async def _first_visible(page: Page, selectors, timeout=5000):
    """Return the first Locator among `selectors` that becomes visible, or None."""
    per_selector_timeout = max(500, timeout // len(selectors))
    for sel in selectors:
        try:
            loc = page.locator(sel).first
            await loc.wait_for(state="visible", timeout=per_selector_timeout)
            return loc
        except PWTimeout:
            continue
        except Exception:
            continue
    return None


async def _dump_debug(page: Page, name: str):
    try:
        await page.screenshot(path=str(DEBUG_DIR / f"{name}.png"), full_page=True)
        html = await page.content()
        (DEBUG_DIR / f"{name}.html").write_text(html, encoding="utf-8")
        log(f"Saved debug artifacts: debug/{name}.png, debug/{name}.html")
    except Exception as e:
        log(f"Could not save debug artifacts for {name}: {e}")


# --------------------------------------------------------------------------
# Grid readiness (handles the Angular SPA cold-load race condition)
# --------------------------------------------------------------------------

async def _row_count(page: Page) -> int:
    for sel in ["table tbody tr", "[role='row']", ".ag-row", ".grid-row"]:
        try:
            n = await page.locator(sel).count()
            if n > 0:
                return n
        except Exception:
            continue
    return 0


async def wait_for_grid_ready(page: Page):
    """Poll row count until it stabilizes for GRID_STABLE_CHECKS consecutive reads."""
    stable = 0
    last = -1
    elapsed = 0.0
    while elapsed < GRID_MAX_WAIT_S:
        n = await _row_count(page)
        if n == last and n > 0:
            stable += 1
        else:
            stable = 0
        last = n
        if stable >= GRID_STABLE_CHECKS:
            return n
        await asyncio.sleep(GRID_STABLE_INTERVAL_S)
        elapsed += GRID_STABLE_INTERVAL_S
    return last  # give up and use whatever we last saw (may be 0 -> empty port)


# --------------------------------------------------------------------------
# Table extraction
# --------------------------------------------------------------------------

JUNK_ROW_PATTERNS = [
    r"^\s*$",
    r"^no (data|results|vessels)",
    r"^loading",
]

# Sidebar / info-panel labels that have previously leaked into scraped rows
# (seen in real output: VesselName == "Country", "Local Time", "Time zone",
# "Coordinates", "Contact Info" with everything else null). These are never
# real vessel names, so treat an exact match on the first cell as junk too.
JUNK_FIRST_CELL_EXACT = {
    "country", "local time", "time zone", "coordinates", "contact info",
    "name, country", "name", "eta", "current ais eta", "type",
    "received via", "owner", "operator / manager", "operator/manager",
}


def _is_junk_row(cells):
    joined = " ".join(cells).strip().lower()
    if not joined:
        return True
    if any(re.match(pat, joined) for pat in JUNK_ROW_PATTERNS):
        return True
    first_cell = cells[0].strip().lower() if cells else ""
    # Take only the first line of the first cell (vessel name cells can be multi-line).
    first_line = first_cell.split("\n")[0].strip()
    if first_line in JUNK_FIRST_CELL_EXACT:
        return True
    return False


async def extract_table(page: Page):
    """
    Extracts rows from the Expected-arrivals table and maps them onto the
    reference schema:
        VesselName, IMO_Number, VesselType, Origin, VesselStatus, ArrivalDate

    Note: this "Expected" table doesn't expose an Origin or VesselStatus
    column, so those two are left as None to match the schema's shape
    without inventing data. (Owner / Operator / Manager / Received-via ARE
    present in the table but aren't part of the reference schema, so they're
    intentionally dropped here -- say the word if you want them added back
    as extra fields instead.)
    """
    await wait_for_grid_ready(page)

    rows_locator = page.locator("table tbody tr")
    row_count = await rows_locator.count()

    if row_count == 0:
        # Fall back to a generic role="row" grid (in case it's not a plain <table>)
        rows_locator = page.locator("[role='row']")
        row_count = await rows_locator.count()

    results = []
    for i in range(row_count):
        row = rows_locator.nth(i)
        try:
            cells = await row.locator("td, [role='cell'], [role='gridcell']").all_inner_texts()
        except Exception:
            continue

        cells = [c.strip() for c in cells]
        if _is_junk_row(cells):
            continue
        if len(cells) < 3:
            continue

        # Best-effort positional mapping based on the observed column order:
        # Name(+country) | Type | ETA | Received via | Owner | Operator/Manager
        name_country = cells[0] if len(cells) > 0 else ""
        vessel_type = cells[1] if len(cells) > 1 else ""
        eta_raw = cells[2] if len(cells) > 2 else ""

        # Name cell often contains the vessel name plus an IMO number on a second line.
        name_lines = [l.strip() for l in name_country.split("\n") if l.strip()]
        vessel_name = name_lines[0] if name_lines else ""
        imo = "-"
        for l in name_lines[1:]:
            m = re.search(r"IMO:\s*(\d+)", l, re.IGNORECASE)
            if m:
                imo = m.group(1)

        if not vessel_name:
            continue  # can't be a real vessel row without a name

        # ETA cell often has a "Current AIS ETA" sub-label baked in -- strip it,
        # then reduce to just the date part (YYYY-MM-DD) for ArrivalDate.
        eta_clean = re.sub(r"current ais eta", "", eta_raw, flags=re.IGNORECASE).strip()
        eta_clean = re.sub(r"\s+", " ", eta_clean)
        date_match = re.search(r"(\d{4}-\d{2}-\d{2})", eta_clean)
        arrival_date = date_match.group(1) if date_match else None

        results.append({
            "VesselName": vessel_name,
            "IMO_Number": imo,
            "VesselType": vessel_type or None,
            "Origin": None,
            "VesselStatus": None,
            "ArrivalDate": arrival_date,
        })

    return results


def is_older_than_max_days(arrival_date: str, max_days: int) -> bool:
    """
    True if arrival_date is more than `max_days` days in the past (i.e. should
    be skipped). Unparseable / missing dates are kept (not skipped) rather
    than silently dropped. Future dates are always kept.
    """
    if not arrival_date:
        return False
    try:
        d = datetime.strptime(arrival_date, "%Y-%m-%d").date()
    except ValueError:
        return False
    cutoff = datetime.now().date() - timedelta(days=max_days)
    return d < cutoff


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

async def scrape_port(page: Page, source: dict, max_days: int):
    port_name = source["portName"]
    country = source["country"]
    url = source["url"]

    #log(f"→ {port_name}, {country} ...")
    if not await _goto_with_retries(page, url, attempts=2, label=port_name):
        log(f"   ✗ Timed out loading {port_name}")
        return []

    await page.wait_for_timeout(800)  # let Angular route settle
    await dismiss_cookie_banner(page)

    try:
        vessels = await extract_table(page)
    except Exception as e:
        await _dump_debug(page, f"extract_error_{source['sourceId']}")
        log(f"   ✗ Extraction error: {e}")
        return []

    records = []
    skipped_old = 0
    for v in vessels:
        if is_older_than_max_days(v["ArrivalDate"], max_days):
            skipped_old += 1
            continue
        records.append({
            "VesselName": v["VesselName"],
            "IMO_Number": v["IMO_Number"],
            "VesselType": v["VesselType"],
            "Origin": v["Origin"],
            "VesselStatus": v["VesselStatus"],
            "ArrivalDate": v["ArrivalDate"],
            "PortID": source["portId"],
            "PortName": port_name,
            "Country": country,
            "DataSource": "Vessel Tracker",
        })

    #log(f"   ✓ {len(records)} vessel(s) kept, {skipped_old} skipped (older than {max_days} days)")
    return records


async def _port_worker(worker_id: int, page: Page, queue: "asyncio.Queue",
                       all_records: list, max_days: int, total: int):
    """Pull ports off the shared queue and scrape each with this worker's own tab.
    All tabs share the same logged-in browser context, so we log in only once."""
    while True:
        try:
            idx, source = queue.get_nowait()
        except asyncio.QueueEmpty:
            return
        try:
            records = await scrape_port(page, source, max_days)
        except Exception as e:
            log(f"   ✗ [{idx}/{total}] {source.get('portName', '?')}: {e}")
            records = []
        finally:
            queue.task_done()

        # list.extend is atomic between awaits under asyncio's single-threaded
        # loop, so results from concurrent tabs accumulate safely without a lock.
        if records:
            all_records.extend(records)
        log(f"   [{idx}/{total}] {source.get('portName', '?')}: "
            f"{len(records)} kept (tab {worker_id})")


async def main():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    sources = config.get("sources", [])
    max_days = config.get("maxDays", 10)

    # No VesselTracker sources for this run → write an empty result and skip the
    # (slow, credentialed) browser login entirely.
    if not sources:
        log("No VesselTracker sources in config — nothing to do.")
        OUTPUT_PATH.write_text("[]", encoding="utf-8")
        return

    if LIMIT:
        sources = sources[:LIMIT]
        log(f"--limit={LIMIT} set: only scraping the first {LIMIT} port(s).")

    # Resolve the navigation timeout: --nav-timeout= wins, else config
    # "navTimeoutMs", else the module default.
    global NAV_TIMEOUT_MS
    NAV_TIMEOUT_MS = _CLI_NAV_TIMEOUT or int(config.get("navTimeoutMs", NAV_TIMEOUT_MS) or NAV_TIMEOUT_MS)

    # Concurrency: all workers share ONE logged-in context (single login); each
    # drives its own tab. This is the main speed lever vs. the old one-at-a-time
    # loop that was timing out on large port lists.
    workers = WORKERS or int(config.get("maxWorkers", DEFAULT_WORKERS) or DEFAULT_WORKERS)
    workers = max(1, min(workers, len(sources)))

    log(f"Loaded {len(sources)} ports from config (maxDays={max_days}).")
    log(f"Running {'HEADLESS' if HEADLESS else 'HEADED (visible browser)'} "
        f"with {workers} concurrent tab(s).")

    all_records: list = []

    async with async_playwright() as pw:
        browser = await pw.chromium.launch(headless=HEADLESS)
        context = await browser.new_context()
        login_page = await context.new_page()

        try:
            await login(login_page)   # log in ONCE; the auth cookies live on the context

            # Fill a work queue, then spin up a pool of tabs that share the session.
            queue: asyncio.Queue = asyncio.Queue()
            for i, source in enumerate(sources, start=1):
                queue.put_nowait((i, source))

            worker_pages = [login_page]
            for _ in range(workers - 1):
                worker_pages.append(await context.new_page())

            await asyncio.gather(*[
                _port_worker(w + 1, worker_pages[w], queue, all_records,
                             max_days, len(sources))
                for w in range(workers)
            ])

            await logout(login_page)

        finally:
            await context.close()
            await browser.close()

    OUTPUT_PATH.write_text(json.dumps(all_records, indent=2, ensure_ascii=False), encoding="utf-8")
    log(f"Done. {len(all_records)} vessel record(s) total.")
    log(f"Results written to {OUTPUT_PATH}")


if __name__ == "__main__":
    try:
        if sys.platform == "win32":
            # Playwright drives the browser through an async subprocess, which on
            # Windows requires asyncio's ProactorEventLoop — the SelectorEventLoop
            # raises NotImplementedError at startup. Proactor is the default on
            # Windows Python 3.8+, but we force it explicitly (via Runner, so we
            # don't touch the event-loop *policy* API that's deprecated in 3.14)
            # in case something set a non-default policy.
            with asyncio.Runner(loop_factory=asyncio.ProactorEventLoop) as runner:
                runner.run(main())
        else:
            asyncio.run(main())
    except Exception as e:
        import traceback
        # Full traceback goes to the .log file for diagnosis...
        logger.error("FATAL: %s: %s\n%s", type(e).__name__, e, traceback.format_exc())
        # ...and a single concise line to stderr so the web app can still surface
        # the failure in its banner (it captures stderr, not the log file).
        print(f"FATAL: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(1)