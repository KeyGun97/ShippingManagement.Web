/* ============================================================
   Global table sorting — applies to EVERY <table class="table">
   in the project. Click a header to sort ascending, click again
   for descending. Smart compare: numbers, dates (yyyy-MM-dd /
   dd MMM… / dd/MM/yyyy), then case-insensitive text.

   Any table with a "Vessel" or "Vessel Name" column is automatically
   sorted ascending by that column as soon as it loads, and stays that
   way after any action that reloads or refreshes its rows. Clicking a
   different header still works normally and takes over as the active
   sort until the table reloads again.

   A header is NON-sortable (skipped, no pointer/arrows) when:
     • it carries [data-nosort], OR
     • it is auto-detected as an action/checkbox column — i.e. its
       header text is blank or "action(s)" AND its body cells hold
       only interactive controls (checkbox / button / link / form).

   Per-cell override: a <td data-sort="..."> sorts by that value
   instead of its visible text (used by the Import Data checkbox
   column). Full-width empty-state rows (single cell with colspan)
   stay pinned at the bottom.
   ============================================================ */
(function () {
    'use strict';

    var SORTED_ASC = 'sorted-asc', SORTED_DESC = 'sorted-desc', SORTABLE = 'th-sortable';
    var MONTHS = { jan: 1, feb: 1, mar: 1, apr: 1, may: 1, jun: 1, jul: 1, aug: 1, sep: 1, oct: 1, nov: 1, dec: 1 };

    function cellText(row, idx) {
        var cell = row.cells[idx];
        if (!cell) return '';
        if (cell.hasAttribute('data-sort')) return cell.getAttribute('data-sort');
        return (cell.innerText || cell.textContent || '').trim();
    }

    function parseVal(t) {
        if (t === '' || t === '—' || t === '-') return { type: 'empty', v: '' };
        var num = t.replace(/,/g, '');                       // numeric (allows 1,234.56)
        if (/^-?\d+(\.\d+)?$/.test(num)) return { type: 'num', v: parseFloat(num) };

        // dd/MM/yyyy or dd-MM-yyyy (day-first, as used across the app)
        var dm = t.match(/^(\d{1,2})[\/.\-](\d{1,2})[\/.\-](\d{2,4})$/);
        if (dm) {
            var yr = dm[3].length === 2 ? '20' + dm[3] : dm[3];
            var iso = Date.parse(yr + '-' + dm[2].padStart(2, '0') + '-' + dm[1].padStart(2, '0'));
            if (!isNaN(iso)) return { type: 'date', v: iso };
        }

        // yyyy-MM-dd, optionally with a time part (the format used throughout the app)
        if (/^\d{4}-\d{2}-\d{2}(?:[ T]\d{2}:\d{2}(:\d{2})?)?$/.test(t)) {
            var iso2 = Date.parse(t.replace(' ', 'T'));
            if (!isNaN(iso2)) return { type: 'date', v: iso2 };
        }

        // dd MMM yyyy, e.g. "01 Jul 2026"
        var dmy = t.match(/^(\d{1,2})\s+([A-Za-z]{3,9})\s+(\d{4})$/);
        if (dmy && MONTHS.hasOwnProperty(dmy[2].slice(0, 3).toLowerCase())) {
            var d3 = Date.parse(dmy[1] + ' ' + dmy[2] + ' ' + dmy[3]);
            if (!isNaN(d3)) return { type: 'date', v: d3 };
        }

        // Anything else (including alphanumeric text like vessel/port names that merely
        // contain a digit, e.g. "APL Paris 2") is plain text, not a date.
        return { type: 'text', v: t.toLowerCase() };
    }

    function compare(a, b) {
        var pa = parseVal(a), pb = parseVal(b);
        if (pa.type === 'empty' && pb.type === 'empty') return 0;   // empties sink
        if (pa.type === 'empty') return 1;
        if (pb.type === 'empty') return -1;
        if (pa.type === pb.type && pa.type !== 'text') return pa.v - pb.v;
        var sa = String(pa.v), sb = String(pb.v);
        return sa < sb ? -1 : sa > sb ? 1 : 0;
    }

    function dataRows(tbody) {
        return Array.prototype.slice.call(tbody.rows).filter(function (r) {
            return !(r.cells.length === 1 && r.cells[0].colSpan > 1);  // skip empty-state rows
        });
    }

    /* Decide whether a column is "action-like" and should be auto-skipped. */
    function isActionColumn(th, table, colIdx) {
        var txt = (th.innerText || th.textContent || '').trim().toLowerCase();
        var headerLooksAction = txt === '' || txt === 'action' || txt === 'actions';
        if (!headerLooksAction) return false;
        // Confirm by sampling the body: are these cells purely interactive controls?
        var tbody = table.tBodies[0];
        if (!tbody) return headerLooksAction;
        var rows = dataRows(tbody).slice(0, 8);
        if (rows.length === 0) return headerLooksAction;
        var interactive = 0, considered = 0;
        rows.forEach(function (r) {
            var c = r.cells[colIdx];
            if (!c) return;
            considered++;
            var hasControl = c.querySelector('input, button, a, select, form, .btn');
            var hasText = (c.innerText || c.textContent || '').trim() !== '';
            if (hasControl && !hasText) interactive++;          // control-only cell
            else if (hasControl) interactive += 0.5;            // control + label
        });
        return considered > 0 && (interactive / considered) >= 0.5;
    }

    /* A column is treated as "the vessel name column" when its header reads exactly
       "Vessel" or "Vessel Name" — never "Vessel Type" or similar. */
    function isVesselNameHeader(th) {
        var txt = (th.innerText || th.textContent || '').trim().toLowerCase();
        return txt === 'vessel' || txt === 'vessel name';
    }

    /* Reorders tbody rows by column idx/dir and updates the header's sorted-state styling.
       Shared by manual header clicks and the automatic vessel-name sort below. */
    function performSort(table, th, idx, dir) {
        var thead = th.closest('thead');
        var tbody = table.tBodies[0];
        if (!thead || !tbody) return;

        thead.querySelectorAll('th').forEach(function (h) {
            h.removeAttribute('data-dir');
            h.classList.remove(SORTED_ASC, SORTED_DESC);
        });
        th.setAttribute('data-dir', dir);
        th.classList.add(dir === 'asc' ? SORTED_ASC : SORTED_DESC);

        var rows = Array.prototype.slice.call(tbody.rows);
        var spanning = rows.filter(function (r) { return r.cells.length === 1 && r.cells[0].colSpan > 1; });
        var sortable = rows.filter(function (r) { return spanning.indexOf(r) === -1; });

        sortable.sort(function (r1, r2) {
            var c = compare(cellText(r1, idx), cellText(r2, idx));
            return dir === 'asc' ? c : -c;
        });

        var frag = document.createDocumentFragment();
        sortable.concat(spanning).forEach(function (r) { frag.appendChild(r); });
        tbody.appendChild(frag);
    }

    /* Keeps a table's Vessel Name column pinned to ascending order whenever rows are
       added/removed after the fact (e.g. an AJAX action that injects/drops rows without a
       full page reload) — but only while that column is still the active sort, so a
       manual click on a different header is respected instead of being fought.
       Disconnects itself while performing its own reorder so that reorder doesn't
       re-trigger the callback (mutation records are delivered as a microtask, so a
       simple synchronous "busy" flag would not prevent that self-triggered loop). */
    function watchVesselColumn(table, th, idx) {
        var tbody = table.tBodies[0];
        if (!tbody || typeof MutationObserver === 'undefined') return;
        var mo = new MutationObserver(function (mutations) {
            var structuralChange = mutations.some(function (m) { return m.type === 'childList'; });
            if (!structuralChange) return;
            if (th.getAttribute('data-dir') !== 'asc') return;   // user sorted by another column — leave it
            mo.disconnect();
            performSort(table, th, idx, 'asc');
            mo.observe(tbody, { childList: true });
        });
        mo.observe(tbody, { childList: true });
    }

    /* Mark sortable headers (for styling), auto-tag action columns as nosort, and default
       any table that has a Vessel Name column to ascending order by that column. */
    function initTable(table) {
        if (!table.classList.contains('table') || table.dataset.sortInit) return;
        table.dataset.sortInit = '1';
        var thead = table.tHead;
        if (!thead || !thead.rows.length) return;
        var headerRow = thead.rows[thead.rows.length - 1];     // bottom header row = data columns
        var vesselTh = null, vesselIdx = -1;
        Array.prototype.forEach.call(headerRow.cells, function (th, i) {
            if (th.hasAttribute('data-nosort')) return;
            if (isActionColumn(th, table, i)) { th.setAttribute('data-nosort', ''); return; }
            th.classList.add(SORTABLE);
            if (!th.hasAttribute('title')) th.setAttribute('title', 'Click to sort');
            if (vesselTh === null && isVesselNameHeader(th)) { vesselTh = th; vesselIdx = i; }
        });
        if (vesselTh !== null) {
            performSort(table, vesselTh, vesselIdx, 'asc');
            watchVesselColumn(table, vesselTh, vesselIdx);
        }
    }

    function initAll(root) {
        (root || document).querySelectorAll('table.table').forEach(initTable);
    }

    function sortBy(th) {
        var table = th.closest('table');
        if (!table || !table.classList.contains('table')) return;
        var idx = Array.prototype.indexOf.call(th.parentNode.children, th);
        var dir = th.getAttribute('data-dir') === 'asc' ? 'desc' : 'asc';
        performSort(table, th, idx, dir);
    }

    document.addEventListener('click', function (e) {
        var th = e.target.closest('th');
        if (!th || th.hasAttribute('data-nosort')) return;
        if (e.target.closest('input, button, a, select, label')) return;  // don't hijack header controls
        var table = th.closest('table');
        if (!table || !table.classList.contains('table')) return;
        initTable(table);                                      // ensure init even for late tables
        if (th.hasAttribute('data-nosort')) return;            // may have been auto-tagged just now
        sortBy(th);
    });

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', function () { initAll(); });
    else
        initAll();

    // Expose for any dynamically-injected tables.
    window.initTableSort = initAll;
})();