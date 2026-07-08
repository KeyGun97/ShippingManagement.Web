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

# python-dotenv is optional (credentials are set below / via real env vars);
# don't let a missing convenience package kill the whole scraper.
try:
    from dotenv import load_dotenv
except ImportError:
    def load_dotenv(*_args, **_kwargs):
        return False

# Playwright is REQUIRED — but fail with an actionable message instead of a
# bare traceback if it isn't installed on this machine.
try:
    from playwright.async_api import async_playwright, Page, TimeoutError as PWTimeout
except ImportError:
    print("FATAL: the 'playwright' package is not installed on this machine.\n"
          "Run these two commands, then retry:\n"
          "    pip install python-dotenv playwright\n"
          "    python -m playwright install chromium", file=sys.stderr)
    sys.exit(1)

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
NAV_TIMEOUT_MS = 90_000
NAV_RETRIES = 3                 # attempts for the login navigation
GRID_STABLE_CHECKS = 5          # WAIT+ was 3 — require 5 consecutive equal row-counts
                                # (~3s of a stable table) before trusting it's fully loaded,
                                # so a half-rendered grid can't pass as "ready"
GRID_STABLE_INTERVAL_S = 0.6
GRID_MAX_WAIT_S = 90            # WAIT+ was 60 — ceiling for waiting on a slow table

# WAIT+ Fixed delay after opening each port page, giving the slow site time to
# render before we start polling the grid (was a hardcoded 800ms). Overridable
# per run with "pageSettleMs" in the config JSON — no code edit needed to tune.
PAGE_SETTLE_MS = 5_000

# WAIT+ How long to wait for a hash-route change to actually swap the grid
# content (was ~8s). The site can take a while to re-render between ports.
SPA_SWAP_WAIT_S = 20

EXPECTED_HEADERS = ["name", "country", "type", "eta", "received via", "owner", "operator", "manager"]


def log(msg: str):
    """Write a line to the run's .log file (in the ScraperData folder)."""
    logger.info(msg)


# --------------------------------------------------------------------------
# Login / logout
# --------------------------------------------------------------------------

async def dismiss_cookie_banner(page: Page):
    """
    Dismisses the cookie-consent overlay (seen live as <div id="cc-overlay"
    class="cc-overlay">) that sits on top of the page and intercepts ALL
    pointer events — it blocked the Log-in button click for a full 30s in
    production. Strategy, in order:
      1. Click a decline/accept button by common texts (inside the overlay
         first, then anywhere on the page).
      2. If no button dismisses it, REMOVE the overlay + its known containers
         from the DOM via JS — guaranteed unblock; we don't need tracking
         cookies for scraping, only functional access.
    Non-fatal if no banner is present.
    """
    overlay = page.locator("#cc-overlay")

    async def overlay_gone() -> bool:
        try:
            return (await overlay.count()) == 0 or not await overlay.first.is_visible()
        except Exception:
            return True

    # Nothing visible? (also covers pages where the banner never appears)
    if await overlay_gone():
        # Still try the old text-based buttons briefly — some pages show a
        # banner variant without the #cc-overlay id.
        for text in ("Got it", "Reject", "Decline", "Accept All", "Accept all", "Accept"):
            try:
                btn = page.locator(f'button:has-text("{text}")').first
                await btn.wait_for(state="visible", timeout=1000)
                await btn.click()
                await page.wait_for_timeout(300)
                log(f"   (dismissed cookie banner via '{text}')")
                return
            except Exception:
                continue
        return

    log("   (cookie overlay #cc-overlay detected — dismissing...)")

    # 1) Try clicking real consent buttons, preferring ones INSIDE the overlay.
    button_texts = ("Got it", "Reject", "Decline", "Deny", "Only necessary",
                    "Accept All", "Accept all", "Accept", "OK", "Agree", "Save")
    scopes = (overlay, page.locator("body"))
    for scope in scopes:
        for text in button_texts:
            try:
                btn = scope.locator(f'button:has-text("{text}"), a:has-text("{text}")').first
                if await btn.count() and await btn.is_visible():
                    await btn.click(timeout=2000)
                    await page.wait_for_timeout(400)
                    if await overlay_gone():
                        log(f"   (cookie overlay dismissed via '{text}' button)")
                        return
            except Exception:
                continue

    # 2) Last resort: strip the overlay (and typical companion containers) out
    #    of the DOM so it can't intercept pointer events. Functional cookies /
    #    login are unaffected — this only removes the consent UI.
    try:
        await page.evaluate(
            """() => {
                for (const sel of ['#cc-overlay', '.cc-overlay', '#cc-banner',
                                   '.cc-banner', '#cc-window', '.cc-window',
                                   '[class*="cookie-consent"]', '[id*="cookie-consent"]']) {
                    document.querySelectorAll(sel).forEach(el => el.remove());
                }
                // Consent libs often freeze scrolling while the modal is up.
                document.documentElement.style.overflow = '';
                document.body.style.overflow = '';
            }"""
        )
        await page.wait_for_timeout(200)
        if await overlay_gone():
            log("   (cookie overlay removed from DOM — no dismissable button matched)")
            return
    except Exception as e:
        log(f"   ⚠ could not remove cookie overlay via JS: {e}")

    log("   ⚠ cookie overlay may still be present — clicks will use JS fallback")


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
    # WAIT+ was 1500ms.
    await page.wait_for_timeout(3000)
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

    # The consent overlay can (re)appear between page load and this click, so
    # dismiss again right before submitting.
    await dismiss_cookie_banner(page)

    submit_btn = await _first_visible(page, submit_selectors, timeout=5000)
    if submit_btn:
        try:
            # Short timeout: if something still intercepts pointer events we
            # don't want to burn 30s — fall through to a JS click instead.
            await submit_btn.click(timeout=5000)
        except Exception:
            log("   ⚠ normal click blocked (overlay?) — using JS click fallback")
            try:
                await submit_btn.evaluate("el => el.click()")   # bypasses hit-testing
            except Exception:
                await password_field.press("Enter")
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

    # WAIT+ was 2000ms — give the cockpit shell longer to finish booting after
    # auth, so the first port each tab visits isn't racing a half-loaded SPA.
    await page.wait_for_timeout(4000)

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


async def wait_for_grid_ready(page: Page, max_wait_s: float | None = None):
    """Poll row count until it stabilizes for GRID_STABLE_CHECKS consecutive reads."""
    stable = 0
    last = -1
    elapsed = 0.0
    max_wait_s = max_wait_s or GRID_MAX_WAIT_S
    while elapsed < max_wait_s:
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


async def _grid_fingerprint(page: Page) -> str:
    """Cheap fingerprint of the currently rendered grid (row count + first row
    text). Used to detect when an Angular hash-route change has actually swapped
    the table content — otherwise we can scrape the PREVIOUS view's rows."""
    try:
        return await page.evaluate(
            """() => {
                const rows = document.querySelectorAll(
                    "table tbody tr, [role='row'], .ag-row, .grid-row");
                const first = rows.length ? rows[0].innerText.slice(0, 120) : "";
                return rows.length + '|' + first;
            }"""
        )
    except Exception:
        return ""


async def _navigate_spa(page: Page, url: str, label: str) -> bool:
    """
    Navigate to a cockpit port URL, handling the Angular hash-routing pitfall:
    URLs that differ only in the '#/...' fragment trigger a SAME-DOCUMENT
    navigation, so page.goto() returns immediately while the old view's grid is
    still in the DOM. In that case we wait for the grid content to actually
    change before declaring the navigation done.
    """
    before = await _grid_fingerprint(page)

    # EVERY port navigation hops through about:blank so the port URL gets a
    # FULL document load (Angular boots directly at the port route) instead of
    # a same-document hash hop from the PREVIOUS port's view. Hash hops return
    # immediately with the old port's grid still in the DOM, and if the swap
    # outlasts SPA_SWAP_WAIT_S the old port's vessels get scraped under the
    # NEW port's name — the "wrong PortName mapping" bug. A full load costs a
    # few seconds per port but guarantees the grid belongs to THIS port.
    try:
        await page.goto("about:blank")
    except Exception:
        pass

    if not await _goto_with_retries(page, url, attempts=2, label=label):
        return False

    # Same-document (hash-only) navigation? Give the router time to swap the
    # view: poll until the grid fingerprint changes. WAIT+ was ~8s, now up to
    # SPA_SWAP_WAIT_S. A genuinely identical grid (rare) just costs the wait,
    # nothing breaks.
    if "#" in url and before:
        for _ in range(int(SPA_SWAP_WAIT_S * 2)):
            await page.wait_for_timeout(500)
            if await _grid_fingerprint(page) != before:
                break
    return True


# --------------------------------------------------------------------------
# Port identity verification — a row is NEVER stamped with a port the page
# doesn't actually show. Belt-and-braces on top of the full-load navigation.
# --------------------------------------------------------------------------

def _norm_name(s: str) -> str:
    """Accent- and case-insensitive normalisation ('Itaguaí' == 'itaguai')."""
    import unicodedata
    s = unicodedata.normalize("NFKD", s or "")
    s = "".join(ch for ch in s if not unicodedata.combining(ch))
    return re.sub(r"\s+", " ", s).strip().casefold()


def _expected_port_route(url: str) -> str | None:
    """The 'portDetails/<id>' segment this source is supposed to land on."""
    m = re.search(r"portDetails/\d+", url or "")
    return m.group(0) if m else None


async def _port_identity_ok(page: Page, source: dict,
                            max_wait_s: float = 15.0) -> bool:
    """
    True only when the rendered page provably belongs to this source's port:
      1. page.url still contains this source's portDetails/<id> route, AND
      2. the configured portName appears in the page text (the cockpit
         sidebar shows the port's name once its data has loaded).
    Polls up to max_wait_s so a slow sidebar isn't misread as a mismatch.
    """
    route = _expected_port_route(source.get("url", ""))
    want = _norm_name(source.get("portName", ""))
    if not want:
        return True                      # nothing to verify against

    elapsed = 0.0
    while True:
        try:
            if route and route not in (page.url or ""):
                ok = False               # tab was redirected elsewhere
            else:
                body = await page.evaluate(
                    "() => (document.body && document.body.innerText) || ''")
                ok = want in _norm_name(body)
        except Exception:
            ok = False
        if ok:
            return True
        if elapsed >= max_wait_s:
            return False
        await page.wait_for_timeout(1000)
        elapsed += 1.0


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
    if not await _navigate_spa(page, url, label=port_name):
        log(f"   ✗ Timed out loading {port_name}")
        return []

    # WAIT+ was 800ms; PAGE_SETTLE_MS (config "pageSettleMs") gives the slow
    # site a proper chance to render before the grid is polled.
    await page.wait_for_timeout(PAGE_SETTLE_MS)
    await dismiss_cookie_banner(page)

    # ── Identity gate ─────────────────────────────────────────────────
    # Before touching the grid, prove the rendered page IS this port:
    # correct portDetails/<id> route + the configured portName visible on
    # the page. If not, one full reload (boots Angular at the port route),
    # then re-check; still wrong -> SKIP with a loud log rather than stamp
    # another port's vessels with this portName.
    if not await _port_identity_ok(page, source):
        log(f"   ⚠ {port_name}: page does not show this port yet — reloading once...")
        try:
            await page.reload(wait_until="domcontentloaded", timeout=NAV_TIMEOUT_MS)
            await page.wait_for_timeout(PAGE_SETTLE_MS)
            await dismiss_cookie_banner(page)
        except Exception as e:
            log(f"   ⚠ reload failed for {port_name}: {e}")
        if not await _port_identity_ok(page, source):
            await _dump_debug(page, f"identity_mismatch_{source['sourceId']}")
            log(f"   ✗ {port_name}: SKIPPED — rendered page never matched this "
                f"port (expected '{port_name}' on {_expected_port_route(url)}). "
                f"Check that this source URL really is {port_name}'s Expected "
                f"Vessels page. No rows imported for it this run, so nothing "
                f"gets mapped to the wrong port.")
            return []

    vessels = []
    for attempt in (1, 2):
        try:
            vessels = await extract_table(page)
        except Exception as e:
            await _dump_debug(page, f"extract_error_{source['sourceId']}")
            log(f"   ✗ Extraction error ({port_name}, attempt {attempt}): {e}")
            vessels = []

        if vessels or attempt == 2:
            break

        # Dead port (Country/UN-Locode showing '-')? The reload retry can never
        # help — the port ID in the URL is invalid or not covered by the
        # account — so skip the retry instead of burning ~2.5 more minutes per
        # bad source. The URL must be re-copied from the cockpit (search the
        # port → Expected Vessels → copy the address bar).
        try:
            dead_port_early = await page.evaluate(
                """() => /UN\\/Locode\\s*-/.test(
                       (document.body.innerText || '').replace(/\\n/g, ' '))"""
            )
        except Exception:
            dead_port_early = False
        if dead_port_early:
            log(f"   ✗ {port_name}: dead port detected on first pass — skipping retry. "
                f"Fix this source URL: {url}")
            break

        # Zero vessels on the first pass is usually a COLD tab: right after
        # login the Angular SPA is still bootstrapping, so the port view
        # renders empty/stale and extraction finds nothing. (This is exactly
        # why the first workers×N ports of a run — or ALL ports when only one
        # VesselTracker source is configured — used to come back empty while
        # MyShipTracking worked fine.) A full reload boots the SPA directly at
        # the port route, guaranteeing a fresh grid; then wait longer.
        log(f"   ⚠ 0 rows for {port_name} — cold SPA suspected, reloading once...")
        try:
            await page.reload(wait_until="domcontentloaded", timeout=NAV_TIMEOUT_MS)
        except Exception as e:
            log(f"   ⚠ reload failed for {port_name}: {e}")
            break
        # WAIT+ was 1200ms; retry pass also gets a 1.5x grid-wait ceiling.
        await page.wait_for_timeout(PAGE_SETTLE_MS)
        await dismiss_cookie_banner(page)
        await wait_for_grid_ready(page, max_wait_s=GRID_MAX_WAIT_S * 1.5)

    if not vessels:
        # Distinguish "the port page never loaded its data" (Country/UN-Locode
        # show '-') from a genuinely empty-but-valid port. The former means the
        # port ID in the source URL is invalid or not covered by the account —
        # no amount of waiting fixes it; the URL must be re-copied from the
        # cockpit (search the port → Expected Vessels → copy address bar).
        try:
            dead_port = await page.evaluate(
                """() => /UN\\/Locode\\s*-/.test(
                       (document.body.innerText || '').replace(/\\n/g, ' '))"""
            )
        except Exception:
            dead_port = False
        if dead_port:
            log(f"   ✗ {port_name}: port page loaded EMPTY (UN/Locode '-') — "
                f"invalid port ID or no account coverage. Fix this source URL: {url}")
        await _dump_debug(page, f"empty_port_{source['sourceId']}")

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
    # "navTimeoutMs", else the module default. WAIT+ pageSettleMs is also
    # config-overridable the same way.
    global NAV_TIMEOUT_MS, PAGE_SETTLE_MS
    NAV_TIMEOUT_MS = _CLI_NAV_TIMEOUT or int(config.get("navTimeoutMs", NAV_TIMEOUT_MS) or NAV_TIMEOUT_MS)
    PAGE_SETTLE_MS = int(config.get("pageSettleMs", PAGE_SETTLE_MS) or PAGE_SETTLE_MS)

    # Concurrency: FIXED at ONE tab — ports are scraped strictly one at a time
    # in a single browser window/tab (config "maxWorkers" and --workers= are
    # intentionally ignored so nothing can raise this again).
    workers = 1

    log(f"Loaded {len(sources)} ports from config (maxDays={max_days}).")
    log(f"Running {'HEADLESS' if HEADLESS else 'HEADED (visible browser)'} "
        f"with {workers} concurrent tab(s).")

    all_records: list = []

    async with async_playwright() as pw:
        try:
            browser = await pw.chromium.launch(headless=HEADLESS)
        except Exception as e:
            # Most common on a NEW machine: the pip package is installed but the
            # browser binary isn't. Surface an actionable message instead of a
            # cryptic "Executable doesn't exist" — this is the #1 reason the
            # VesselTracker side returns nothing while MyShipTracking (Selenium
            # + system Chrome) still works.
            raise RuntimeError(
                "Could not launch Chromium for Playwright. On this machine run:\n"
                "    pip install playwright\n"
                "    python -m playwright install chromium\n"
                f"Original error: {e}"
            ) from e
        # Headless Chromium advertises itself ("HeadlessChrome" in the user
        # agent + navigator.webdriver=true), and cockpit-style sites often
        # reject such logins — which shows up as a login failure ONLY on
        # machines that run via the web app (always --headless) while a headed
        # test run works. Mask both signals.
        context = await browser.new_context(
            user_agent=("Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                        "AppleWebKit/537.36 (KHTML, like Gecko) "
                        "Chrome/126.0.0.0 Safari/537.36"),
            viewport={"width": 1366, "height": 768},
        )
        await context.add_init_script(
            "Object.defineProperty(navigator, 'webdriver', {get: () => undefined});"
        )
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
    if not all_records:
        # Exit 0 (login DID work, ports were just empty/failed) but leave a
        # trace on stderr so the web app's banner isn't silently green.
        print(f"WARNING: VesselTracker run finished with 0 records for "
              f"{len(sources)} source(s). See {LOG_PATH} and the debug folder.",
              file=sys.stderr)


if __name__ == "__main__":
    try:
        if sys.platform == "win32":
            # Playwright drives the browser through an async subprocess, which on
            # Windows requires asyncio's ProactorEventLoop — the SelectorEventLoop
            # raises NotImplementedError at startup. Proactor is the default on
            # Windows Python 3.8+, but we force it explicitly (via Runner, so we
            # don't touch the event-loop *policy* API that's deprecated in 3.14)
            # in case something set a non-default policy.
            if hasattr(asyncio, "Runner"):          # Python 3.11+
                with asyncio.Runner(loop_factory=asyncio.ProactorEventLoop) as runner:
                    runner.run(main())
            else:                                    # Python 3.8 – 3.10 fallback
                asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())
                asyncio.run(main())
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