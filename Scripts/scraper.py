"""
Shipping Management — web scraper
Called by the web app's "Load Data" button (ImportData module).

Usage:
    python scraper.py <config.json> <output.json>

config.json (written by the app from Ports Setup -> Data Sources):
{
  "sources": [
    {
      "sourceId": 1,
      "sourceName": "MyShipTracking",
      "portId": 3,
      "portName": "Houston",
      "country": "United States",
      "url": "https://myshiptracking.com/vessels?side=false&name=&destination=Houston&visible=vname,imo,vtype,area,speed,destination,received",
      "pageParamPattern": "&page={page}",   # optional; '{page}' is replaced
      "startPage": 1,
      "endPage": 50,
      "maxPages": 50
    }
  ]
}

output.json: flat list of scraped vessel rows the app imports into ScrapedData.
"""

import json
import sys

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.chrome.options import Options


def build_driver():
    chrome_options = Options()
    chrome_options.add_argument("--headless=new")   # Run in background
    chrome_options.add_argument("--disable-gpu")
    chrome_options.add_argument("--no-sandbox")
    chrome_options.add_argument("--window-size=1920,1080")
    driver = webdriver.Chrome(options=chrome_options)
    driver.implicitly_wait(30)
    return driver


def is_recent(received: str) -> bool:
    """Keep only recent records — same rule as the original script."""
    return ("4 d" in received or "m" in received or "h" in received
            or "min" in received or "Now" in received)


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


def scrape_source(driver, source: dict) -> list:
    rows_out = []
    start = int(source.get("startPage", 1) or 1)
    end = int(source.get("endPage", 1) or 1)
    max_pages = int(source.get("maxPages", 50) or 50)   # "first 50 pages" rule
    end = min(end, start + max_pages - 1)
    paged = bool((source.get("pageParamPattern") or "").strip())
    last_page = end if paged else start

    for page in range(start, last_page + 1):
        url = page_url(source, page)
        try:
            driver.get(url)
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
            if not is_recent(received):
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


def main():
    if len(sys.argv) < 3:
        print("Usage: python scraper.py <config.json> <output.json>", file=sys.stderr)
        sys.exit(2)

    config_path, output_path = sys.argv[1], sys.argv[2]
    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    vessels, seen = [], set()
    driver = build_driver()
    try:
        for source in config.get("sources", []):
            for rec in scrape_source(driver, source):
                key = (rec.get("IMO_Number") or rec["VesselName"], rec["PortID"])
                if key in seen:                      # dedupe across sources/pages
                    continue
                seen.add(key)
                vessels.append(rec)
    finally:
        driver.quit()

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(vessels, f, indent=4)

    print(json.dumps({"total": len(vessels)}))


if __name__ == "__main__":
    main()
