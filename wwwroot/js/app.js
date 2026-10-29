// Uncovering Greatness CRM — front-end behaviour.
// Strict CSP (script-src 'self'): no inline scripts or handlers anywhere; everything is wired up here via data-* attributes.
document.addEventListener('DOMContentLoaded', function () {
  var csrf = (document.querySelector('meta[name="csrf-token"]') || {}).content || '';

  function post(url, body) {
    var headers = { 'RequestVerificationToken': csrf, 'X-Requested-With': 'XMLHttpRequest' };
    var opts = { method: 'POST', headers: headers, credentials: 'same-origin' };
    // Marking a notification read happens as the browser follows its link; keepalive lets the request finish after the page changes.
    if (url.indexOf('/Notifications/MarkRead') === 0) opts.keepalive = true;
    if (body !== undefined) { headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    return fetch(url, opts).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (j) { j.status = r.status; j.httpOk = r.ok; return j; });
    });
  }
  function toast(msg, kind) {
    var host = document.querySelector('.content'); if (!host) return;
    var el = document.createElement('div');
    el.className = 'flash ' + (kind || 'error');
    el.setAttribute('role', (kind || 'error') === 'error' ? 'alert' : 'status');
    el.innerHTML = '<i class="bi bi-info-circle"></i>';
    el.appendChild(document.createTextNode(' ' + msg));
    host.insertBefore(el, host.firstChild);
    setTimeout(function () { el.remove(); }, 5000);
  }

  // Decorative icons are hidden from screen readers (icon-only controls carry an aria-label instead)
  document.querySelectorAll('i.bi').forEach(function (i) { i.setAttribute('aria-hidden', 'true'); });
  document.querySelectorAll('.nav-item.active').forEach(function (a) { a.setAttribute('aria-current', 'page'); });

  // Mobile sidebar
  var toggle = document.querySelector('.menu-toggle'), sidebar = document.querySelector('.sidebar'), scrim = document.querySelector('.scrim');
  function closeSidebar() { sidebar && sidebar.classList.remove('open'); scrim && scrim.classList.remove('show'); toggle && toggle.setAttribute('aria-expanded', 'false'); }
  if (toggle && sidebar) toggle.addEventListener('click', function () { var open = sidebar.classList.toggle('open'); scrim && scrim.classList.toggle('show', open); toggle.setAttribute('aria-expanded', open ? 'true' : 'false'); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeSidebar(); });
  scrim && scrim.addEventListener('click', closeSidebar);

  // Tabs (client-side panels)
  document.querySelectorAll('.tabs[data-tabs]').forEach(function (group) {
    var panels = document.querySelectorAll('#' + group.getAttribute('data-tabs') + ' > .tab-panel');
    group.querySelectorAll('.tab').forEach(function (tab, i) {
      tab.addEventListener('click', function () {
        group.querySelectorAll('.tab').forEach(function (t) { t.classList.remove('active'); t.setAttribute('aria-selected', 'false'); });
        tab.classList.add('active'); tab.setAttribute('aria-selected', 'true');
        panels.forEach(function (p) { p.classList.remove('active'); });
        if (panels[i]) panels[i].classList.add('active');
      });
    });
  });
  if (location.hash === '#notes') { var nt = document.querySelector('.tab[data-tab-name="notes"]'); nt && nt.click(); }

  // Dropdowns
  document.querySelectorAll('[data-dropdown]').forEach(function (btn) {
    var menu = document.getElementById(btn.getAttribute('data-dropdown')); if (!menu) return;
    btn.addEventListener('click', function (e) {
      e.stopPropagation();
      document.querySelectorAll('.dropdown-panel.show').forEach(function (m) { if (m !== menu) m.classList.remove('show'); });
      var open = menu.classList.toggle('show');
      document.querySelectorAll('[data-dropdown]').forEach(function (b) { b.setAttribute('aria-expanded', b === btn && open ? 'true' : 'false'); });
    });
  });
  function closeDropdowns(returnFocus) {
    var owner = null;
    if (returnFocus && document.activeElement) { var dd = document.activeElement.closest('.dropdown'); owner = dd && dd.querySelector('[data-dropdown]'); }
    document.querySelectorAll('.dropdown-panel.show').forEach(function (m) { m.classList.remove('show'); });
    document.querySelectorAll('[data-dropdown]').forEach(function (b) { b.setAttribute('aria-expanded', 'false'); });
    if (owner) owner.focus();
  }
  document.addEventListener('click', function (e) {
    if (e.target.closest('.dropdown-panel')) return;
    closeDropdowns(false);
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeDropdowns(true); });

  // Clickable table rows (replaces inline onclick, which the CSP forbids)
  document.querySelectorAll('tr[data-href]').forEach(function (row) {
    row.setAttribute('tabindex', '0');
    row.setAttribute('role', 'link');
    row.addEventListener('click', function (e) { if (!e.target.closest('a,button,input,select,form,label')) location.href = row.getAttribute('data-href'); });
    row.addEventListener('keydown', function (e) { if ((e.key === 'Enter' || e.key === ' ') && e.target === row) { e.preventDefault(); location.href = row.getAttribute('data-href'); } });
  });

  // Auto-submit filters
  document.querySelectorAll('select[data-autosubmit]').forEach(function (s) { s.addEventListener('change', function () { s.form.submit(); }); });

  // Confirm dialogs on destructive forms
  document.querySelectorAll('form[data-confirm]').forEach(function (f) {
    f.addEventListener('submit', function (e) { if (!window.confirm(f.getAttribute('data-confirm'))) e.preventDefault(); });
  });

  // Prevent double submits
  document.querySelectorAll('form').forEach(function (f) {
    f.addEventListener('submit', function (e) {
      if (e.defaultPrevented) return;
      f.querySelectorAll('button[type=submit]:not([data-keep])').forEach(function (b) { setTimeout(function () { b.disabled = true; b.classList.add('is-loading'); b.setAttribute('aria-busy', 'true'); }, 0); });
    });
  });

  // Star toggle
  document.querySelectorAll('[data-star]').forEach(function (el) {
    el.addEventListener('click', function (e) {
      e.stopPropagation(); e.preventDefault();
      post('/Leads/ToggleStar/' + el.getAttribute('data-star')).then(function (r) {
        if (r.ok) el.classList.toggle('is-on', !!(r.data && r.data.starred)); else toast(r.error || 'Could not update star.');
      });
    });
  });

  // Task completion checkbox
  document.querySelectorAll('input[data-task]').forEach(function (cb) {
    cb.addEventListener('change', function () {
      var wanted = cb.checked;
      post('/Tasks/Toggle/' + cb.getAttribute('data-task') + '?completed=' + wanted).then(function (r) {
        if (r.ok) { var row = cb.closest('[data-task-row]'); row && row.classList.toggle('is-done', wanted); setTimeout(function () { location.reload(); }, 350); }
        else { cb.checked = !wanted; toast(r.error || 'Could not update task.'); }
      });
    });
  });

  // Notifications: mark as read
  document.querySelectorAll('[data-notif]').forEach(function (el) {
    el.addEventListener('click', function () { navigator.sendBeacon && post('/Notifications/MarkRead/' + el.getAttribute('data-notif')); });
  });
  document.querySelectorAll('[data-mark-all]').forEach(function (a) {
    a.addEventListener('click', function (e) {
      e.preventDefault();
      post('/Notifications/MarkAllRead').then(function () {
        document.querySelectorAll('.notif-dot,.dropdown-item.unread,.notif-row.unread').forEach(function (n) { n.classList.remove('unread'); if (n.classList.contains('notif-dot')) n.remove(); });
      });
    });
  });

  // Live bell: ask the server once a minute, update the badge and pop a message when something new arrives
  var bellBtn = document.querySelector('[data-dropdown="notifPanel"]'), lastUnread = null;
  function setBadge(n) {
    if (!bellBtn) return;
    var dot = bellBtn.querySelector('.notif-dot');
    if (n > 0 && !dot) { dot = document.createElement('span'); dot.className = 'dot notif-dot'; bellBtn.appendChild(dot); }
    if (n === 0 && dot) dot.remove();
    bellBtn.setAttribute('aria-label', 'Notifications' + (n > 0 ? ', ' + n + ' unread' : ''));
  }
  function pollNotifications() {
    if (document.hidden) return;
    fetch('/Notifications/Poll', { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j) return;
        setBadge(j.unread);
        if (lastUnread !== null && j.unread > lastUnread && j.latest && j.latest.length) toast(j.latest[0].title + (j.unread > lastUnread + 1 ? ' (and ' + (j.unread - lastUnread - 1) + ' more)' : ''), 'success');
        lastUnread = j.unread;
      }).catch(function () { /* offline or signed out: try again next minute */ });
  }
  if (bellBtn) { pollNotifications(); setInterval(pollNotifications, 60000); }

  // Pipeline drag & drop -> moves stage via AJAX
  var dragged = null;
  document.querySelectorAll('.deal-card[draggable=true]').forEach(function (card) {
    card.addEventListener('dragstart', function (e) { dragged = card; card.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; try { e.dataTransfer.setData('text/plain', card.getAttribute('data-lead')); } catch (x) {} });
    card.addEventListener('dragend', function () { card.classList.remove('dragging'); document.querySelectorAll('.drop-target').forEach(function (d) { d.classList.remove('drop-target'); }); });
  });
  document.querySelectorAll('.pipeline-cards[data-stage]').forEach(function (col) {
    col.addEventListener('dragover', function (e) { if (dragged) { e.preventDefault(); col.classList.add('drop-target'); } });
    col.addEventListener('dragleave', function () { col.classList.remove('drop-target'); });
    col.addEventListener('drop', function (e) {
      e.preventDefault(); col.classList.remove('drop-target');
      if (!dragged) return;
      var card = dragged, from = card.parentElement; dragged = null;
      if (from === col) return;
      col.insertBefore(card, col.firstChild);
      post('/Leads/MoveStage/' + card.getAttribute('data-lead'), { stage: parseInt(col.getAttribute('data-stage'), 10) }).then(function (r) {
        if (r.ok) location.reload(); else { from.insertBefore(card, from.firstChild); toast(r.error || 'Could not move lead.'); }
      });
    });
  });

  // Calendar: visibility picker on create/edit
  function syncVisibility() {
    var checked = document.querySelector('input[name="Visibility"]:checked');
    var shareList = document.getElementById('shareWithList');
    document.querySelectorAll('.visibility-option').forEach(function (o) { o.classList.remove('is-selected'); });
    if (checked) { var o = checked.closest('.visibility-option'); o && o.classList.add('is-selected'); }
    if (shareList) shareList.classList.toggle('show', !!checked && checked.value === 'Shared');
  }
  document.querySelectorAll('input[name="Visibility"]').forEach(function (r) { r.addEventListener('change', syncVisibility); });
  syncVisibility();

  // Calendar: linked-record picker (type -> id list)
  var linkedType = document.getElementById('LinkedType');
  if (linkedType) {
    var sync = function () {
      document.querySelectorAll('[data-linked-for]').forEach(function (el) {
        var on = el.getAttribute('data-linked-for') === linkedType.value;
        el.classList.toggle('is-hidden', !on);
        el.querySelectorAll('select').forEach(function (s) { s.disabled = !on; });
      });
    };
    linkedType.addEventListener('change', sync); sync();
  }

  // Campaign form: audience selector + live recipient count + preview
  var target = document.getElementById('Target');
  if (target) {
    var countEl = document.getElementById('audienceCount');
    var refresh = function () {
      document.querySelectorAll('[data-target-for]').forEach(function (el) {
        var on = el.getAttribute('data-target-for') === target.value;
        el.classList.toggle('is-hidden', !on);
        el.querySelectorAll('select').forEach(function (s) { s.disabled = !on; });
      });
      var year = document.getElementById('TargetYear'), ev = document.getElementById('TargetEventId');
      var qs = 'target=' + encodeURIComponent(target.value) + (year && !year.disabled && year.value ? '&year=' + year.value : '') + (ev && !ev.disabled && ev.value ? '&eventId=' + ev.value : '');
      fetch('/Campaigns/Audience?' + qs, { credentials: 'same-origin' }).then(function (r) { return r.json(); })
        .then(function (j) { if (countEl) countEl.textContent = j.count; }).catch(function () {});
    };
    target.addEventListener('change', refresh);
    ['TargetYear', 'TargetEventId'].forEach(function (id) { var e = document.getElementById(id); e && e.addEventListener('change', refresh); });
    refresh();
  }

  // Password show/hide
  document.querySelectorAll('[data-toggle-password]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var input = document.getElementById(btn.getAttribute('data-toggle-password')); if (!input) return;
      input.type = input.type === 'password' ? 'text' : 'password';
      var shown = input.type === 'text';
      btn.setAttribute('aria-pressed', shown ? 'true' : 'false');
      btn.setAttribute('aria-label', shown ? 'Hide password' : 'Show password');
      btn.textContent = shown ? 'Hide' : 'Show';
    });
  });

  // Copy-to-clipboard helper
  document.querySelectorAll('[data-copy]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var input = document.getElementById(btn.getAttribute('data-copy')); if (!input) return;
      var label = btn.textContent; navigator.clipboard && navigator.clipboard.writeText(input.value).then(function () { btn.textContent = 'Copied'; setTimeout(function () { btn.textContent = label; }, 1800); });
    });
  });
});
