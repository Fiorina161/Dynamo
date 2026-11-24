// Dynamo client script: theme, dialog, toast, drag and drop and workplaces.
// Everything else is htmx attributes in the pages.
(function () {
    'use strict';

    // ---- Theme: light -> dark -> system ---------------------------------
    var root = document.documentElement;
    var toggle = document.getElementById('theme-toggle');
    var modes = ['light', 'dark', ''];

    function current() { return root.dataset.theme || ''; }
    function label() { toggle.textContent = 'Theme: ' + (current() || 'system'); }

    toggle.addEventListener('click', function () {
        var next = modes[(modes.indexOf(current()) + 1) % modes.length];
        if (next) root.dataset.theme = next; else delete root.dataset.theme;
        try { next ? localStorage.setItem('theme', next) : localStorage.removeItem('theme'); }
        catch (e) { }
        label();
    });
    label();

    // ---- Greeting (pages that have one) ----------------------------------
    var hello = document.getElementById('hello');
    if (hello) {
        var now = new Date();
        var hr = now.getHours();
        hello.textContent = hr < 12 ? 'Good morning' : hr < 18 ? 'Good afternoon' : 'Good evening';
        document.getElementById('today').textContent = now.toLocaleDateString(undefined,
            { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
    }

    // ---- Dialog ----------------------------------------------------------
    var dlg = document.getElementById('dlg');

    // A form was loaded into the dialog: show it.
    document.body.addEventListener('htmx:afterSwap', function (e) {
        if (e.detail.target === dlg && !dlg.open) dlg.showModal();
    });
    document.body.addEventListener('closeDialog', function () { if (dlg.open) dlg.close(); });
    dlg.addEventListener('click', function (e) {
        if (e.target === dlg || e.target.closest('[data-close]')) dlg.close();
    });

    // ---- Toast -----------------------------------------------------------
    var toast = document.getElementById('toast');
    var toastTimer;
    document.body.addEventListener('toast', function (e) {
        toast.textContent = e.detail.value;
        toast.classList.add('show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { toast.classList.remove('show'); }, 5000);
    });

    // ---- Selecting rows: checkboxes, select-all, shift-click ---------------
    // A list marked data-selectable has [data-select-item] checkboxes in its
    // rows, one [data-select-all], a [data-select-count] and a
    // [data-select-action] button that is enabled only while something is
    // selected. "All" means all the rows currently shown (not hidden by the
    // filter).
    var lastPicked = null;

    function shownBoxes(root) {
        return Array.prototype.filter.call(root.querySelectorAll('[data-select-item]'),
            function (b) { return !b.closest('.item').hidden; });
    }

    function refreshSelection(root) {
        var shown = shownBoxes(root);
        var n = shown.filter(function (b) { return b.checked; }).length;
        var all = root.querySelector('[data-select-all]');
        if (all) {
            all.checked = n > 0 && n === shown.length;
            all.indeterminate = n > 0 && n < shown.length;
        }
        var count = root.querySelector('[data-select-count]');
        if (count) count.textContent = n > 0 ? n + ' selected' : '';
        var action = root.querySelector('[data-select-action]');
        if (action) action.disabled = n === 0;
    }

    document.addEventListener('click', function (e) {
        var box = e.target.closest && e.target.closest('[data-select-item]');
        if (!box) return;
        var root = box.closest('[data-selectable]');
        // Shift-click: every shown row between the last pick and this one
        // takes the state this click just gave.
        if (e.shiftKey && lastPicked && lastPicked !== box && root.contains(lastPicked)) {
            var shown = shownBoxes(root);
            var from = shown.indexOf(lastPicked);
            var to = shown.indexOf(box);
            if (from >= 0 && to >= 0) {
                for (var i = Math.min(from, to); i <= Math.max(from, to); i++)
                    shown[i].checked = box.checked;
            }
        }
        lastPicked = box;
        refreshSelection(root);
    });

    document.addEventListener('change', function (e) {
        var all = e.target.closest && e.target.closest('[data-select-all]');
        if (!all) return;
        var root = all.closest('[data-selectable]');
        shownBoxes(root).forEach(function (b) { b.checked = all.checked; });
        refreshSelection(root);
    });
    // ---- Filter box: hides the rows (and empty groups) that do not match -----
    document.addEventListener('input', function (e) {
        var box = e.target.closest && e.target.closest('[data-filter]');
        if (!box) return;
        var root = document.querySelector(box.dataset.filter);
        if (!root) return;
        var q = box.value.trim().toLowerCase();
        root.querySelectorAll('.item').forEach(function (li) {
            li.hidden = q !== '' && li.textContent.toLowerCase().indexOf(q) < 0;
        });
        root.querySelectorAll('.group').forEach(function (g) {
            g.hidden = q !== '' && !g.querySelector('.item:not([hidden])');
        });
        // What the filter hides is never selected: nothing out of sight is acted on.
        root.querySelectorAll('[data-select-item]').forEach(function (b) {
            if (b.closest('.item').hidden) b.checked = false;
        });
        refreshSelection(root);
    });

    // ---- A button that posts its form natively (file downloads) ---------------
    // htmx cannot save a response as a file: build a plain form with the same
    // fields and the antiforgery token, and let the browser download the answer.
    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-post-form]');
        if (!btn) return;
        var src = btn.closest('form');
        var form = document.createElement('form');
        form.method = 'post';
        form.action = btn.dataset.postForm;
        function add(name, value) {
            var input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            input.value = value;
            form.appendChild(input);
        }
        new FormData(src).forEach(function (value, name) { add(name, value); });
        // Fields that live outside the button's form (a workspace's shared binds).
        if (btn.dataset.postInclude) {
            var extra = document.querySelector(btn.dataset.postInclude);
            if (extra) new FormData(extra).forEach(function (value, name) { add(name, value); });
        }
        try {
            add('__RequestVerificationToken',
                JSON.parse(document.body.getAttribute('hx-headers')).RequestVerificationToken);
        } catch (err) { }
        document.body.appendChild(form);
        form.submit();
        form.remove();
    });
    // ---- Drag and drop -----------------------------------------------------
    // Two kinds of drag: a bookmark (between and inside groups) and a group
    // (by its header, among the cards). The dragged element follows the
    // pointer live; on drop, the final order is sent to the server.
    var dragged = null;
    var kind = null;       // 'item' | 'group'
    var dropped = false;

    function board() { return document.getElementById('board'); }

    document.addEventListener('dragstart', function (e) {
        if (!e.target.closest) return;
        var item = e.target.closest('.item');
        var head = e.target.closest('.group > header');
        if (item) {
            dragged = item;
            kind = 'item';
        } else if (head && head.parentElement.dataset.id) {
            dragged = head.parentElement;
            kind = 'group';
        } else {
            return;
        }
        dropped = false;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', dragged.dataset.id || '');
        document.body.classList.add('dragging');
        setTimeout(function () { dragged && dragged.classList.add('dragging'); });
    });

    document.addEventListener('dragover', function (e) {
        if (!dragged || !e.target.closest) return;
        if (kind === 'item') overForItem(e); else overForGroup(e);
    });

    function overForItem(e) {
        var group = e.target.closest('.group');
        if (!group) return;
        e.preventDefault();
        // A collapsed card opens while a bookmark is over it.
        if (group.classList.contains('collapsed')) group.classList.add('peek');

        var zone = group.querySelector('.dropzone');
        document.querySelectorAll('.dropzone.over').forEach(function (z) {
            if (z !== zone) z.classList.remove('over');
        });
        zone.classList.add('over');

        var after = itemAfter(zone, e.clientY);
        if (after === dragged) return;
        if (after) zone.insertBefore(dragged, after); else zone.appendChild(dragged);
    }

    // The row the dragged one goes before (null = at the end).
    function itemAfter(zone, y) {
        var rows = Array.prototype.filter.call(zone.children, function (el) {
            return el !== dragged;
        });
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i].getBoundingClientRect();
            if (y < r.top + r.height / 2) return rows[i];
        }
        return null;
    }

    function overForGroup(e) {
        var target = e.target.closest('.group');
        if (!target || !target.dataset.id) return;
        e.preventDefault();
        if (target === dragged) return;
        var r = target.getBoundingClientRect();
        var before = e.clientY < r.top + r.height / 2;
        target.parentNode.insertBefore(dragged, before ? target : target.nextSibling);
    }

    document.addEventListener('drop', function (e) {
        if (!dragged) return;
        e.preventDefault();
        dropped = true;
        var b = board();
        var args = { source: b, target: '#board', swap: 'outerHTML' };
        if (kind === 'item') {
            var zone = dragged.closest('.dropzone');
            var ids = Array.prototype.map.call(zone.children, function (el) { return el.dataset.id; });
            args.values = { groupId: zone.dataset.group, ids: ids.join(',') };
            htmx.ajax('POST', b.dataset.arrange, args);
        } else {
            var groups = document.querySelectorAll('.group[data-id]');
            var order = Array.prototype.map.call(groups, function (g) { return g.dataset.id; });
            args.values = { ids: order.join(',') };
            htmx.ajax('POST', b.dataset.arrangeGroups, args);
        }
    });

    document.addEventListener('dragend', function () {
        // Only a bookmark or group drag started here (a workspace panel has its own).
        if (!dragged) return;
        document.body.classList.remove('dragging');
        document.querySelectorAll('.dropzone.over').forEach(function (z) { z.classList.remove('over'); });
        document.querySelectorAll('.group.peek').forEach(function (g) { g.classList.remove('peek'); });
        if (dragged) dragged.classList.remove('dragging');
        dragged = null;
        // Cancelled (Escape, dropped outside): the page was rearranged live,
        // so show the real order again.
        if (!dropped) {
            htmx.ajax('GET', location.href, { target: '#board', select: '#board', swap: 'outerHTML' });
        }
    });
    // ---- The hamburger menu of small screens ----------------------------------
    // The button is only visible below 760px (css); it folds the menu in and out.
    var side = document.getElementById('side');
    var burger = document.getElementById('menu-toggle');
    if (side && burger) {
        var setMenu = function (open) {
            side.classList.toggle('open', open);
            burger.setAttribute('aria-expanded', open ? 'true' : 'false');
        };
        burger.addEventListener('click', function () { setMenu(!side.classList.contains('open')); });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && side.classList.contains('open')) setMenu(false);
        });
        // A tap anywhere outside the open menu closes it.
        document.addEventListener('click', function (e) {
            if (side.classList.contains('open') && !side.contains(e.target)) setMenu(false);
        });
    }
    // ---- Sorting a result table by its column headers ----------------------
    // Click a header to sort by that column, click again to reverse. NULLs stay
    // last in both directions; numeric columns sort as numbers, the rest as
    // text with numbers inside compared as numbers (item2 < item10). Shift-click
    // (or the Escape key on a focused header) goes back to the query's own
    // order. Only the rows fetched are sorted. The choice lives on the results
    // container (a panel's .panel-results), which survives
    // refreshes: it is applied again to every new result with the same column.
    var SORT_ROOT = '.panel-results';

    // ---- Pinning rows -------------------------------------------------------
    // Click a row to pin it to the top (click again to unpin); the row-number
    // header unpins all. A row is known by its first column's value, plus its
    // rank among the rows with that same value, so a pin survives a refresh
    // even when the other cells change. Pins are kept per results container in
    // this browser (localStorage), like the height.
    function pinStoreKey(root) { return 'pins:' + location.pathname + '#' + (root.id || ''); }

    function pinsOf(root) {
        if (root.dataset.pins === undefined) {
            var stored = null;
            try { stored = localStorage.getItem(pinStoreKey(root)); } catch (err) { }
            root.dataset.pins = stored || '[]';
        }
        try { return new Set(JSON.parse(root.dataset.pins)); } catch (err) { return new Set(); }
    }

    function savePins(root, set) {
        var json = JSON.stringify(Array.from(set));
        root.dataset.pins = json;
        try {
            if (set.size) localStorage.setItem(pinStoreKey(root), json);
            else localStorage.removeItem(pinStoreKey(root));
        } catch (err) { }
    }

    // Gives every row its original position (data-row) and its pin key; returns
    // the rows as they are now on screen.
    function keyRows(body) {
        var rows = Array.prototype.slice.call(body.rows);
        rows.forEach(function (tr, i) { if (tr.dataset.row === undefined) tr.dataset.row = i; });
        var seen = new Map();
        rows.slice().sort(function (a, b) { return a.dataset.row - b.dataset.row; }).forEach(function (tr) {
            // The first data column: not the row number, not the Kill column of the sessions panel.
            var first = 1;
            while (first < tr.cells.length && tr.cells[first].classList.contains('kill-col')) first++;
            var value = first < tr.cells.length ? tr.cells[first].textContent : '';
            var rank = (seen.get(value) || 0) + 1;
            seen.set(value, rank);
            tr.dataset.pinKey = value + String.fromCharCode(1) + rank;
        });
        return rows;
    }

    // Runs the current order again (so the pins apply) and tells the page.
    function rearrange(root, table) {
        if (root.dataset.sortCol !== undefined)
            sortTable(root, table, parseInt(root.dataset.sortCol, 10), root.dataset.sortDir);
        else sortTable(root, table, null);
        root.dispatchEvent(new CustomEvent('resultsorted', { bubbles: true }));
    }

    // ---- Filtering rows by a column's values (like a spreadsheet) -------------------
    // The arrow in each header opens a list of that column's distinct values
    // (with counts, a search box, "Select all" and "Clear this filter"). Unticked
    // values are hidden. A filter is stored as the values left out, not the ones
    // kept, so a new value that shows up in a refresh is visible rather than
    // silently hidden. Several columns combine (a row must pass them all). The
    // filters live on the results container like the sort and the pins, are
    // applied again after every refresh, are kept per page in localStorage, and a
    // filter is dropped when its column is gone or renamed. Rows are hidden with
    // the hidden attribute, so the row numbers (a CSS counter) count what is shown.
    function filterStoreKey(root) { return 'filters:' + location.pathname + '#' + (root.id || ''); }

    function filtersOf(root) {
        if (root.dataset.filters === undefined) {
            var stored = null;
            try { stored = localStorage.getItem(filterStoreKey(root)); } catch (err) { }
            root.dataset.filters = stored || '{}';
        }
        try { return JSON.parse(root.dataset.filters) || {}; } catch (err) { return {}; }
    }

    function saveFilters(root, filters) {
        var json = JSON.stringify(filters);
        root.dataset.filters = json;
        try {
            if (Object.keys(filters).length) localStorage.setItem(filterStoreKey(root), json);
            else localStorage.removeItem(filterStoreKey(root));
        } catch (err) { }
    }

    // A cell's value as a key: NULL, the empty text and "x" are three different things.
    function cellKey(td) { return td.classList.contains('null') ? 'n:' : 'v:' + td.textContent; }
    function keyLabel(key) { return key === 'n:' ? '(NULL)' : (key.length === 2 ? '(empty)' : key.slice(2)); }

    function showFilterStatus(table, active, shown, total) {
        var box = table.closest('.results');
        if (!box || !box.parentNode) return;
        Array.prototype.slice.call(box.parentNode.children).forEach(function (child) {
            if (child.classList.contains('filter-status')) child.remove();
        });
        if (!active) return;
        var bar = document.createElement('div');
        bar.className = 'filter-status';
        var text = document.createElement('span');
        text.textContent = 'Showing ' + shown + ' of ' + total + (total === 1 ? ' row' : ' rows');
        var clear = document.createElement('button');
        clear.type = 'button';
        clear.className = 'ghost small';
        clear.setAttribute('data-clear-filters', '');
        clear.textContent = 'Clear filters';
        bar.appendChild(text);
        bar.appendChild(clear);
        box.parentNode.insertBefore(bar, box);
    }

    // Hides the rows that fail the container's filters.
    function applyFilters(root, table) {
        var head = table.tHead && table.tHead.rows[0];
        var body = table.tBodies[0];
        if (!head || !body) return;
        var filters = filtersOf(root), changed = false;
        Object.keys(filters).forEach(function (col) {
            var th = head.cells[col];
            if (!th || th.textContent.trim() !== filters[col].name) { delete filters[col]; changed = true; }
        });
        if (changed) saveFilters(root, filters);
        var cols = Object.keys(filters).map(function (col) {
            return { col: parseInt(col, 10), out: new Set(filters[col].out) };
        });
        var rows = Array.prototype.slice.call(body.rows), shown = 0;
        rows.forEach(function (tr) {
            var hide = cols.some(function (f) { return f.out.has(cellKey(tr.cells[f.col])); });
            tr.hidden = hide;
            if (!hide) shown++;
        });
        Array.prototype.forEach.call(head.cells, function (th, i) {
            th.classList.toggle('filtered', filters[i] !== undefined);
        });
        showFilterStatus(table, cols.length, shown, rows.length);
    }

    // The filters changed: hide the rows, and tell the page (heights, highlights...).
    function refilter(root) {
        var table = root.querySelector('.results table');
        if (table) applyFilters(root, table);
        root.dispatchEvent(new CustomEvent('resultsorted', { bubbles: true }));
    }

    var filterMenu = null;
    var filterMenuState = null;     // { root, col, name, search }

    function closeFilterMenu() {
        if (filterMenu) filterMenu.hidden = true;
        filterMenuState = null;
    }

    // The distinct values of a column, with how many rows have each, in a sensible order.
    function columnValues(table, col) {
        var numeric = table.tHead.rows[0].cells[col].classList.contains('num');
        var counts = new Map();
        Array.prototype.forEach.call(table.tBodies[0].rows, function (tr) {
            var key = cellKey(tr.cells[col]);
            counts.set(key, (counts.get(key) || 0) + 1);
        });
        var keys = Array.from(counts.keys());
        keys.sort(function (a, b) {
            if (a === 'n:' || b === 'n:') return a === b ? 0 : (a === 'n:' ? 1 : -1);
            var x = a.slice(2), y = b.slice(2);
            if (numeric && x !== '' && y !== '' && isFinite(Number(x)) && isFinite(Number(y))) return Number(x) - Number(y);
            return x.localeCompare(y, undefined, { numeric: true, sensitivity: 'base' });
        });
        return keys.map(function (key) { return { key: key, count: counts.get(key) }; });
    }

    function renderFilterMenu() {
        var st = filterMenuState;
        if (!st || !filterMenu) return;
        var table = st.root.querySelector('.results table');
        var th = table && table.tHead && table.tHead.rows[0].cells[st.col];
        if (!th || th.textContent.trim() !== st.name) { closeFilterMenu(); return; }
        var filters = filtersOf(st.root);
        var out = new Set(filters[st.col] ? filters[st.col].out : []);
        var needle = st.search.toLowerCase();
        var values = columnValues(table, st.col).filter(function (v) {
            return !needle || keyLabel(v.key).toLowerCase().indexOf(needle) >= 0;
        });
        var listed = values.slice(0, 1000);
        var list = filterMenu.querySelector('.fm-list');
        var scrolled = list.scrollTop;
        list.textContent = '';
        listed.forEach(function (v) {
            var label = document.createElement('label');
            var box = document.createElement('input');
            box.type = 'checkbox';
            box.checked = !out.has(v.key);
            box.dataset.key = v.key;
            var text = document.createElement('span');
            text.className = 'fm-text';
            text.textContent = keyLabel(v.key);
            text.title = keyLabel(v.key);
            var count = document.createElement('span');
            count.className = 'count';
            count.textContent = String(v.count);
            label.appendChild(box);
            label.appendChild(text);
            label.appendChild(count);
            list.appendChild(label);
        });
        if (!values.length || values.length > listed.length) {
            var note = document.createElement('p');
            note.className = 'fm-note';
            note.textContent = values.length ? 'Showing the first 1000 values: search to narrow them.' : 'No matching value.';
            list.appendChild(note);
        }
        list.scrollTop = scrolled;
        var all = filterMenu.querySelector('.fm-all input');
        var kept = listed.filter(function (v) { return !out.has(v.key); }).length;
        all.checked = listed.length > 0 && kept === listed.length;
        all.indeterminate = kept > 0 && kept < listed.length;
    }

    function ensureFilterMenu() {
        if (filterMenu) return filterMenu;
        filterMenu = document.createElement('div');
        filterMenu.className = 'filter-menu';
        filterMenu.hidden = true;
        filterMenu.innerHTML =
            '<input type="search" class="fm-search" placeholder="Search values…" autocomplete="off" ' +
            'aria-label="Search the values of this column">' +
            '<label class="fm-all"><input type="checkbox"><span>(Select all)</span></label>' +
            '<div class="fm-list"></div>' +
            '<div class="fm-foot"><button type="button" class="ghost small fm-clear">Clear this filter</button></div>';
        document.body.appendChild(filterMenu);

        filterMenu.querySelector('.fm-search').addEventListener('input', function (e) {
            if (!filterMenuState) return;
            filterMenuState.search = e.target.value;
            renderFilterMenu();
        });
        filterMenu.addEventListener('change', function (e) {
            var st = filterMenuState;
            if (!st) return;
            var filters = filtersOf(st.root);
            var out = new Set(filters[st.col] ? filters[st.col].out : []);
            if (e.target.closest('.fm-all')) {
                // All the values listed (the search narrows them), as in a spreadsheet.
                filterMenu.querySelectorAll('.fm-list input').forEach(function (box) {
                    if (e.target.checked) out.delete(box.dataset.key); else out.add(box.dataset.key);
                });
            } else if (e.target.dataset.key !== undefined) {
                if (e.target.checked) out.delete(e.target.dataset.key); else out.add(e.target.dataset.key);
            } else return;
            if (out.size) filters[st.col] = { name: st.name, out: Array.from(out) };
            else delete filters[st.col];
            saveFilters(st.root, filters);
            refilter(st.root);
            renderFilterMenu();
        });
        filterMenu.querySelector('.fm-clear').addEventListener('click', function () {
            var st = filterMenuState;
            if (!st) return;
            var filters = filtersOf(st.root);
            delete filters[st.col];
            saveFilters(st.root, filters);
            refilter(st.root);
            renderFilterMenu();
        });
        return filterMenu;
    }

    function openFilterMenu(button) {
        var th = button.closest('th');
        var root = th && th.closest(SORT_ROOT);
        if (!th || !root) return;
        var col = th.cellIndex;
        if (filterMenuState && filterMenuState.root === root && filterMenuState.col === col) {
            closeFilterMenu();
            return;
        }
        ensureFilterMenu();
        filterMenuState = { root: root, col: col, name: th.textContent.trim(), search: '' };
        filterMenu.querySelector('.fm-search').value = '';
        renderFilterMenu();
        filterMenu.hidden = false;
        var rect = button.getBoundingClientRect();
        var left = rect.left + window.scrollX;
        var most = window.scrollX + document.documentElement.clientWidth - filterMenu.offsetWidth - 8;
        filterMenu.style.left = Math.max(8, Math.min(left, most)) + 'px';
        filterMenu.style.top = (rect.bottom + window.scrollY + 4) + 'px';
        filterMenu.querySelector('.fm-search').focus();
    }

    function clearAllFilters(button) {
        var root = button.closest(SORT_ROOT);
        if (!root) return;
        saveFilters(root, {});
        refilter(root);
        if (filterMenuState && filterMenuState.root === root) renderFilterMenu();
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && filterMenu && !filterMenu.hidden) closeFilterMenu();
    });

    function sortTable(root, table, col, dir) {
        var body = table.tBodies[0];
        var head = table.tHead && table.tHead.rows[0];
        if (!body || !head) return;
        var rows = keyRows(body);
        var original = function (a, b) { return a.dataset.row - b.dataset.row; };
        if (col === null) {
            rows.sort(original);
        } else {
            var numeric = head.cells[col].classList.contains('num');
            var sign = dir === 'desc' ? -1 : 1;
            var isNull = function (tr) { return tr.cells[col].classList.contains('null'); };
            rows.sort(function (a, b) {
                var na = isNull(a), nb = isNull(b);
                if (na || nb) return na === nb ? original(a, b) : (na ? 1 : -1);
                var x = a.cells[col].textContent, y = b.cells[col].textContent;
                var c;
                if (numeric && isFinite(Number(x)) && isFinite(Number(y))) c = Number(x) - Number(y);
                else c = x.localeCompare(y, undefined, { numeric: true, sensitivity: 'base' });
                return c * sign || original(a, b);
            });
        }
        // Pinned rows stay on top, whatever the order: stable, so they keep the
        // relative order the sort gives them.
        var pins = pinsOf(root);
        if (pins.size) {
            var pinned = [], rest = [];
            rows.forEach(function (tr) { (pins.has(tr.dataset.pinKey) ? pinned : rest).push(tr); });
            rows = pinned.concat(rest);
        }
        rows.forEach(function (tr) {
            body.appendChild(tr);
            tr.classList.toggle('pinned', pins.has(tr.dataset.pinKey));
        });
        applyFilters(root, table);
        if (filterMenuState && filterMenuState.root === root) renderFilterMenu();
        Array.prototype.forEach.call(head.cells, function (th, i) {
            if (col !== null && i === col) th.setAttribute('aria-sort', dir === 'desc' ? 'descending' : 'ascending');
            else th.removeAttribute('aria-sort');
        });
    }

    function clearSort(root) {
        delete root.dataset.sortCol;
        delete root.dataset.sortDir;
        delete root.dataset.sortName;
    }

    // A new result arrived: sort it as the user had it, if it has the same column.
    // Also puts the pinned rows on top when there is no sort.
    function applyStoredSort(root) {
        if (!root) return;
        var table = root.querySelector('.results table');
        if (!table || !table.tHead) return;
        if (root.dataset.sortCol !== undefined) {
            var col = parseInt(root.dataset.sortCol, 10);
            var th = table.tHead.rows[0].cells[col];
            if (th && th.textContent.trim() === root.dataset.sortName) {
                sortTable(root, table, col, root.dataset.sortDir);
                return;
            }
            clearSort(root);
        }
        sortTable(root, table, null);
    }

    function toggleSort(th, restore) {
        if (th.classList.contains('rownum')) return;      // the row numbers
        var root = th.closest(SORT_ROOT);
        var table = th.closest('table');
        if (!root || !table) return;
        if (restore) {
            clearSort(root);
            sortTable(root, table, null);
        } else {
            var col = th.cellIndex;
            var dir = root.dataset.sortCol === String(col) && root.dataset.sortDir === 'asc' ? 'desc' : 'asc';
            root.dataset.sortCol = String(col);
            root.dataset.sortDir = dir;
            root.dataset.sortName = th.textContent.trim();
            sortTable(root, table, col, dir);
        }
        // The panel's change highlighting compares with the order on screen.
        root.dispatchEvent(new CustomEvent('resultsorted', { bubbles: true }));
    }

    document.addEventListener('click', function (e) {
        var filterButton = e.target.closest && e.target.closest('.col-filter');
        if (filterButton) { openFilterMenu(filterButton); return; }
        var clearFilters = e.target.closest && e.target.closest('[data-clear-filters]');
        if (clearFilters) { clearAllFilters(clearFilters); return; }
        // A click outside the open filter list only closes it.
        if (filterMenu && !filterMenu.hidden) {
            if (!filterMenu.contains(e.target)) closeFilterMenu();
            return;
        }
        var th = e.target.closest && e.target.closest('.results th');
        if (th && th.classList.contains('rownum')) {
            var head = th.closest(SORT_ROOT), headTable = th.closest('table');
            if (head && headTable) { savePins(head, new Set()); rearrange(head, headTable); }
            return;
        }
        if (th && th.classList.contains('kill-col')) return;
        if (th) { toggleSort(th, e.shiftKey); return; }

        // A click on a row pins or unpins it (not while the user is selecting text).
        var cell = e.target.closest && e.target.closest('.results tbody td');
        if (!cell || e.target.closest('a, button, input, select, textarea')) return;
        if (window.getSelection && String(window.getSelection())) return;
        // A long cell (cut short on screen), or any cell with Alt: show its whole text; no pin.
        if (!cell.classList.contains('rownum') && !cell.classList.contains('kill-col') && !cell.classList.contains('null') &&
            (e.altKey || cell.classList.contains('long') || cell.scrollWidth > cell.clientWidth + 1)) {
            cell.dispatchEvent(new CustomEvent('cellview', { bubbles: true }));
            return;
        }
        var tr = cell.parentNode, root = tr.closest(SORT_ROOT), table = tr.closest('table');
        if (!root || !table) return;
        keyRows(table.tBodies[0]);
        var pins = pinsOf(root);
        if (pins.has(tr.dataset.pinKey)) pins.delete(tr.dataset.pinKey); else pins.add(tr.dataset.pinKey);
        savePins(root, pins);
        rearrange(root, table);
    });
    document.addEventListener('keydown', function (e) {
        var th = e.target.closest && e.target.closest('.results th');
        if (!th || e.target.closest('.col-filter') || th.classList.contains('kill-col')) return;
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleSort(th, e.shiftKey); }
        else if (e.key === 'Escape') toggleSort(th, true);
    });

    // ---- Wide results ----------------------------------------------------------------
    // [data-wide-toggle] (the ↔ button of a panel) makes the panel take the whole width
    // of the workspace (.panel.full-width, a column span); kept per panel in this browser.
    function setWideButton(button, on) {
        button.setAttribute('aria-pressed', on ? 'true' : 'false');
        button.title = on ? 'Back to normal width' : 'Use the full width';
    }

    function wideKey(panel) { return 'wide:' + location.pathname + '#' + (panel.dataset.panel || ''); }

    document.querySelectorAll('.panel[data-panel]').forEach(function (panel) {
        var saved = null;
        try { saved = localStorage.getItem(wideKey(panel)); } catch (err) { }
        var button = panel.querySelector('[data-wide-toggle]');
        if (saved === '1' && button) { panel.classList.add('full-width'); setWideButton(button, true); }
    });

    document.addEventListener('click', function (e) {
        var button = e.target.closest && e.target.closest('[data-wide-toggle]');
        if (!button) return;
        var panel = button.closest('.panel');
        if (!panel) return;
        var on = !panel.classList.contains('full-width');
        panel.classList.toggle('full-width', on);
        setWideButton(button, on);
        try {
            if (on) localStorage.setItem(wideKey(panel), '1');
            else localStorage.removeItem(wideKey(panel));
        } catch (err) { }
    });

    // ---- Highlighting ------------------------------------------------------------
    // A .highlight-box holds up to three fields (data-slot 1..3, three colours),
    // each an <input data-highlight-scope="<selector>">: what is typed is marked
    // (case does not matter) in every result cell inside the scope: the query
    // all the panels of a workspace. The fields work
    // together: each cell is cut up once, from its plain text, so the colours
    // never nest; where two matches overlap, the lower slot wins. Marks are
    // applied again after each refresh, each text is remembered per page, and
    // each count says how many places matched. Only the cells' own text is
    // touched, so sorting, pinning and the change flash (all of which read
    // text) are unaffected.
    function clearMarks(scope) {
        scope.querySelectorAll('mark.hl').forEach(function (m) {
            var parent = m.parentNode;
            parent.replaceChild(document.createTextNode(m.textContent), m);
            parent.normalize();
        });
    }

    // Marks one cell (its marks were cleared first): adds to counts, one per term.
    function markCell(td, needles, counts) {
        if (td.classList.contains('null')) return;
        var text = td.textContent;
        var lower = text.toLowerCase();
        if (!text || lower.length !== text.length) return;
        var owner = [], any = false;
        needles.forEach(function (needle, k) {
            if (!needle) return;
            var at = lower.indexOf(needle);
            while (at >= 0) {
                counts[k]++;
                any = true;
                for (var i = at; i < at + needle.length; i++) if (owner[i] === undefined) owner[i] = k;
                at = lower.indexOf(needle, at + needle.length);
            }
        });
        if (!any) return;
        var fragment = document.createDocumentFragment();
        var start = 0;
        for (var i = 1; i <= text.length; i++) {
            if (i < text.length && owner[i] === owner[start]) continue;
            var piece = text.slice(start, i);
            if (owner[start] === undefined) fragment.appendChild(document.createTextNode(piece));
            else {
                var mark = document.createElement('mark');
                mark.className = 'hl hl' + (owner[start] + 1);
                mark.textContent = piece;
                fragment.appendChild(mark);
            }
            start = i;
        }
        td.textContent = '';
        td.appendChild(fragment);
    }

    // Returns how many matches each term found.
    function highlightIn(scope, terms) {
        clearMarks(scope);
        var needles = terms.map(function (t) { return t.toLowerCase(); });
        var counts = needles.map(function () { return 0; });
        if (needles.some(Boolean))
            scope.querySelectorAll('.results tbody td').forEach(function (td) {
                if (!td.parentNode.hidden) markCell(td, needles, counts);
            });
        return counts;
    }

    function initHighlightBox(box) {
        var fields = Array.prototype.slice.call(box.querySelectorAll('[data-highlight-scope]')).map(function (input) {
            var holder = input.closest('.highlight-field') || box;
            var slot = parseInt(input.dataset.slot || '1', 10);
            return {
                input: input,
                counter: holder.querySelector('[data-highlight-count]'),
                clear: holder.querySelector('[data-highlight-clear]'),
                key: 'highlight:' + location.pathname + (slot === 1 ? '' : ':' + slot),
            };
        });
        if (!fields.length) return;
        var selector = fields[0].input.dataset.highlightScope;
        var timer = null;

        function apply() {
            var scope = document.querySelector(selector);
            var terms = fields.map(function (f) { return f.input.value.trim(); });
            var counts = scope ? highlightIn(scope, terms) : terms.map(function () { return 0; });
            fields.forEach(function (f, k) {
                if (f.counter) f.counter.textContent = terms[k] ? (counts[k] === 1 ? '1 match' : counts[k] + ' matches') : '';
                if (f.clear) f.clear.hidden = !f.input.value;
                try {
                    if (terms[k]) localStorage.setItem(f.key, f.input.value);
                    else localStorage.removeItem(f.key);
                } catch (err) { }
            });
        }
        function soon(ms) { clearTimeout(timer); timer = setTimeout(apply, ms); }

        fields.forEach(function (f) {
            try { var saved = localStorage.getItem(f.key); if (saved) f.input.value = saved; } catch (err) { }
            f.input.addEventListener('input', function () { soon(120); });
            f.input.addEventListener('keydown', function (e) {
                if (e.key === 'Escape' && f.input.value) { f.input.value = ''; apply(); }
            });
            if (f.clear) f.clear.addEventListener('click', function () { f.input.value = ''; apply(); f.input.focus(); });
        });
        // Rows shown or hidden by a filter, or moved: count what is on screen.
        document.body.addEventListener('resultsorted', function (e) {
            var scope = document.querySelector(selector);
            if (scope && e.target && scope.contains(e.target) &&
                fields.some(function (f) { return f.input.value.trim(); })) soon(30);
        });
        // New results arrive (a run or an auto-refresh): mark them too.
        document.body.addEventListener('htmx:afterSwap', function (e) {
            var scope = document.querySelector(selector);
            if (scope && e.target && scope.contains(e.target) &&
                fields.some(function (f) { return f.input.value.trim(); })) soon(30);
        });
        apply();
    }

    document.querySelectorAll('.highlight-box').forEach(initHighlightBox);

    // ---- A form that takes a while: say so ------------------------------------
    // data-busy-text on a form: its button shows that text and is disabled once
    // the browser has started sending the form.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        var text = form.dataset && form.dataset.busyText;
        if (!text) return;
        var button = form.querySelector('button[type=submit]');
        if (!button) return;
        setTimeout(function () { button.textContent = text; button.disabled = true; }, 0);
    });

    // ---- Waiting for the service to restart after an update ---------------------
    // [data-wait-restart="<address>"] [data-wait-version="<version being installed>"]:
    // polls the site every second and goes to the address (the page of the new version)
    // as soon as it answers with the version that was being installed. It must not wait
    // to SEE the site go down: the restart takes about two seconds, which a poll can fall
    // between entirely (the page then waited for ever while the update was long done).
    // The site answering again after having been down also ends the wait, with whatever
    // the new page says (an update that was rolled back says so). If neither happens it
    // says where to look. It goes there with a plain navigation: reloading a page that
    // came from a form POST would resend the form.
    (function () {
        var marker = document.querySelector('[data-wait-restart]');
        if (!marker) return;
        var status = document.querySelector('[data-wait-status]');
        var target = marker.dataset.waitRestart;
        var expected = marker.dataset.waitVersion || '';
        var started = Date.now();
        var wasDown = false;
        var finished = false;
        function say(text) { if (status) status.textContent = text; }
        function go() {
            if (finished) return;
            finished = true;
            say('Done: opening the page…');
            location.assign(target);
        }
        // The version the menu title of an answered page says is installed.
        function versionOf(html) {
            var m = /class="version"[^>]*>\s*([^<\s]+)/.exec(html);
            return m ? m[1] : '';
        }
        function poll() {
            if (finished) return;
            var seconds = Math.round((Date.now() - started) / 1000);
            fetch('/Settings', { cache: 'no-store' }).then(function (r) {
                if (!r.ok) { wasDown = true; return null; }
                return r.text();
            }).then(function (html) {
                if (html === null) { next(seconds); return; }
                if ((expected && versionOf(html) === expected) || wasDown) { go(); return; }
                next(seconds);
            }).catch(function () { wasDown = true; next(seconds); });
        }
        function next(seconds) {
            if (seconds > 240) {
                say('Dynamo has not answered with the new version after ' + seconds + ' s. Look at update.log in the data ' +
                    'folder (C:\\dynamo by default), or at the service in the Windows Services list.');
                return;
            }
            say(wasDown ? 'Restarting… ' + seconds + ' s' : 'Installing… ' + seconds + ' s');
            setTimeout(poll, 1000);
        }
        setTimeout(poll, 1500);
    })();

    // ---- Height of the results, in rows ------------------------------------------
    // An input marked data-view-rows-for="<selector>" says how many rows of the
    // result show before it scrolls (default 10). It is the panel's own setting,
    // saved on the server by the input's hx-post. The height is
    // measured from the real rows: the header plus the first N rows, so wrapped
    // or tall rows still count as one row each.
    var DEFAULT_VIEW_ROWS = 10;
    var MAX_VIEW_ROWS = 1000;

    // A panel's own number; 0 (or nothing) means the default number of rows from Settings,
    // which the page puts on the input (data-view-rows-default).
    function viewRowsOf(target) {
        var n = parseInt(target.dataset.viewRows, 10);
        if (!(n >= 1)) n = parseInt(target.dataset.viewRowsDefault, 10);
        return n >= 1 ? Math.min(n, MAX_VIEW_ROWS) : DEFAULT_VIEW_ROWS;
    }

    // Also makes the panel exactly N rows tall: its results container gets a
    // min-height of the header plus N rows, so "Visible rows" is the height, not a
    // ceiling: a result with fewer rows, an error or no rows leaves blank space
    // inside the panel (the owner asked for it: the number set is the number
    // respected). It also keeps the panel from changing height when a refresh
    // changes the number of rows, which would make the panels hop between the
    // page's columns. The row and header heights are measured when there are rows
    // and remembered in this browser (localStorage), so the first paint, before any
    // result, already has the right height.
    var METRICS_KEY = 'resultMetrics';

    function savedMetrics() {
        try {
            var m = JSON.parse(localStorage.getItem(METRICS_KEY));
            if (m && m.head > 0 && m.row > 0) return m;
        } catch (err) { }
        return { head: 30, row: 29 };
    }

    // The thickness of the browser's scrollbars (0 for overlay ones), measured once with
    // a hidden scrolling box. Every panel reserves it, so the panels are the same height
    // whether or not their result has a horizontal scrollbar.
    var scrollbarSize = null;
    function scrollbarThickness() {
        if (scrollbarSize === null) {
            var probe = document.createElement('div');
            probe.style.cssText = 'position:absolute;visibility:hidden;width:60px;height:60px;overflow:scroll';
            document.body.appendChild(probe);
            scrollbarSize = Math.max(0, probe.offsetHeight - probe.clientHeight);
            document.body.removeChild(probe);
        }
        return scrollbarSize;
    }

    function fitResults(target) {
        if (!target) return;
        var n = viewRowsOf(target);
        var remembered = savedMetrics();
        var headHeight = remembered.head;
        var rowHeight = remembered.row;
        var box = target.querySelector('.results');
        var table = box && box.querySelector('table');
        var scrollbar = scrollbarThickness();
        if (table && table.tBodies[0]) {
            var rows = Array.prototype.filter.call(table.tBodies[0].rows, function (tr) { return !tr.hidden; });
            var measuredHead = table.tHead ? table.tHead.offsetHeight : 0;
            var measuredRow = rows.length ? rows[0].offsetHeight : 0;
            if (measuredHead > 0) headHeight = measuredHead;
            if (measuredRow > 0) rowHeight = measuredRow;
            if ((measuredHead > 0 || measuredRow > 0) && (headHeight !== remembered.head || rowHeight !== remembered.row)) {
                try { localStorage.setItem(METRICS_KEY, JSON.stringify({ head: headHeight, row: rowHeight })); } catch (err) { }
            }
            if (rows.length <= n) {
                box.style.maxHeight = '';
            } else {
                var height = table.tHead ? table.tHead.offsetHeight : 0;
                for (var i = 0; i < n; i++) height += rows[i].offsetHeight;
                if (height > 0) box.style.maxHeight = (height + 1) + 'px';
            }
            // A horizontal scrollbar (a wide result) takes room from the rows: add its
            // thickness, so N rows are N whole rows.
            if (box.scrollWidth > box.clientWidth && rows.length > n && box.style.maxHeight)
                box.style.maxHeight = (parseFloat(box.style.maxHeight) + scrollbar) + 'px';
        }
        target.style.minHeight = Math.round(headHeight + n * rowHeight + 1 + scrollbar) + 'px';
    }

    function fitAllResults() {
        document.querySelectorAll('[data-view-rows-for]').forEach(function (input) {
            var target = document.querySelector(input.dataset.viewRowsFor);
            if (target) fitResults(target);
        });
    }

    function initViewRows() {
        document.querySelectorAll('[data-view-rows-for]').forEach(function (input) {
            var target = document.querySelector(input.dataset.viewRowsFor);
            if (!target) return;
            target.dataset.viewRows = input.value;
            target.dataset.viewRowsDefault = input.dataset.viewRowsDefault || '';
            fitResults(target);       // the panel's height from the start, before any result
            input.addEventListener('input', function () {
                target.dataset.viewRows = input.value;
                fitResults(target);
            });
        });
    }

    // A new result arrives in a results container, or its rows are re-sorted.
    document.body.addEventListener('htmx:afterSwap', function (e) {
        if (e.target && e.target.dataset && e.target.dataset.viewRows !== undefined) fitResults(e.target);
    });
    document.body.addEventListener('resultsorted', function (e) {
        var target = e.target.closest && e.target.closest('[data-view-rows]');
        if (target) fitResults(target);
    });
    var fitTimer = null;
    window.addEventListener('resize', function () {
        clearTimeout(fitTimer);
        fitTimer = setTimeout(fitAllResults, 200);
    });
    initViewRows();

    // ---- Reordering panels by dragging -----------------------------------------------
    // The ⠿ handle in a panel's title bar: press and drag (mouse, pen or finger: pointer
    // events), a line shows where the panel will land, release to drop it there. The new
    // order is saved with one request (every panel id, in order) and the page is not
    // reloaded. Escape cancels. On the handle, the up and down arrow keys move the panel
    // one place. The panels flow down the columns of the page, so what is saved is a
    // sequence: the dragged panel goes "before" or "after" the panel nearest to the
    // pointer. The earlier/later buttons in each panel's details stay.
    (function () {
        var grid = document.querySelector('[data-workplace][data-reorder-url]');
        if (!grid) return;
        var marker = document.createElement('div');
        marker.className = 'panel-drop-marker';
        marker.hidden = true;
        grid.appendChild(marker);
        var drag = null;

        function panelsOf() {
            return Array.prototype.filter.call(grid.children, function (el) { return el.classList.contains('panel'); });
        }

        function orderOf() {
            return panelsOf().map(function (panel) { return panel.dataset.panel; });
        }

        // The panel nearest to the pointer, and whether the pointer is in its upper half.
        function slotAt(x, y, dragged) {
            var best = null, bestDistance = Infinity;
            panelsOf().forEach(function (panel) {
                if (panel === dragged) return;
                var r = panel.getBoundingClientRect();
                var dx = x < r.left ? r.left - x : (x > r.right ? x - r.right : 0);
                var dy = y < r.top ? r.top - y : (y > r.bottom ? y - r.bottom : 0);
                var distance = Math.sqrt(dx * dx + dy * dy);
                if (distance < bestDistance) { bestDistance = distance; best = { panel: panel, rect: r }; }
            });
            if (best) best.before = y < best.rect.top + best.rect.height / 2;
            return best;
        }

        function showMarker(slot) {
            if (!slot) { marker.hidden = true; return; }
            var box = grid.getBoundingClientRect();
            var edge = slot.before ? slot.rect.top : slot.rect.bottom;
            marker.style.left = (slot.rect.left - box.left) + 'px';
            marker.style.width = slot.rect.width + 'px';
            marker.style.top = (edge - box.top - 2) + 'px';
            marker.hidden = false;
        }

        // The earlier/later buttons in the details: the first panel cannot go earlier, the last later.
        function refreshMoveButtons() {
            var list = panelsOf();
            list.forEach(function (panel, i) {
                var earlier = panel.querySelector('[data-move="-1"]');
                var later = panel.querySelector('[data-move="1"]');
                if (earlier) earlier.disabled = i === 0;
                if (later) later.disabled = i === list.length - 1;
            });
        }

        function saveOrder() {
            var token = '';
            try { token = JSON.parse(document.body.getAttribute('hx-headers')).RequestVerificationToken; } catch (err) { }
            refreshMoveButtons();
            fetch(grid.dataset.reorderUrl, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token, 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'order=' + encodeURIComponent(orderOf().join(','))
            }).then(function (response) {
                if (!response.ok) throw new Error('status ' + response.status);
            }).catch(function () {
                // The server did not take it (the page was out of date?): show the real order.
                location.reload();
            });
        }

        function stop() {
            if (!drag) return;
            clearInterval(drag.scroller);
            drag.panel.classList.remove('dragging');
            document.body.classList.remove('panel-dragging');
            marker.hidden = true;
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            document.removeEventListener('pointercancel', stop);
            document.removeEventListener('keydown', onKey);
            drag = null;
        }

        function update() {
            if (!drag) return;
            drag.slot = slotAt(drag.x, drag.y, drag.panel);
            showMarker(drag.slot);
        }

        function onMove(e) {
            if (!drag) return;
            drag.x = e.clientX;
            drag.y = e.clientY;
            if (Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY) > 4) drag.moved = true;
            update();
        }

        function onUp() {
            if (!drag) return;
            var finished = drag;
            stop();
            if (!finished.moved || !finished.slot) return;
            if (finished.slot.before) grid.insertBefore(finished.panel, finished.slot.panel);
            else grid.insertBefore(finished.panel, finished.slot.panel.nextSibling);
            if (orderOf().join(',') !== finished.original) saveOrder();
        }

        function onKey(e) {
            if (e.key === 'Escape') stop();
        }

        grid.addEventListener('pointerdown', function (e) {
            var grip = e.target.closest && e.target.closest('[data-panel-grip]');
            if (!grip || drag || (e.button !== undefined && e.button !== 0)) return;
            var panel = grip.closest('.panel');
            if (!panel) return;
            e.preventDefault();
            drag = {
                panel: panel, startX: e.clientX, startY: e.clientY, x: e.clientX, y: e.clientY,
                moved: false, slot: null, original: orderOf().join(','),
                // Near the top or the bottom of the window, the page scrolls on its own.
                scroller: setInterval(function () {
                    if (!drag) return;
                    if (drag.y < 60) window.scrollBy(0, -16);
                    else if (drag.y > window.innerHeight - 60) window.scrollBy(0, 16);
                    else return;
                    update();
                }, 30)
            };
            panel.classList.add('dragging');
            document.body.classList.add('panel-dragging');
            document.addEventListener('pointermove', onMove);
            document.addEventListener('pointerup', onUp);
            document.addEventListener('pointercancel', stop);
            document.addEventListener('keydown', onKey);
        });

        // From the keyboard: the arrow keys on the handle move the panel one place.
        grid.addEventListener('keydown', function (e) {
            var grip = e.target.closest && e.target.closest('[data-panel-grip]');
            if (!grip) return;
            var up = e.key === 'ArrowUp' || e.key === 'ArrowLeft';
            var down = e.key === 'ArrowDown' || e.key === 'ArrowRight';
            if (!up && !down) return;
            e.preventDefault();
            var panel = grip.closest('.panel');
            var list = panelsOf();
            var i = list.indexOf(panel);
            if (up && i > 0) grid.insertBefore(panel, list[i - 1]);
            else if (down && i >= 0 && i < list.length - 1) grid.insertBefore(panel, list[i + 1].nextSibling);
            else return;
            grip.focus();
            saveOrder();
        });
    })();

    // ---- Result tools: copy, vertical view, readable sizes, full text -------------------------------
    // For the results of every panel (a .panel-results container). The ⋯ button of the title bar opens a
    // menu: copy what is on screen (the visible rows, in the order shown) as TSV (for Excel), as a Markdown
    // table or as a formatted table; see a result of one row as a list down the page (vertical view); read
    // the byte columns as KB, MB, GB. A click on a long cell (or Alt-click on any) opens its whole text.
    // All client side, on the rows fetched. The two toggles are kept per panel in this browser.
    (function () {
        var BYTE_COLUMN = /(^|_)bytes($|_)/i;       // INPUT_BYTES, BYTES, BYTES_FREE...
        var LONG_CELL = 120;                        // as in the cell's title (_ResultTable)

        function say(message) {
            document.body.dispatchEvent(new CustomEvent('toast', { detail: { value: message } }));
        }
        function prefKey(kind, root) { return kind + ':' + location.pathname + '#' + (root.id || ''); }
        function getPref(kind, root) {
            try { return localStorage.getItem(prefKey(kind, root)) === '1'; } catch (err) { return false; }
        }
        function setPref(kind, root, on) {
            try {
                if (on) localStorage.setItem(prefKey(kind, root), '1');
                else localStorage.removeItem(prefKey(kind, root));
            } catch (err) { }
        }

        // What the results container shows, as data: the column names, which are numbers, and the
        // visible rows in the order on screen (the row-number and Kill columns are left out).
        function readTable(root) {
            var table = root.querySelector('.results table');
            if (!table || !table.tBodies[0] || !table.tHead) return null;
            var heads = Array.prototype.slice.call(table.tHead.rows[0].cells);
            var cols = [];
            heads.forEach(function (th, i) {
                if (!th.classList.contains('rownum') && !th.classList.contains('kill-col')) cols.push(i);
            });
            var data = {
                head: cols.map(function (i) { return heads[i].textContent.trim(); }),
                numeric: cols.map(function (i) { return heads[i].classList.contains('num'); }),
                rows: [], nulls: [], cols: cols, table: table
            };
            Array.prototype.forEach.call(table.tBodies[0].rows, function (tr) {
                if (tr.hidden) return;
                data.rows.push(cols.map(function (i) {
                    var td = tr.cells[i];
                    return !td || td.classList.contains('null') ? '' : td.textContent;
                }));
                data.nulls.push(cols.map(function (i) { return !tr.cells[i] || tr.cells[i].classList.contains('null'); }));
            });
            return data;
        }

        function tsvCell(s) { return /[\t\n\r"]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; }
        function toTsv(d) {
            return [d.head].concat(d.rows).map(function (r) { return r.map(tsvCell).join('\t'); }).join('\r\n');
        }
        function mdCell(s) { return s.replace(/\|/g, '\\|').replace(/\r?\n/g, '<br>'); }
        function toMarkdown(d) {
            var lines = [
                '| ' + d.head.map(mdCell).join(' | ') + ' |',
                '| ' + d.numeric.map(function (n) { return n ? '---:' : '---'; }).join(' | ') + ' |'
            ];
            d.rows.forEach(function (r) { lines.push('| ' + r.map(mdCell).join(' | ') + ' |'); });
            return lines.join('\n');
        }
        function esc(s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
        function toHtml(d) {
            var h = '<table border="1" cellspacing="0" cellpadding="3"><thead><tr>' +
                d.head.map(function (n) { return '<th>' + esc(n) + '</th>'; }).join('') + '</tr></thead><tbody>';
            d.rows.forEach(function (r) {
                h += '<tr>' + r.map(function (c, i) {
                    return '<td' + (d.numeric[i] ? ' align="right"' : '') + '>' + esc(c) + '</td>';
                }).join('') + '</tr>';
            });
            return h + '</tbody></table>';
        }

        // Copies text (and, when given, the same as HTML). A hidden text box and the copy command work
        // on plain http too (the clipboard API needs https); inside an open dialog the box must be
        // inside it, since the rest of the page is inert.
        function copy(text, html) {
            var ok = false;
            function onCopy(e) {
                e.clipboardData.setData('text/plain', text);
                if (html) e.clipboardData.setData('text/html', html);
                e.preventDefault();
            }
            var host = document.querySelector('dialog[open]') || document.body;
            var box = document.createElement('textarea');
            box.value = text;
            box.setAttribute('readonly', '');
            box.style.cssText = 'position:fixed;left:-9999px;top:0;width:1px;height:1px';
            host.appendChild(box);
            box.select();
            document.addEventListener('copy', onCopy);
            try { ok = document.execCommand('copy'); } catch (err) { }
            document.removeEventListener('copy', onCopy);
            host.removeChild(box);
            if (!ok && navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).catch(function () { });
                ok = true;
            }
            return ok;
        }

        function plural(n, word) { return n + ' ' + word + (n === 1 ? '' : 's'); }

        // ---- Readable sizes
        function humanBytes(n) {
            if (!isFinite(n)) return null;
            var units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'], i = 0, v = Math.abs(n);
            while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
            var s = i === 0 ? String(Math.round(v)) : v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2);
            return (n < 0 ? '-' : '') + s + ' ' + units[i];
        }
        function byteColumns(table) {
            var cols = [];
            if (!table || !table.tHead) return cols;
            Array.prototype.forEach.call(table.tHead.rows[0].cells, function (th, i) {
                if (th.classList.contains('num') && BYTE_COLUMN.test(th.textContent.trim())) cols.push(i);
            });
            return cols;
        }
        // The raw number stays in the cell (sorting, filtering and copying read it); a data attribute
        // shows the readable form instead, through the stylesheet.
        function applySizes(root) {
            var table = root.querySelector('.results table');
            var on = getPref('sizes', root);
            root.classList.remove('human-sizes');
            if (!table || !table.tBodies[0]) return;
            var cols = byteColumns(table);
            Array.prototype.forEach.call(table.tBodies[0].rows, function (tr) {
                cols.forEach(function (i) {
                    var td = tr.cells[i];
                    if (!td) return;
                    var human = on && !td.classList.contains('null') ? humanBytes(Number(td.textContent)) : null;
                    if (human) td.setAttribute('data-human', human); else td.removeAttribute('data-human');
                });
            });
            if (on && cols.length) root.classList.add('human-sizes');
        }

        // ---- Vertical view of a one-row result
        function applyVertical(root) {
            var old = root.querySelector('.results-vertical');
            if (old) old.parentNode.removeChild(old);
            var d = readTable(root);
            var on = getPref('vertical', root) && d && d.rows.length === 1 && d.head.length > 1;
            root.classList.toggle('vertical-on', !!on);
            if (!on) return;
            var box = document.createElement('div');
            box.className = 'results-vertical';
            var table = document.createElement('table');
            var body = document.createElement('tbody');
            d.head.forEach(function (name, i) {
                var tr = document.createElement('tr');
                var th = document.createElement('th');
                th.textContent = name;
                var td = document.createElement('td');
                if (d.nulls[0][i]) { td.className = 'null'; td.textContent = 'NULL'; }
                else {
                    td.textContent = d.rows[0][i];
                    if (d.numeric[i]) td.classList.add('num');
                    if (d.rows[0][i].length > LONG_CELL) td.classList.add('long');
                }
                tr.appendChild(th);
                tr.appendChild(td);
                body.appendChild(tr);
            });
            table.appendChild(body);
            box.appendChild(table);
            var results = root.querySelector('.results');
            results.parentNode.insertBefore(box, results.nextSibling);
        }

        function applyAll(root) {
            if (!root) return;
            applySizes(root);
            applyVertical(root);
        }

        // New results (a run, an auto-refresh), or the rows re-sorted or filtered: show them as chosen.
        document.body.addEventListener('htmx:afterSwap', function (e) {
            var t = e.target;
            if (!t || !t.querySelectorAll) return;
            var roots = t.classList && t.classList.contains('panel-results')
                ? [t] : Array.prototype.slice.call(t.querySelectorAll('.panel-results'));
            roots.forEach(applyAll);
        });
        document.body.addEventListener('resultsorted', function (e) {
            var root = e.target.closest && e.target.closest('.panel-results');
            if (root) applyAll(root);
        });
        document.querySelectorAll('.panel-results').forEach(applyAll);

        // ---- The menu of the ⋯ button
        var menu = document.createElement('div');
        menu.className = 'result-menu';
        menu.hidden = true;
        menu.setAttribute('role', 'menu');
        document.body.appendChild(menu);
        var menuRoot = null;

        function closeMenu() { menu.hidden = true; menuRoot = null; }

        function addItem(label, enabled, title, action) {
            var b = document.createElement('button');
            b.type = 'button';
            b.setAttribute('role', 'menuitem');
            b.textContent = label;
            if (title) b.title = title;
            if (!enabled) b.disabled = true;
            else b.addEventListener('click', function () { var root = menuRoot; closeMenu(); action(root); });
            menu.appendChild(b);
        }

        function copyAs(kind) {
            return function (root) {
                var d = readTable(root);
                if (!d) return;
                var n = d.rows.length;
                if (kind === 'tsv') copy(toTsv(d));
                else if (kind === 'md') copy(toMarkdown(d));
                else copy(toTsv(d), toHtml(d));
                say('Copied ' + plural(n, 'row') + ' as ' +
                    (kind === 'tsv' ? 'TSV' : kind === 'md' ? 'a Markdown table' : 'a table') + '.');
            };
        }

        function openMenu(button) {
            var panel = button.closest('.panel');
            var root = panel && panel.querySelector('.panel-results');
            if (!root) return;
            menuRoot = root;
            var d = readTable(root);
            var hasBytes = !!(d && byteColumns(d.table).length);
            var vertical = getPref('vertical', root), sizes = getPref('sizes', root);
            menu.textContent = '';
            addItem('Copy as TSV (paste into Excel)', !!d, 'The rows on screen, tab separated', copyAs('tsv'));
            addItem('Copy as Markdown table', !!d, null, copyAs('md'));
            addItem('Copy as formatted table', !!d, 'Pastes as a table in Word, Outlook, Excel', copyAs('html'));
            menu.appendChild(document.createElement('hr'));
            addItem((vertical ? '✓ ' : '') + 'Vertical view', !!d && (d.rows.length === 1 || vertical),
                'A result of one row, listed down the page: one line per column',
                function (r) { setPref('vertical', r, !getPref('vertical', r)); applyVertical(r); });
            addItem((sizes ? '✓ ' : '') + 'Readable sizes (KB, MB, GB)', hasBytes || sizes,
                'Shows the columns whose name has BYTES in it as KB, MB or GB',
                function (r) { setPref('sizes', r, !getPref('sizes', r)); applySizes(r); });
            menu.hidden = false;
            var rect = button.getBoundingClientRect();
            var width = menu.offsetWidth || 240;
            menu.style.left = Math.max(8, rect.right + window.pageXOffset - width) + 'px';
            menu.style.top = (rect.bottom + window.pageYOffset + 4) + 'px';
        }

        document.addEventListener('click', function (e) {
            var tools = e.target.closest && e.target.closest('[data-result-tools]');
            if (tools) {
                var same = !menu.hidden && menuRoot && menuRoot === (tools.closest('.panel') || document).querySelector('.panel-results');
                if (same) closeMenu(); else openMenu(tools);
                return;
            }
            if (!menu.hidden && !menu.contains(e.target)) closeMenu();
            var copier = e.target.closest && e.target.closest('[data-copy-target]');
            if (copier) {
                var source = document.querySelector(copier.dataset.copyTarget);
                if (source) say(copy(source.textContent) ? 'Copied.' : 'Could not copy.');
            }
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !menu.hidden) closeMenu();
        });

        // ---- The whole text of a cell: a long one, or any with Alt-click (the click handler of the
        // result tables sends the event, so a click on a long cell does not pin its row)
        function openCell(name, text) {
            var shown = text, trimmed = text.trim();
            if (/^[\[{]/.test(trimmed)) {
                try { shown = JSON.stringify(JSON.parse(trimmed), null, 2); } catch (err) { }
            }
            dlg.textContent = '';
            var form = document.createElement('form');
            form.className = 'wide cell-viewer';
            form.addEventListener('submit', function (e) { e.preventDefault(); });
            var title = document.createElement('h2');
            title.textContent = name;
            var pre = document.createElement('pre');
            pre.className = 'plan-text';
            pre.id = 'cell-text';
            pre.textContent = shown;
            var footer = document.createElement('footer');
            var copyButton = document.createElement('button');
            copyButton.type = 'button';
            copyButton.className = 'ghost';
            copyButton.setAttribute('data-copy-target', '#cell-text');
            copyButton.textContent = 'Copy';
            var spacer = document.createElement('span');
            spacer.className = 'spacer';
            var close = document.createElement('button');
            close.type = 'button';
            close.setAttribute('data-close', '');
            close.textContent = 'Close';
            footer.appendChild(copyButton);
            footer.appendChild(spacer);
            footer.appendChild(close);
            form.appendChild(title);
            form.appendChild(pre);
            form.appendChild(footer);
            dlg.appendChild(form);
            if (!dlg.open) dlg.showModal();
        }

        document.addEventListener('cellview', function (e) {
            var td = e.target.closest && e.target.closest('td');
            if (!td) return;
            var table = td.closest('table'), name = '';
            if (table && table.tHead && table.tHead.rows[0] && table.tHead.rows[0].cells[td.cellIndex])
                name = table.tHead.rows[0].cells[td.cellIndex].textContent.trim();
            var row = td.parentNode;
            if (table.parentNode.classList.contains('results-vertical') && row.cells[0]) name = row.cells[0].textContent.trim();
            openCell(name || 'Value', td.textContent);
        });

        // In the vertical view a click on a long value opens it too (nothing there pins).
        document.addEventListener('click', function (e) {
            var td = e.target.closest && e.target.closest('.results-vertical td');
            if (td && !td.classList.contains('null') && (td.classList.contains('long') || e.altKey))
                td.dispatchEvent(new CustomEvent('cellview', { bubbles: true }));
        });
    })();

    // ---- Connection health page ---------------------------------------------------------------
    // "Test all again" fires the retest event every row listens to (htmx, trigger "retest from:body");
    // the summary counts the rows as their answers arrive.
    (function () {
        var summary = document.querySelector('[data-health-summary]');
        if (!summary) return;
        function count() {
            var rows = document.querySelectorAll('tr[data-health-state]');
            var up = 0, down = 0, waiting = 0;
            rows.forEach(function (tr) {
                var s = tr.getAttribute('data-health-state');
                if (s === 'up') up++; else if (s === 'testing') waiting++; else down++;
            });
            var parts = [up + ' up'];
            if (down) parts.push(down + ' down');
            if (waiting) parts.push(waiting + ' testing');
            summary.textContent = parts.join(' · ');
        }
        var button = document.querySelector('[data-retest-all]');
        if (button) button.addEventListener('click', function () {
            document.querySelectorAll('tr[data-health-state]').forEach(function (tr) {
                tr.setAttribute('data-health-state', 'testing');
                var pill = tr.querySelector('.health-pill');
                if (pill) pill.textContent = 'Testing…';
            });
            count();
            htmx.trigger(document.body, 'retest');
        });
        // Rows are replaced as their answers arrive: count again whenever the table changes.
        var table = document.querySelector('.health-table tbody');
        if (table) new MutationObserver(count).observe(table, { childList: true, subtree: true });
        count();
    })();

    // ---- Panel title colour = its connection's colour ------------------------------------------
    // The server draws it (class conn-<colour> on the panel); when the connection is changed in
    // the panel's settings the class follows at once. Each <option> carries data-color.
    document.addEventListener('change', function (e) {
        var select = e.target;
        if (!select || select.name !== 'connection' || !select.closest) return;
        var panel = select.closest('.panel');
        if (!panel) return;
        Array.prototype.slice.call(panel.classList).forEach(function (c) {
            if (c.indexOf('conn-') === 0) panel.classList.remove(c);
        });
        var option = select.options[select.selectedIndex];
        var color = option && option.getAttribute('data-color');
        if (color) panel.classList.add('conn-' + color);
    });

    // ---- Collapsing panels -------------------------------------------------------------
    // The ▾ in a panel's title bar (or a click on the title) collapses the panel to its title
    // line, and expands it again. A collapsed panel keeps refreshing, so its title line (the
    // status dot, the fetched time, rows and duration, and a small info text: refresh state, row
    // change, filter) stays current. "Collapse all" / "Expand all" in the page header does every
    // panel. The state is saved on the server (with the panel's other settings), so a duplicate
    // and every browser agree; the page is not reloaded. The ⚙ on a collapsed panel expands it
    // (its details open at the same time).
    (function () {
        var grid = document.querySelector('[data-workplace][data-collapse-url]');
        if (!grid) return;
        var allButton = document.querySelector('[data-workplace-collapse-all]');

        function panelsOf() {
            return Array.prototype.filter.call(grid.children, function (el) { return el.classList.contains('panel'); });
        }

        function isCollapsed(panel) { return panel.classList.contains('collapsed'); }

        function syncAllButton() {
            if (!allButton) return;
            var list = panelsOf();
            allButton.textContent = list.length > 0 && list.every(isCollapsed) ? 'Expand all' : 'Collapse all';
        }

        function save(url, collapsed) {
            var token = '';
            try { token = JSON.parse(document.body.getAttribute('hx-headers')).RequestVerificationToken; } catch (err) { }
            fetch(url, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token, 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'collapsed=' + (collapsed ? 'true' : 'false')
            }).then(function (response) {
                if (!response.ok) throw new Error('status ' + response.status);
            }).catch(function () {
                location.reload();      // the server did not take it: show the real state
            });
        }

        function setCollapsed(panel, on) {
            panel.classList.toggle('collapsed', on);
            var button = panel.querySelector('[data-panel-collapse]');
            if (button) {
                button.setAttribute('aria-expanded', on ? 'false' : 'true');
                button.title = on ? 'Expand this panel' : 'Collapse this panel to its title';
            }
            // Back from hidden: measure the results again (they could not be while hidden).
            if (!on) fitResults(panel.querySelector('.panel-results'));
        }

        function toggle(panel) {
            var on = !isCollapsed(panel);
            setCollapsed(panel, on);
            syncAllButton();
            save(grid.dataset.collapseUrl + '&panel=' + panel.dataset.panel, on);
        }

        grid.addEventListener('click', function (e) {
            if (e.target.closest('[data-panel-collapse], [data-panel-title]')) {
                var panel = e.target.closest('.panel');
                if (panel) toggle(panel);
                return;
            }
            // The gear of a collapsed panel expands it (the details open too: see initWorkplace).
            var gear = e.target.closest('[data-panel-toggle]');
            var owner = gear && gear.closest('.panel');
            if (owner && isCollapsed(owner)) {
                setCollapsed(owner, false);
                syncAllButton();
                save(grid.dataset.collapseUrl + '&panel=' + owner.dataset.panel, false);
            }
        });

        if (allButton) allButton.addEventListener('click', function () {
            var list = panelsOf();
            var on = !list.every(isCollapsed);      // some are open: collapse them all; else expand them all
            list.forEach(function (panel) { setCollapsed(panel, on); });
            syncAllButton();
            save(grid.dataset.collapseAllUrl, on);
        });

        syncAllButton();
    })();

    // ---- Workplaces: several queries refreshing side by side -------------------
    // [data-workplace] holds .panel sections. Each has a form (.panel-form, an
    // htmx POST that fills .panel-results), a seconds input and a status line.
    // Each panel refreshes on its own: the next run is scheduled after the
    // previous answer; a failed run stops that panel only; a hidden browser
    // tab waits (nothing runs until it is shown again). The cells that differ
    // from the previous result flash, and the scroll position of a table is
    // kept across refreshes.
    function initWorkplace(grid) {
        var panels = [];
        var paused = false;
        var pauseButton = document.querySelector('[data-workplace-pause]');

        function makePanel(el) {
            var form = el.querySelector('.panel-form');
            var out = el.querySelector('.panel-results');
            var secondsInput = el.querySelector('[data-panel-seconds]');
            var status = el.querySelector('[data-panel-status]');
            var dot = el.querySelector('[data-panel-dot]');
            var info = el.querySelector('[data-panel-info]');
            var gear = el.querySelector('[data-panel-toggle]');
            if (!form || !out || !secondsInput || !status) return null;

            var ticker = null;
            var nextAt = 0;
            var inFlight = false;
            var failed = false;
            var lastRun = null;
            var lastText = '';
            var lastDelta = '';       // the change in the row count since the last run, for the collapsed title
            var previous = null;      // { head: string, rows: [[text]] }
            var scroll = null;

            function seconds() {
                var n = parseInt(secondsInput.value, 10);
                return n >= 1 && n <= 3600 ? n : 0;
            }

            function clock(d) {
                return d ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—';
            }

            function show() {
                var n = seconds();
                var parts = [];
                if (failed) parts.push('Stopped: the last run failed. Run now to try again.');
                else if (paused && n) parts.push('Paused');
                else if (n) parts.push('Every ' + n + ' s');
                else parts.push('Not refreshing');
                if (lastRun) parts.push('last run ' + clock(lastRun));
                if (lastText) parts.push(lastText);
                if (inFlight) parts.push('running…');
                else if (n && !paused && !failed && ticker)
                    parts.push('next in ' + Math.max(0, Math.ceil((nextAt - Date.now()) / 1000)) + ' s');
                status.textContent = parts.join(' · ');
                // The title line of a collapsed panel keeps the essentials.
                if (info) {
                    var bits = [];
                    if (failed) bits.push('stopped');
                    else if (paused && n) bits.push('paused');
                    else if (n) bits.push('every ' + n + ' s');
                    if (lastDelta) bits.push(lastDelta);
                    var filtered = out.querySelector('.filter-status span');
                    if (filtered) bits.push(filtered.textContent);
                    info.textContent = bits.join(' · ');
                }
                el.classList.toggle('failed', failed);
                el.classList.toggle('paused', !failed && paused && n > 0);
                el.classList.toggle('live', !failed && !paused && n > 0);
            }

            function stopTimer() {
                clearInterval(ticker);
                ticker = null;
            }

            function run() {
                stopTimer();
                inFlight = true;
                htmx.trigger(form, 'submit');
                show();
            }

            function schedule() {
                stopTimer();
                if (!seconds() || paused || failed) { show(); return; }
                nextAt = Date.now() + seconds() * 1000;
                ticker = setInterval(function () {
                    if (Date.now() < nextAt) { show(); return; }
                    // Due, but nobody is looking: wait until the tab is shown.
                    if (document.hidden || paused) return;
                    run();
                }, 1000);
                show();
            }

            function snapshot(root) {
                var table = root.querySelector('table');
                if (!table) return null;
                var head = Array.prototype.map.call(table.querySelectorAll('thead th'),
                    function (th) { return th.textContent; }).join('\u0001');
                var rows = Array.prototype.map.call(table.querySelectorAll('tbody tr'), function (tr) {
                    return Array.prototype.map.call(tr.children, function (td) { return td.textContent; });
                });
                return { head: head, rows: rows };
            }

            // Flash what changed since the previous result (same columns only);
            // returns the change in the row count, or 0.
            function markChanges(now) {
                var delta = 0;
                if (previous && now && previous.head === now.head) {
                    delta = now.rows.length - previous.rows.length;
                    var trs = out.querySelectorAll('tbody tr');
                    now.rows.forEach(function (row, r) {
                        var old = previous.rows[r];
                        row.forEach(function (text, c) {
                            if (!old || old[c] !== text) trs[r].children[c].classList.add('changed');
                        });
                    });
                }
                previous = now;
                return delta;
            }

            function answered() {
                inFlight = false;
                lastRun = new Date();
                var meta = out.querySelector('.panel-meta');
                failed = !!out.querySelector('.error');
                var delta = 0;
                if (failed) {
                    previous = null;
                    lastText = '';
                    lastDelta = '';
                } else {
                    applyStoredSort(out);
                    delta = markChanges(snapshot(out));
                    lastDelta = delta ? (delta > 0 ? '+' : '') + delta + (Math.abs(delta) === 1 ? ' row' : ' rows') : '';
                    if (meta) {
                        var n = parseInt(meta.dataset.rows, 10);
                        lastText = n + (n === 1 ? ' row' : ' rows') +
                            (delta ? ' (' + (delta > 0 ? '+' : '') + delta + ')' : '') +
                            ' · ' + meta.dataset.ms + ' ms' +
                            (meta.dataset.more === '1' ? ' · more rows exist' : '');
                    }
                }
                if (dot) {
                    dot.classList.remove('tick');
                    void dot.offsetWidth;      // restart the animation
                    dot.classList.add('tick');
                }
                schedule();
            }

            function broke() {
                inFlight = false;
                failed = true;
                lastRun = new Date();
                schedule();
            }

            document.body.addEventListener('htmx:beforeRequest', function (e) {
                if (e.detail && e.detail.elt === form) inFlight = true;
            });
            document.body.addEventListener('htmx:beforeSwap', function (e) {
                if (e.target !== out) return;
                var table = out.querySelector('.results');
                scroll = table ? { top: table.scrollTop, left: table.scrollLeft } : null;
            });
            document.body.addEventListener('htmx:afterSwap', function (e) {
                if (e.target !== out) return;
                var table = out.querySelector('.results');
                if (table && scroll) {
                    table.scrollTop = scroll.top;
                    table.scrollLeft = scroll.left;
                }
                answered();
            });
            ['htmx:responseError', 'htmx:sendError', 'htmx:timeout'].forEach(function (name) {
                document.body.addEventListener(name, function (e) {
                    if (e.detail && e.detail.elt === form) broke();
                });
            });

            // The user sorted the table: what is on screen is the reference now.
            out.addEventListener('resultsorted', function () { previous = snapshot(out); });

            // A new interval applies from now.
            secondsInput.addEventListener('change', function () { if (!inFlight) schedule(); });
            // A filter was applied or cleared: the collapsed title line says so.
            out.addEventListener('resultsorted', show);

            if (gear) gear.addEventListener('click', function () {
                form.hidden = !form.hidden;
                gear.setAttribute('aria-expanded', form.hidden ? 'false' : 'true');
            });

            // Run now (the submit button) retries a failed panel.
            form.addEventListener('submit', function () { failed = false; });

            return {
                run: function () { failed = false; run(); },
                schedule: schedule,
                refresh: show,
            };
        }

        grid.querySelectorAll('.panel').forEach(function (el) {
            var p = makePanel(el);
            if (p) panels.push(p);
        });

        function runAll() {
            panels.forEach(function (p, i) {
                // Staggered: a few hundred ms apart, not all at the same instant.
                setTimeout(p.run, i * 250);
            });
        }

        function showPause() {
            if (pauseButton) pauseButton.textContent = paused ? 'Resume all' : 'Pause all';
            panels.forEach(function (p) { p.refresh(); });
        }

        if (pauseButton) pauseButton.addEventListener('click', function () {
            paused = !paused;
            if (paused) {
                panels.forEach(function (p) { p.schedule(); });
            } else {
                runAll();
            }
            showPause();
        });

        var runAllButton = document.querySelector('[data-workplace-run-all]');
        if (runAllButton) runAllButton.addEventListener('click', runAll);
        // The shared values were saved: run everything with them.
        document.body.addEventListener('runAll', runAll);

        // First runs once the page, and htmx with it, is fully set up.
        if (document.readyState === 'complete') runAll();
        else window.addEventListener('load', runAll);
    }

    document.querySelectorAll('[data-workplace]').forEach(initWorkplace);
})();
