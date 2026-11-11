// Dynamo client script: theme, dialog, toast and drag and drop. Everything
// else is htmx attributes in the pages.
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
})();
