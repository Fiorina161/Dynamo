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

    // ---- Auto-refresh of a query -----------------------------------------------
    // A box marked data-auto-refresh="#form" re-submits that htmx form every N
    // seconds until stopped. The next run is scheduled when the previous one has
    // finished (a slow query never overlaps itself), and a failed run stops it, so
    // a wrong password is not retried again and again.
    function initAutoRefresh(box) {
        var form = document.querySelector(box.dataset.autoRefresh);
        var input = box.querySelector('[data-refresh-seconds]');
        var toggle = box.querySelector('[data-refresh-toggle]');
        var status = box.querySelector('[data-refresh-status]');
        if (!form || !input || !toggle || !status) return;

        var key = 'refresh-seconds:' + location.pathname;
        var seconds = 0;          // 0 = not running
        var ticker = null;
        var nextAt = 0;
        var lastRun = null;
        var inFlight = false;

        try {
            var saved = localStorage.getItem(key);
            if (saved) input.value = saved;
        } catch (e) { }

        function running() { return seconds > 0; }

        function clock(d) {
            return d ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—';
        }

        function show() {
            var left = Math.max(0, Math.ceil((nextAt - Date.now()) / 1000));
            status.textContent = 'Every ' + seconds + ' s · last run ' + clock(lastRun) + ' · ' +
                (inFlight ? 'running…' : 'next in ' + left + ' s');
        }

        function stop(message) {
            seconds = 0;
            clearInterval(ticker);
            ticker = null;
            box.classList.remove('on');
            toggle.textContent = 'Start auto-refresh';
            status.textContent = message || '';
        }

        function run() {
            inFlight = true;
            htmx.trigger(form, 'submit');
            show();
        }

        function scheduleNext() {
            nextAt = Date.now() + seconds * 1000;
            clearInterval(ticker);
            ticker = setInterval(function () {
                if (!running()) return;
                if (Date.now() >= nextAt) {
                    clearInterval(ticker);
                    run();
                } else {
                    show();
                }
            }, 1000);
            show();
        }

        function secondsOf(text) {
            var n = parseInt(text, 10);
            return n >= 1 && n <= 3600 ? n : 0;
        }

        toggle.addEventListener('click', function () {
            if (running()) {
                stop('Stopped.');
                return;
            }
            var n = secondsOf(input.value);
            if (!n) {
                status.textContent = 'Enter a number of seconds, from 1 to 3600.';
                input.focus();
                return;
            }
            seconds = n;
            try { localStorage.setItem(key, String(n)); } catch (e) { }
            box.classList.add('on');
            toggle.textContent = 'Stop auto-refresh';
            run();
        });

        // A new interval while running applies from now.
        input.addEventListener('change', function () {
            var n = secondsOf(input.value);
            if (!running() || !n) return;
            seconds = n;
            try { localStorage.setItem(key, String(n)); } catch (e) { }
            if (!inFlight) scheduleNext();
        });

        document.body.addEventListener('htmx:beforeRequest', function (e) {
            if (e.detail && e.detail.elt === form) inFlight = true;
        });

        // The answer is in the page: schedule the next run, or stop if it failed.
        document.body.addEventListener('htmx:afterSwap', function (e) {
            if (!e.target || e.target.id !== 'results') return;
            inFlight = false;
            lastRun = new Date();
            if (!running()) return;
            if (e.target.querySelector('.error')) stop('Stopped: the last run failed.');
            else scheduleNext();
        });

        ['htmx:responseError', 'htmx:sendError', 'htmx:timeout'].forEach(function (name) {
            document.body.addEventListener(name, function (e) {
                if (!e.detail || e.detail.elt !== form) return;
                inFlight = false;
                if (running()) stop('Stopped: the server did not answer.');
            });
        });
    }

    document.querySelectorAll('[data-auto-refresh]').forEach(initAutoRefresh);

    // A refresh replaces the results: keep the table where the user had scrolled it.
    var savedScroll = null;
    document.body.addEventListener('htmx:beforeSwap', function (e) {
        if (!e.target || e.target.id !== 'results') return;
        var table = e.target.querySelector('.results');
        savedScroll = table ? { top: table.scrollTop, left: table.scrollLeft } : null;
    });
    document.body.addEventListener('htmx:afterSwap', function (e) {
        if (!e.target || e.target.id !== 'results' || !savedScroll) return;
        var table = e.target.querySelector('.results');
        if (table) {
            table.scrollTop = savedScroll.top;
            table.scrollLeft = savedScroll.left;
        }
        savedScroll = null;
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
    // container (#results, a panel's .panel-results), which survives
    // refreshes: it is applied again to every new result with the same column.
    var SORT_ROOT = '#results, .panel-results';

    function sortTable(root, table, col, dir) {
        var body = table.tBodies[0];
        var head = table.tHead && table.tHead.rows[0];
        if (!body || !head) return;
        var rows = Array.prototype.slice.call(body.rows);
        rows.forEach(function (tr, i) { if (tr.dataset.row === undefined) tr.dataset.row = i; });
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
        rows.forEach(function (tr) { body.appendChild(tr); });
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
    function applyStoredSort(root) {
        if (!root || root.dataset.sortCol === undefined) return;
        var table = root.querySelector('.results table');
        if (!table || !table.tHead) return;
        var col = parseInt(root.dataset.sortCol, 10);
        var th = table.tHead.rows[0].cells[col];
        if (!th || th.textContent.trim() !== root.dataset.sortName) { clearSort(root); return; }
        sortTable(root, table, col, root.dataset.sortDir);
    }

    function toggleSort(th, restore) {
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
        var th = e.target.closest && e.target.closest('.results th');
        if (th) toggleSort(th, e.shiftKey);
    });
    document.addEventListener('keydown', function (e) {
        var th = e.target.closest && e.target.closest('.results th');
        if (!th) return;
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleSort(th, e.shiftKey); }
        else if (e.key === 'Escape') toggleSort(th, true);
    });
    // The query page's results are replaced on every run or refresh.
    document.body.addEventListener('htmx:afterSwap', function (e) {
        if (e.target && e.target.id === 'results') applyStoredSort(e.target);
    });

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
            var countBadge = el.querySelector('[data-panel-count]');
            var gear = el.querySelector('[data-panel-toggle]');
            if (!form || !out || !secondsInput || !status) return null;

            var ticker = null;
            var nextAt = 0;
            var inFlight = false;
            var failed = false;
            var lastRun = null;
            var lastText = '';
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
                    if (countBadge) countBadge.textContent = '';
                } else {
                    applyStoredSort(out);
                    delta = markChanges(snapshot(out));
                    if (meta) {
                        var n = parseInt(meta.dataset.rows, 10);
                        // In the title, so it shows even with the details folded.
                        if (countBadge)
                            countBadge.textContent = n + (meta.dataset.more === '1' ? '+' : '') +
                                (n === 1 && meta.dataset.more !== '1' ? ' row' : ' rows');
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
