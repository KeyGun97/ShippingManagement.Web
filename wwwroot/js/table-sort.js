/* ============================================================
   Global table sorting — applies to EVERY <table class="table">
   in the project. Click a header to sort asc, click again for
   desc. Smart compare: numbers, dates (yyyy-MM-dd / dd MMM…),
   then case-insensitive text. Header cells with [data-nosort]
   (checkbox / action columns) are skipped, as are empty-state
   rows that span the table with colspan.
   ============================================================ */
(function () {
    'use strict';

    function cellText(row, idx) {
        var cell = row.cells[idx];
        if (!cell) return '';
        // prefer explicit sort value if provided
        if (cell.hasAttribute('data-sort')) return cell.getAttribute('data-sort');
        return (cell.innerText || cell.textContent || '').trim();
    }

    function parseVal(t) {
        if (t === '' || t === '—' || t === '-') return { type: 'empty', v: '' };
        // numeric (allows 1,234.56)
        var num = t.replace(/,/g, '');
        if (/^-?\d+(\.\d+)?$/.test(num)) return { type: 'num', v: parseFloat(num) };
        // date-ish
        var d = Date.parse(t);
        if (!isNaN(d) && /\d/.test(t) && (t.includes('-') || t.includes('/') || /[A-Za-z]{3}/.test(t)))
            return { type: 'date', v: d };
        return { type: 'text', v: t.toLowerCase() };
    }

    function compare(a, b) {
        var pa = parseVal(a), pb = parseVal(b);
        // empties always sink to the bottom
        if (pa.type === 'empty' && pb.type === 'empty') return 0;
        if (pa.type === 'empty') return 1;
        if (pb.type === 'empty') return -1;
        if (pa.type === pb.type && pa.type !== 'text')
            return pa.v - pb.v;
        var sa = String(pa.v), sb = String(pb.v);
        return sa < sb ? -1 : sa > sb ? 1 : 0;
    }

    document.addEventListener('click', function (e) {
        var th = e.target.closest('th');
        if (!th || th.hasAttribute('data-nosort')) return;
        // don't hijack clicks on interactive elements inside the header
        if (e.target.closest('input, button, a, select, label')) return;
        var thead = th.closest('thead');
        var table = th.closest('table');
        if (!thead || !table || !table.classList.contains('table')) return;
        var tbody = table.tBodies[0];
        if (!tbody) return;

        var idx = Array.prototype.indexOf.call(th.parentNode.children, th);
        var dir = th.getAttribute('data-dir') === 'asc' ? 'desc' : 'asc';

        // reset indicators on sibling headers
        thead.querySelectorAll('th').forEach(function (h) {
            h.removeAttribute('data-dir');
            h.classList.remove('sorted-asc', 'sorted-desc');
        });
        th.setAttribute('data-dir', dir);
        th.classList.add(dir === 'asc' ? 'sorted-asc' : 'sorted-desc');

        var rows = Array.prototype.slice.call(tbody.rows);
        // keep full-width empty/info rows pinned at the bottom
        var spanning = rows.filter(function (r) { return r.cells.length === 1 && r.cells[0].colSpan > 1; });
        var sortable = rows.filter(function (r) { return spanning.indexOf(r) === -1; });

        sortable.sort(function (r1, r2) {
            var c = compare(cellText(r1, idx), cellText(r2, idx));
            return dir === 'asc' ? c : -c;
        });

        var frag = document.createDocumentFragment();
        sortable.concat(spanning).forEach(function (r) { frag.appendChild(r); });
        tbody.appendChild(frag);
    });
})();
