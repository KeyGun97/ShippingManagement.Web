"""
MarineVesselTraffic "Ships in Port -> EXPECTED" scraper
=======================================================

Third scrape source, run by the web app AFTER MyShipTracking and
VesselTracker have both finished (see ScraperService.LoadData stage 2).

Logs into www.marinevesseltraffic.com ONCE (optional -- skipped when no
credentials are configured), then visits every per-port URL listed in the
config (e.g. https://www.marinevesseltraffic.com/ships-in-port/KARACHI/pk/type-Port),
opens the EXPECTED tab, walks EVERY pagination page (up to maxPages), and
maps the rows onto the same reference schema the other two scrapers emit:

    VesselName, IMO_Number, VesselType, Origin, VesselStatus, ArrivalDate,
    PortID, PortName, Country, DataSource

Usage (the web app calls it exactly like the VesselTracker script):
    python marinevesseltraffic_scraper.py <config.json> <output.json> [--headless]

Env vars (set by ScraperService from appsettings.json -> Scraper:MvtEmail /
Scraper:MvtPassword, or set them machine-wide):
    MVT_EMAIL
    MVT_PASSWORD

Notes for first run
--------------------
The EXPECTED tab and its table are rendered client-side, so the selectors
below are written defensively with several fallback strategies + debug
artifacts (screenshots / HTML dumps saved to ./debug/ as mvt_*.png/html)
rather than a single hardcoded guess. If the tab click or table extraction
fails on the very first run, check the debug folder -- the saved screenshot
+ HTML will make it a 2-minute fix to correct the one selector that doesn't
match.
"""

import asyncio
import json
import logging
import os
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

# python-dotenv is optional (credentials come via env vars set by the app).
try:
    from dotenv import load_dotenv
except ImportError:
    def load_dotenv(*_args, **_kwargs):
        return False

# Playwright is REQUIRED. We PREFER "patchright" (a drop-in patched Playwright
# that removes the automation fingerprints Cloudflare checks for) and fall back
# to stock playwright. Install on the server:
#     pip install patchright
#     python -m patchright install chromium
_PW_FLAVOR = "patchright"
try:
    from patchright.async_api import async_playwright, Page, TimeoutError as PWTimeout
except ImportError:
    _PW_FLAVOR = "playwright"
    try:
        from playwright.async_api import async_playwright, Page, TimeoutError as PWTimeout
    except ImportError:
        print("FATAL: neither 'patchright' nor 'playwright' is installed on this machine.\n"
              "Run these two commands, then retry:\n"
              "    pip install patchright python-dotenv\n"
              "    python -m patchright install chromium", file=sys.stderr)
        sys.exit(1)

# ── Make the residual stderr output UTF-8 safe ────────────────────────────
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# --------------------------------------------------------------------------
# Config / paths  (same CLI contract as vesseltracker_scraper.py)
# --------------------------------------------------------------------------

load_dotenv()

_positional = [a for a in sys.argv[1:] if not a.startswith("-")]
SCRIPT_DIR = Path(__file__).resolve().parent
_default_paths_note = None
if len(_positional) >= 2:
    CONFIG_PATH = Path(_positional[0])
    OUTPUT_PATH = Path(_positional[1])
else:
    CONFIG_PATH = SCRIPT_DIR / "config_marinevesseltraffic.json"
    OUTPUT_PATH = SCRIPT_DIR / "marinevesseltraffic_results.json"
    _default_paths_note = (f"No CLI paths given — using defaults: "
                           f"config={CONFIG_PATH}, output={OUTPUT_PATH}")

BASE_DIR = OUTPUT_PATH.parent
DEBUG_DIR = BASE_DIR / "debug"
DEBUG_DIR.mkdir(parents=True, exist_ok=True)

# ── File logging (same pattern as the other two scrapers) ─────────────────
LOG_PATH = OUTPUT_PATH.with_suffix(".log")
logger = logging.getLogger("marinevesseltraffic")
logger.setLevel(logging.INFO)
logger.propagate = False
logger.handlers.clear()
_fh = logging.FileHandler(LOG_PATH, mode="w", encoding="utf-8")
_fh.setFormatter(logging.Formatter("[%(asctime)s] %(levelname)s %(message)s",
                                   datefmt="%Y-%m-%d %H:%M:%S"))
logger.addHandler(_fh)
if _default_paths_note:
    logger.info(_default_paths_note)


def log(msg: str):
    logger.info(msg)


BASE_URL = "https://www.marinevesseltraffic.com"
LOGIN_URL = f"{BASE_URL}/login"

MVT_EMAIL = os.getenv("MVT_EMAIL", "").strip()
MVT_PASSWORD = os.getenv("MVT_PASSWORD", "").strip()

# Headless resolution — Cloudflare clears far more reliably in HEADED mode,
# so the web app runs this scraper headed by default (Scraper:MvtHeadless in
# appsettings.json). Priority: SCRAPER_FORCE_HEADLESS env (explicit 1/0
# forces headless ON/OFF) > --headless CLI flag > headed default.
_force_headless = os.environ.get("SCRAPER_FORCE_HEADLESS", "").strip().lower()
if _force_headless in ("1", "true", "yes"):
    HEADLESS = True
elif _force_headless in ("0", "false", "no"):
    HEADLESS = False
else:
    HEADLESS = "--headless" in sys.argv

# VISIBLE vs OFF-SCREEN (only meaningful when not headless).
#   default      -> the browser runs headed but is parked far off-screen, so
#                   nothing appears on the desktop. Cloudflare still sees a
#                   real, rendering, headed Chrome — which is what it checks.
#   --visible    -> show the window on-screen. Use this for the ONE-TIME run
#                   where you may need to tick "Verify you are human"; the
#                   clearance is then saved in the profile and reused.
VISIBLE = ("--visible" in sys.argv
           or os.environ.get("SCRAPER_MVT_VISIBLE", "").strip().lower() in ("1", "true", "yes"))

# How long to wait for the EXPECTED table to render before treating the tab
# as empty. Override with SCRAPER_MVT_PAGE_WAIT.
PAGE_WAIT_S = float(os.environ.get("SCRAPER_MVT_PAGE_WAIT", "12") or 12)

DEFAULT_MAX_DAYS = 10
DEFAULT_MAX_PAGES = 50          # per-port safety cap when the source doesn't set one


# --------------------------------------------------------------------------
# Progress sidecar — the web app's "Fetch Data" loading overlay polls
# output_marinevesseltraffic.progress.json (same shape as the other two).
# --------------------------------------------------------------------------
_PROGRESS_PATH = OUTPUT_PATH.with_suffix(".progress.json")
_PROGRESS_DONE = 0
_PROGRESS_TOTAL = 0
_PROGRESS_ROWS = 0


def _progress_write(phase: str):
    try:
        import time as _t
        _PROGRESS_PATH.write_text(json.dumps({
            "done": _PROGRESS_DONE,
            "total": _PROGRESS_TOTAL,
            "rows": _PROGRESS_ROWS,
            "phase": phase,
            "ts": _t.time(),
        }), encoding="utf-8")
    except Exception:
        pass


def _progress_init(total: int, phase: str = "logging in"):
    global _PROGRESS_DONE, _PROGRESS_TOTAL, _PROGRESS_ROWS
    _PROGRESS_DONE, _PROGRESS_TOTAL, _PROGRESS_ROWS = 0, max(0, int(total)), 0
    _progress_write(phase)


def _progress_tick(port_name: str = "", rows: int = 0):
    global _PROGRESS_DONE, _PROGRESS_ROWS
    _PROGRESS_DONE += 1
    _PROGRESS_ROWS += max(0, rows)
    _progress_write(f"scraped {port_name}".strip() if port_name
                    else f"{_PROGRESS_DONE}/{_PROGRESS_TOTAL}")


# --------------------------------------------------------------------------
# Debug helpers
# --------------------------------------------------------------------------

async def _dump_debug(page: Page, name: str):
    """Save screenshot + HTML for post-mortem (mvt_<name>.png / .html).
    The HTML is saved FIRST and separately, because a challenge page can
    stall screenshotting (fonts/animations) — the HTML is the more useful
    artifact anyway, so never lose it to a screenshot timeout."""
    try:
        (DEBUG_DIR / f"mvt_{name}.html").write_text(await page.content(), encoding="utf-8")
    except Exception as e:
        log(f"   ⚠ could not save debug HTML for {name}: {e}")
    try:
        # Viewport-only + short timeout: full_page on a stalled challenge page
        # is exactly what used to hit the 30s screenshot timeout.
        await page.screenshot(path=str(DEBUG_DIR / f"mvt_{name}.png"),
                              full_page=False, timeout=10_000)
        log(f"   (debug artifacts saved: debug/mvt_{name}.png/.html)")
    except Exception as e:
        log(f"   (debug HTML saved: debug/mvt_{name}.html — screenshot skipped: "
            f"{type(e).__name__})")


# --------------------------------------------------------------------------
# Cloudflare challenge handling
# --------------------------------------------------------------------------
# The site sits behind Cloudflare bot protection ("Performing security
# verification" / "Just a moment..."). The non-interactive check usually
# clears by itself after a few seconds IF the browser doesn't look automated
# (hence patchright + a persistent real-Chrome profile below). Once cleared,
# Cloudflare sets a cf_clearance cookie which the persistent profile keeps,
# so later runs typically skip the challenge entirely.

CF_MAX_WAIT_S = float(os.environ.get("SCRAPER_MVT_CF_WAIT", "90") or 90)

# Set once a challenge proves unclearable, so the remaining ports skip
# immediately instead of each burning the full CF_MAX_WAIT_S.
_CF_GIVE_UP = False


async def _is_interactive_challenge(page: Page) -> bool:
    """True when the challenge is the INTERACTIVE 'Verify you are human'
    checkbox (Turnstile), as opposed to the automatic 'Verifying...' one.
    The interactive variant cannot clear on its own in headless mode."""
    try:
        body = await page.evaluate(
            "() => document.body ? document.body.innerText.slice(0, 800) : ''")
        low = (body or "").lower()
        return "verify you are human" in low or "confirm you are human" in low
    except Exception:
        return False


async def _on_cf_challenge(page: Page) -> bool:
    """True while the current page is a Cloudflare challenge interstitial."""
    try:
        title = (await page.title() or "").lower()
        if "just a moment" in title or "attention required" in title:
            return True
        body = await page.evaluate("() => document.body ? document.body.innerText.slice(0, 600) : ''")
        low = (body or "").lower()
        return ("performing security verification" in low
                or "verifying you are human" in low
                or "checking your browser" in low
                or "verify you are not a bot" in low)
    except Exception:
        return False


async def wait_for_cloudflare(page: Page, label: str = "") -> bool:
    """
    Wait for a Cloudflare challenge to clear (max CF_MAX_WAIT_S). Returns
    True when the real page is showing, False when the challenge never
    cleared (caller should skip + a debug dump is saved).
    """
    global _CF_GIVE_UP

    if not await _on_cf_challenge(page):
        return True

    # Already established that challenges aren't clearing in this run —
    # don't make every remaining port wait the full timeout again.
    if _CF_GIVE_UP:
        log(f"   {label}: skipped — Cloudflare already blocking this run "
            f"(see the note above)")
        return False

    interactive = await _is_interactive_challenge(page)
    if interactive and (HEADLESS or not VISIBLE):
        # The checkbox can't be ticked: either there's no browser at all
        # (headless) or the window is parked off-screen. Stop the run now
        # with the fix, rather than 90s per port for a foregone conclusion.
        _CF_GIVE_UP = True
        log("   " + "=" * 66)
        log(f"   ⚠ {label}: Cloudflare is showing the INTERACTIVE "
            f"'Verify you are human' checkbox.")
        if HEADLESS:
            log("   This run is HEADLESS, which cannot pass it. Set")
            log("   Scraper:MvtHeadless=false in appsettings.json and rebuild.")
        else:
            log("   The browser window is off-screen, so it can't be ticked.")
        log("   Do ONE visible run to clear it by hand:")
        log("       python Scripts\\marinevesseltraffic_scraper.py "
            "ScraperData\\config_marinevesseltraffic.json "
            "ScraperData\\output_marinevesseltraffic.json --visible")
        log("   Tick the checkbox in the window that opens. The clearance is")
        log("   saved in the profile folder, and after that the normal hidden")
        log("   runs reuse it without showing anything.")
        log("   " + "=" * 66)
        await _dump_debug(page, f"cf_blocked_{re.sub(r'[^A-Za-z0-9]+', '_', label)[:40]}")
        return False

    kind = "interactive checkbox" if interactive else "automatic"
    log(f"   {label}: Cloudflare challenge detected ({kind}) — waiting up to "
        f"{CF_MAX_WAIT_S:.0f}s ...")
    deadline = asyncio.get_event_loop().time() + CF_MAX_WAIT_S
    while asyncio.get_event_loop().time() < deadline:
        await asyncio.sleep(1.5)
        if not await _on_cf_challenge(page):
            log(f"   {label}: challenge cleared ✔")
            await asyncio.sleep(1.0)   # let the real page settle
            return True
    log(f"   ⚠ {label}: Cloudflare challenge did NOT clear in {CF_MAX_WAIT_S:.0f}s")
    if interactive:
        log(f"   {label}: it's the checkbox challenge — tick it in the browser "
            f"window once; the clearance is then saved in the profile folder.")
    _CF_GIVE_UP = True     # remaining ports skip fast
    await _dump_debug(page, f"cf_blocked_{re.sub(r'[^A-Za-z0-9]+', '_', label)[:40]}")
    return False


async def safe_goto(page: Page, url: str, label: str) -> bool:
    """
    Navigate with Cloudflare-aware error tolerance.

    Cloudflare frequently CANCELS the original navigation and client-side
    redirects into its challenge, which Chrome surfaces as net::ERR_ABORTED —
    the browser is actually sitting on a (challenge) page, the goto() call
    just lost the race. So: an aborted navigation is NOT fatal. We wait a
    moment, and if the page has real content we carry on; otherwise retry
    once with the looser "commit" wait. Returns False only when the page is
    genuinely unreachable.
    """
    for attempt, wait_until in ((1, "domcontentloaded"), (2, "commit")):
        try:
            await page.goto(url, wait_until=wait_until, timeout=60_000)
            return True
        except Exception as e:
            msg = str(e)
            if "ERR_ABORTED" in msg:
                # Navigation was superseded (typically by the CF challenge
                # redirect). Give the replacement page a moment, then check
                # whether ANYTHING rendered — challenge pages count, because
                # wait_for_cloudflare() takes it from there.
                await page.wait_for_timeout(3_000)
                try:
                    body_len = await page.evaluate(
                        "() => document.body ? document.body.innerText.length : 0")
                except Exception:
                    body_len = 0
                if body_len > 0:
                    log(f"   {label}: navigation was superseded (ERR_ABORTED) — "
                        f"a page rendered anyway, continuing")
                    return True
                log(f"   {label}: ERR_ABORTED with an empty page "
                    f"(attempt {attempt}) — retrying" if attempt == 1 else
                    f"   ⚠ {label}: ERR_ABORTED twice with an empty page — skipping")
            elif "Timeout" in msg or "timeout" in msg:
                log(f"   ⚠ {label}: navigation timed out (attempt {attempt})")
            else:
                log(f"   ⚠ {label}: navigation error (attempt {attempt}): {msg.splitlines()[0]}")
            if attempt == 2:
                await _dump_debug(page, f"nav_failed_{re.sub(r'[^A-Za-z0-9]+', '_', label)[:40]}")
                return False
            await page.wait_for_timeout(2_000)
    return False


# --------------------------------------------------------------------------
# Cookie banner (same defensive approach that fixed VesselTracker in prod)
# --------------------------------------------------------------------------

async def dismiss_cookie_banner(page: Page):
    """Dismiss/remove any consent overlay so it can't intercept clicks."""
    button_texts = ("Got it", "Reject", "Decline", "Deny", "Only necessary",
                    "Accept All", "Accept all", "Accept", "OK", "Agree", "Save")
    for text in button_texts:
        try:
            btn = page.locator(f'button:has-text("{text}"), a:has-text("{text}")').first
            if await btn.count() and await btn.is_visible():
                await btn.click(timeout=1500)
                await page.wait_for_timeout(300)
                log(f"   (dismissed cookie banner via '{text}')")
                return
        except Exception:
            continue
    # Last resort: strip typical consent containers out of the DOM.
    try:
        await page.evaluate(
            """() => {
                for (const sel of ['#cc-overlay', '.cc-overlay', '#cc-banner',
                                   '.cc-banner', '#cc-window', '.cc-window',
                                   '[class*="cookie-consent"]', '[id*="cookie-consent"]',
                                   '[class*="cookie"] [class*="overlay"]']) {
                    document.querySelectorAll(sel).forEach(el => el.remove());
                }
                document.documentElement.style.overflow = '';
                document.body.style.overflow = '';
            }"""
        )
    except Exception:
        pass


# --------------------------------------------------------------------------
# Login / logout (optional — the port pages are public, but a logged-in
# session removes row limits / ads for subscribed accounts)
# --------------------------------------------------------------------------

async def login(page: Page) -> bool:
    """Log in once. Returns True on success, False when skipped/failed.
    A failed login is NOT fatal — we still scrape whatever the public
    pages show, and log a warning so the limitation is visible."""
    if not MVT_EMAIL or not MVT_PASSWORD:
        log("No MVT_EMAIL / MVT_PASSWORD configured — scraping without login.")
        return False

    log(f"Logging in to {LOGIN_URL} as {MVT_EMAIL} ...")
    if not await safe_goto(page, LOGIN_URL, "login"):
        log("   ⚠ login page unreachable — continuing without login")
        return False

    if not await wait_for_cloudflare(page, "login"):
        return False

    await dismiss_cookie_banner(page)

    # The login form is a standard email + password pair (Laravel-style).
    email_sel = ('input[type="email"], input[name="email"], '
                 'input[id*="email" i], input[placeholder*="mail" i]')
    pass_sel = ('input[type="password"], input[name="password"], '
                'input[id*="password" i]')
    try:
        email_box = page.locator(email_sel).first
        await email_box.wait_for(state="visible", timeout=10_000)
        await email_box.fill(MVT_EMAIL)
        await page.locator(pass_sel).first.fill(MVT_PASSWORD)
    except Exception as e:
        log(f"   ⚠ could not find/fill the login form: {e} — continuing without login")
        await _dump_debug(page, "login_no_form")
        return False

    # Submit: prefer a real submit button near the form; fall back to Enter.
    submitted = False
    for sel in ('button[type="submit"]', 'input[type="submit"]',
                'button:has-text("Log In")', 'button:has-text("Login")',
                'a:has-text("Log In")'):
        try:
            btn = page.locator(sel).first
            if await btn.count() and await btn.is_visible():
                await btn.click(timeout=5_000)
                submitted = True
                break
        except Exception:
            continue
    if not submitted:
        try:
            await page.locator(pass_sel).first.press("Enter")
            submitted = True
        except Exception:
            pass
    if not submitted:
        log("   ⚠ could not submit the login form — continuing without login")
        await _dump_debug(page, "login_no_submit")
        return False

    try:
        await page.wait_for_load_state("networkidle", timeout=30_000)
    except PWTimeout:
        pass

    # Logged-in heuristic: the "Log In" nav link disappears / "Log Out" appears.
    try:
        has_logout = await page.locator(
            'a:has-text("Log Out"), a:has-text("Logout"), a[href*="logout" i]').count()
        still_login_page = "/login" in (page.url or "")
        if has_logout or not still_login_page:
            log("   ✔ logged in")
            return True
    except Exception:
        pass

    log("   ⚠ login may not have succeeded — continuing anyway (public data only)")
    await _dump_debug(page, "login_unverified")
    return False


async def logout(page: Page):
    try:
        link = page.locator('a[href*="logout" i], a:has-text("Log Out"), '
                            'a:has-text("Logout")').first
        if await link.count():
            await link.click(timeout=5_000)
            log("Logged out.")
    except Exception:
        pass   # best-effort


# --------------------------------------------------------------------------
# EXPECTED tab + vessel list extraction
# --------------------------------------------------------------------------
# IMPORTANT: this site's vessel list is NOT an HTML <table>. It is a Vue
# component built entirely from <div>s, confirmed against the live DOM:
#
#   div.port__lists__wrapper                       <- scope everything to this
#     h3.port-lists__tabs
#       div.port-lists__tab-active   "EXPECTED"    <- active tab
#       div.port-lists__tab          "ARRIVALS" / "DEPARTURES" / "IN PORT"
#     div.port__list__table
#       div.port__list__table__row.port__list__table__row--main   <- header row
#       div.port__list__table__row                                <- data rows
#         div.port__list__table__row__timestamp        "Aug 18, 11:00"
#         div.port__list__table__row__flag  > img[alt]  "Panama"
#         div.port__list__table__row__vessel
#             a.port__list__table__row__name           "SPRING JASMINE"
#             span.port__list__table__row__type        "Bulk Carrier"
#         div.port__list__table__port
#             div.port__list__table__row__flag > img[alt]  "United States..."
#             div.port__list__table__port__details
#                 a.port__list__table__row__name       "NEW ORLEANS"
#                 span.port__list__table__row__type    "ATA: Jun 27, 03:53 UTC"
#         div.port__list__table__vessel-details
#             div...__detail--imo    "9942079"
#             div...__detail--mmsi   "352002226"
#             div...__detail         "199 / 32"   (length / beam)
#     div.pagination
#       div.pagination__found  "Total 12"
#       div.paginator
#         span.paginator__arrow--left   "‹"
#         div.paginator__page  "Page [input] of 2"
#         span.paginator__arrow--right  "›"
#
# So: find the element with class port__lists__wrapper (in the main document
# or any child frame), and do all tab / extraction / pagination work scoped
# to THAT element rather than the whole page.

WRAPPER_SEL = ".port__lists__wrapper"
ROW_SEL = ".port__list__table__row:not(.port__list__table__row--main)"


async def find_table_frame(page: Page, max_wait_s: float = PAGE_WAIT_S):
    """
    Return the frame whose document contains div.port__lists__wrapper —
    normally the main frame, but child frames are searched too so an
    embedded layout still works. Returns None if it never renders.
    """
    deadline = asyncio.get_event_loop().time() + max_wait_s
    while True:
        for f in page.frames:
            try:
                if await f.locator(WRAPPER_SEL).count():
                    return f
            except Exception:
                continue
        # Fallback: any frame that has the row class even if the wrapper
        # class was renamed by a site update.
        for f in page.frames:
            try:
                if await f.locator(".port__list__table__row").count():
                    return f
            except Exception:
                continue
        if asyncio.get_event_loop().time() >= deadline:
            return None
        await asyncio.sleep(0.5)


async def open_expected_tab(frame, port_label: str, page: Page) -> bool:
    """
    Ensure EXPECTED is the active tab inside the wrapper. EXPECTED is the
    site's default tab (rendered as div.port-lists__tab-active), so the
    usual outcome is 'already active — nothing to click'.
    """
    try:
        active = frame.locator(f"{WRAPPER_SEL} .port-lists__tab-active").first
        if await active.count():
            txt = (await active.inner_text() or "").strip().upper()
            if "EXPECTED" in txt:
                return True
            log(f"   {port_label}: active tab is '{txt}' — switching to EXPECTED")
    except Exception:
        pass

    # Not active (or unknown) — click the EXPECTED tab.
    for sel in (f'{WRAPPER_SEL} .port-lists__tab:has-text("EXPECTED")',
                f'{WRAPPER_SEL} .port-lists__tabs div:has-text("EXPECTED")',
                '.port-lists__tab:has-text("EXPECTED")'):
        try:
            tab = frame.locator(sel).first
            if not await tab.count() or not await tab.is_visible():
                continue
            try:
                await tab.click(timeout=5_000)
            except Exception:
                await tab.evaluate("el => el.click()")
            await asyncio.sleep(1.0)
            return True
        except Exception:
            continue

    # No tab element, but rows are present → EXPECTED is showing by default.
    try:
        if await frame.locator(ROW_SEL).count() > 0:
            log(f"   {port_label}: no tab element found, but rows are present "
                f"(EXPECTED is the default view) — continuing")
            return True
    except Exception:
        pass

    log(f"   ⚠ {port_label}: EXPECTED tab not found")
    await _dump_debug(page, f"no_expected_tab_{re.sub(r'[^A-Za-z0-9]+', '_', port_label)[:40]}")
    return False


async def wait_for_rows(frame, max_wait_s: float = PAGE_WAIT_S) -> int:
    """Wait until at least one data row exists inside the wrapper."""
    deadline = asyncio.get_event_loop().time() + max_wait_s
    while True:
        try:
            n = await frame.locator(ROW_SEL).count()
        except Exception:
            n = 0
        if n > 0:
            return n
        if asyncio.get_event_loop().time() >= deadline:
            return 0
        await asyncio.sleep(0.5)


# One JS pass pulls every row of the current page in a single round-trip —
# far faster and less flaky than per-cell locator calls.
_ROW_EXTRACT_JS = """
() => {
  const wrap = document.querySelector('.port__lists__wrapper')
             || document.querySelector('.port__list__table')?.parentElement;
  if (!wrap) return [];
  const txt = el => (el ? el.textContent : '').replace(/\\s+/g, ' ').trim();
  const rows = [...wrap.querySelectorAll('.port__list__table__row')]
      .filter(r => !r.classList.contains('port__list__table__row--main'));
  return rows.map(r => {
    const vesselBox = r.querySelector('.port__list__table__row__vessel');
    const portBox   = r.querySelector('.port__list__table__port');
    const portDet   = portBox ? portBox.querySelector('.port__list__table__port__details') : null;
    const nameLink  = vesselBox ? vesselBox.querySelector('a.port__list__table__row__name') : null;
    return {
      eta:      txt(r.querySelector('.port__list__table__row__timestamp')),
      name:     txt(nameLink) || txt(vesselBox),
      type:     txt(vesselBox ? vesselBox.querySelector('.port__list__table__row__type') : null),
      flag:     (r.querySelector('.port__list__table__row__flag img') || {}).alt || '',
      lastPort: txt(portDet ? portDet.querySelector('a.port__list__table__row__name') : null),
      lastAta:  txt(portDet ? portDet.querySelector('.port__list__table__row__type') : null),
      imo:      txt(r.querySelector('.port__list__table__vessel-details__detail--imo')),
      mmsi:     txt(r.querySelector('.port__list__table__vessel-details__detail--mmsi')),
      href:     nameLink ? nameLink.getAttribute('href') || '' : ''
    };
  });
}
"""


async def extract_rows(frame, source: dict, max_days: int) -> list:
    """
    Extract every vessel row of the CURRENT pagination page, mapped onto the
    reference schema shared with the other two scrapers.
    """
    try:
        raw_rows = await frame.evaluate(_ROW_EXTRACT_JS)
    except Exception as e:
        log(f"   ⚠ row extraction failed: {type(e).__name__}: {e}")
        return []

    out = []
    for r in raw_rows or []:
        vessel_name = (r.get("name") or "").strip()
        if not vessel_name:
            continue

        eta_raw = (r.get("eta") or "").strip()
        if not within_date_window(eta_raw, max_days):
            continue

        # IMO: dedicated column first, else the 2nd-to-last path segment of
        # /ship-owner-manager-ism-data/<NAME>/<IMO>/<MMSI>.
        imo = None
        m = re.search(r"\b(\d{7})\b", r.get("imo") or "")
        if m:
            imo = m.group(1)
        if imo is None:
            m = re.search(r"/(\d{6,8})/(\d{6,9})\s*$", (r.get("href") or "").strip())
            if m:
                imo = m.group(1)

        origin = (r.get("lastPort") or "").strip() or None
        vessel_type = (r.get("type") or "").strip() or None

        out.append({
            "VesselName":   vessel_name,
            "IMO_Number":   imo,
            "VesselType":   vessel_type,
            "Origin":       origin,
            "VesselStatus": None,
            "ArrivalDate":  eta_raw or None,
            "PortID":       source["portId"],
            "PortName":     source["portName"],
            "Country":      source["country"],
            "DataSource":   source.get("sourceName") or "MarineVesselTraffic",
        })
    return out


def within_date_window(raw: str, max_days: int) -> bool:
    """
    Keep a row only when its ETA/arrival value is within max_days of today,
    in EITHER direction (mirrors the MyShipTracking rule). Unparseable
    values are KEPT rather than silently dropped.
    """
    if not raw or not raw.strip():
        return True
    s = raw.strip()

    # Relative: "7 d", "8 h, 57 min", "3 days ago", "in 2 days"
    m = re.search(r"(\d+)\s*d(?:ay)?s?\b", s, re.IGNORECASE)
    if m and not re.search(r"\d{4}", s):
        return int(m.group(1)) <= max_days
    if re.search(r"\d+\s*(h|hr|hour|min|m)\b", s, re.IGNORECASE) and not re.search(r"\d{4}", s):
        return True     # hours/minutes are always inside any day window

    # Absolute: try common formats, with/without time.
    now = datetime.now()
    for fmt in ("%Y-%m-%d %H:%M", "%Y-%m-%d", "%d/%m/%Y %H:%M", "%d/%m/%Y",
                "%b %d, %Y %H:%M", "%b %d, %Y", "%d %b %Y %H:%M", "%d %b %Y",
                "%b %d, %H:%M", "%d %b, %H:%M",     # "Aug 18, 11:00" — MVT's ETA UTC column
                "%b %d %H:%M", "%d %b %H:%M"):
        try:
            d = datetime.strptime(s, fmt)
            if d.year == 1900:                     # formats without a year
                d = d.replace(year=now.year)
                # Dec ETA read in Jan (or vice versa) — pick the nearer year.
                if (d - now).days > 180:
                    d = d.replace(year=now.year - 1)
                elif (now - d).days > 180:
                    d = d.replace(year=now.year + 1)
            return abs((d - now).days) <= max_days
        except ValueError:
            continue

    # Embedded ISO date anywhere in the cell ("ETA 2026-08-20 14:00 UTC").
    m = re.search(r"(\d{4}-\d{2}-\d{2})", s)
    if m:
        try:
            d = datetime.strptime(m.group(1), "%Y-%m-%d")
            return abs((d - now).days) <= max_days
        except ValueError:
            pass

    return True     # genuinely unparseable — keep


# --------------------------------------------------------------------------
# Pagination — walk EVERY page of the EXPECTED tab (bounded by maxPages)
# --------------------------------------------------------------------------

async def _rows_fingerprint(frame) -> str:
    """Cheap identity of the current page of rows (count + first/last text)."""
    try:
        return await frame.evaluate("""
        () => {
          const rows = [...document.querySelectorAll('.port__list__table__row')]
              .filter(r => !r.classList.contains('port__list__table__row--main'));
          if (!rows.length) return '0|';
          const t = el => el.textContent.replace(/\\s+/g, ' ').trim().slice(0, 120);
          return rows.length + '|' + t(rows[0]) + '|' + t(rows[rows.length - 1]);
        }
        """)
    except Exception:
        return "?"


async def total_pages(frame) -> int:
    """
    Read the paginator's total: the markup is
        <div class="paginator__page"> Page <input ...> of 2</div>
    Returns 0 when it can't be read.
    """
    try:
        n = await frame.evaluate("""
        () => {
          const el = document.querySelector('.paginator__page');
          if (!el) return 0;
          const m = (el.textContent || '').match(/of\\s+(\\d+)/i);
          return m ? parseInt(m[1], 10) : 0;
        }
        """)
        return int(n or 0)
    except Exception:
        return 0


async def total_found(frame) -> int:
    """Read the 'Total 12' counter, for logging. 0 when unavailable."""
    try:
        n = await frame.evaluate("""
        () => {
          const el = document.querySelector('.pagination__found');
          if (!el) return 0;
          const m = (el.textContent || '').match(/(\\d+)/);
          return m ? parseInt(m[1], 10) : 0;
        }
        """)
        return int(n or 0)
    except Exception:
        return 0


async def goto_next_page(frame) -> bool:
    """
    Advance the paginator. The next control is a SPAN (not a link):
        <span class="paginator__arrow paginator__arrow--right"> › </span>
    Returns True once a new set of rows has rendered.
    """
    before = await _rows_fingerprint(frame)
    for sel in (".paginator__arrow--right",
                '.paginator span:has-text("›")',
                '.pagination [class*="right"]'):
        try:
            nxt = frame.locator(sel).first
            if not await nxt.count() or not await nxt.is_visible():
                continue
            cls = (await nxt.get_attribute("class")) or ""
            if "disabled" in cls or "inactive" in cls:
                return False
            try:
                await nxt.click(timeout=5_000)
            except Exception:
                await nxt.evaluate("el => el.click()")
            # The pager re-renders in place (no navigation) — wait for change.
            for _ in range(24):
                await asyncio.sleep(0.5)
                after = await _rows_fingerprint(frame)
                if after != before and not after.startswith("0|"):
                    return True
            return False
        except Exception:
            continue
    return False



# --------------------------------------------------------------------------
# Per-port scrape
# --------------------------------------------------------------------------

async def scrape_port(page: Page, source: dict, max_days: int) -> list:
    port_name = source["portName"]
    country = source["country"]
    url = source["url"]
    label = f"{port_name}, {country}"
    max_pages = int(source.get("maxPages") or 0) or DEFAULT_MAX_PAGES

    if not await safe_goto(page, url, label):
        return []

    if not await wait_for_cloudflare(page, label):
        return []

    await dismiss_cookie_banner(page)

    # The vessel list is a Vue component (divs, not a <table>) — locate the
    # .port__lists__wrapper element's frame, then scope everything to it.
    frame = await find_table_frame(page)
    if frame is None:
        log(f"   ⚠ {label}: vessel list (.port__lists__wrapper) not found on the page")
        await _dump_debug(page, f"no_table_{re.sub(r'[^A-Za-z0-9]+', '_', label)[:40]}")
        return []

    if not await open_expected_tab(frame, label, page):
        return []

    if await wait_for_rows(frame) == 0:
        log(f"   {label}: EXPECTED tab is empty")
        return []

    pages_total = await total_pages(frame)
    found = await total_found(frame)
    log(f"   {label}: site reports Total {found or '?'} across {pages_total or '?'} page(s)")

    all_rows, seen_fp = [], set()
    page_no = 1
    while True:
        fp = await _rows_fingerprint(frame)
        if fp in seen_fp:            # pager looped back — stop
            log(f"   {label}: page {page_no} repeats an earlier page — stopping")
            break
        seen_fp.add(fp)

        rows = await extract_rows(frame, source, max_days)
        all_rows.extend(rows)
        log(f"   {label}: page {page_no} — {len(rows)} row(s) kept")

        if page_no >= max_pages:
            log(f"   {label}: reached maxPages={max_pages} — stopping")
            break
        if pages_total and page_no >= pages_total:
            break
        if not await goto_next_page(frame):
            break
        page_no += 1

    # De-duplicate inside this port by (IMO|name).
    out, seen = [], set()
    for r in all_rows:
        key = (r.get("IMO_Number") or r["VesselName"], r["PortID"])
        if key in seen:
            continue
        seen.add(key)
        out.append(r)
    log(f"→ {label}: {len(out)} vessel(s) after {page_no} page(s)")
    return out


# --------------------------------------------------------------------------
# Worker pool (one tab per worker, shared logged-in browser context —
# same shape as the VesselTracker port workers)
# --------------------------------------------------------------------------

async def _port_worker(worker_id: int, page: Page, queue: "asyncio.Queue",
                       results: list, max_days: int):
    while True:
        try:
            source = queue.get_nowait()
        except asyncio.QueueEmpty:
            return
        label = source.get("portName") or source.get("sourceName") or "?"
        try:
            rows = await scrape_port(page, source, max_days)
        except Exception as ex:
            log(f"   ⚠ worker {worker_id} / {label}: {type(ex).__name__}: {ex}")
            rows = []
        finally:
            queue.task_done()
        results.append((source, rows))
        _progress_tick(label, rows=len(rows))


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

async def main():
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        config = json.load(f)

    sources = config.get("sources", [])
    max_days = int(config.get("maxDays", config.get("maxEtaDays", DEFAULT_MAX_DAYS))
                   or DEFAULT_MAX_DAYS)
    max_workers = int(config.get("maxWorkers", 2) or 2)

    mode = ("headless" if HEADLESS
            else ("headed, visible window" if VISIBLE else "headed, window off-screen (hidden)"))
    log(f"MarineVesselTraffic: {len(sources)} source(s), up to {max_workers} tab(s), "
        f"maxDays={max_days}, mode={mode}")
    if HEADLESS:
        log("   ⚠ Running HEADLESS. This site is behind Cloudflare, which usually "
            "shows an interactive checkbox to headless browsers — expect 0 rows. "
            "Set Scraper:MvtHeadless=false (and rebuild) to run headed.")
    _progress_init(len(sources))

    if not sources:
        OUTPUT_PATH.write_text("[]", encoding="utf-8")
        log("Nothing to scrape — empty output written.")
        return

    async with async_playwright() as pw:
        # ── Anti-bot posture ──────────────────────────────────────────────
        #  • persistent profile: the cf_clearance cookie Cloudflare sets after
        #    a successful check is KEPT between runs, so later runs usually
        #    skip the challenge entirely.
        #  • channel="chrome": use the REAL installed Google Chrome (already on
        #    the server for Selenium) — its fingerprint passes far more often
        #    than the bundled test Chromium. Falls back to Chromium if absent.
        #  • no custom user-agent: a UA that doesn't match the real browser is
        #    itself a bot signal, so we let the browser use its own.
        profile_dir = BASE_DIR / "mvt_profile"
        profile_dir.mkdir(parents=True, exist_ok=True)

        def _kwargs(headless: bool) -> dict:
            args = ["--disable-blink-features=AutomationControlled"]
            if not headless and not VISIBLE:
                # Park the window far outside every monitor's bounds: it's a
                # real rendering headed Chrome, just never on your desktop.
                args += [
                    "--window-position=-32000,-32000",
                    "--window-size=1400,900",
                    # An off-screen window can be treated as occluded/background
                    # by Chrome and throttled — that would stall the challenge
                    # and the Vue table, so disable all three throttles.
                    "--disable-backgrounding-occluded-windows",
                    "--disable-renderer-backgrounding",
                    "--disable-background-timer-throttling",
                ]
            return dict(
                user_data_dir=str(profile_dir),
                headless=headless,
                viewport={"width": 1400, "height": 900},
                args=args,
            )

        # Launch ladder: real Chrome at the requested headed-ness, then bundled
        # Chromium, then (only if a headed launch is impossible, e.g. running
        # as a service with no desktop) headless as a last resort.
        attempts = [("real Chrome", "chrome", HEADLESS),
                    ("bundled Chromium", None, HEADLESS)]
        if not HEADLESS:
            attempts += [("real Chrome (headless fallback)", "chrome", True),
                         ("bundled Chromium (headless fallback)", None, True)]
        context = None
        for label_b, channel, headless in attempts:
            try:
                kw = _kwargs(headless)
                if channel:
                    kw["channel"] = channel
                context = await pw.chromium.launch_persistent_context(**kw)
                log(f"Browser: {label_b} via {_PW_FLAVOR}, headless={headless}, "
                    f"persistent profile at {profile_dir}")
                break
            except Exception as e:
                log(f"   ({label_b} launch failed — {str(e).splitlines()[0]})")
        if context is None:
            raise RuntimeError("could not launch any browser configuration")

        # NOTE: no request interception here. Routing every request through the
        # automation layer (a) can abort/duplicate the very navigations
        # Cloudflare replaces during its challenge, and (b) is itself a
        # detectable automation signal. The ad weight it saved isn't worth
        # either risk on this site.

        login_page = context.pages[0] if context.pages else await context.new_page()
        # A login/warm-up failure must never kill the run — every port visit
        # does its own safe_goto + Cloudflare wait anyway.
        logged_in = False
        try:
            logged_in = await login(login_page)
        except Exception as e:
            log(f"   ⚠ login step crashed ({type(e).__name__}: {e}) — continuing without login")
        if not logged_in:
            # Even without credentials, visit the site ONCE up front so the
            # Cloudflare check runs (and its clearance cookie is stored) before
            # several worker tabs start hitting port pages simultaneously —
            # parallel first-contact requests are exactly what trips bot rules.
            try:
                if await safe_goto(login_page, BASE_URL, "warm-up"):
                    await wait_for_cloudflare(login_page, "warm-up")
            except Exception as e:
                log(f"   ⚠ warm-up visit failed: {e}")

        queue: asyncio.Queue = asyncio.Queue()
        for s in sources:
            queue.put_nowait(s)

        workers = max(1, min(max_workers, len(sources)))
        pages = [login_page] + [await context.new_page() for _ in range(workers - 1)]
        results: list = []
        _progress_write("scraping")
        await asyncio.gather(*[
            _port_worker(i + 1, pages[i], queue, results, max_days)
            for i in range(workers)
        ])

        await logout(pages[0])
        await context.close()

    # ── MERGE + de-duplicate across ports (same rule as the other two) ─────
    vessels, seen = [], set()
    for _src, rows in results:
        for rec in rows:
            key = (rec.get("IMO_Number") or rec["VesselName"], rec["PortID"])
            if key in seen:
                continue
            seen.add(key)
            vessels.append(rec)

    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump(vessels, f, indent=4)
    _progress_write("done")
    log(f"Done. total={len(vessels)} sources={len(sources)} — results written to {OUTPUT_PATH}")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as e:
        import traceback
        try:
            logger.error("FATAL: %s: %s\n%s", type(e).__name__, e, traceback.format_exc())
        except Exception:
            pass
        print(f"FATAL: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(1)