/* HCP · Sala de reuniones — cliente de la tablet.
   Vanilla JS, sin dependencias. Toda la lógica horaria usa la HORA DE LA SALA que envía el
   servidor (no la zona ni el reloj de la tablet). */
(() => {
  'use strict';

  const CFG = Object.assign(
    { base: '/', min: 15, max: 240, slot: 15, workStart: 420, workEnd: 1260, maxDays: 90, refreshMs: 30000, idleMs: 90000 },
    window.ROOM_CFG || {});
  const $ = id => document.getElementById(id);
  const pad = n => String(n).padStart(2, '0');
  const hhmm = min => pad(Math.floor(min / 60) % 24) + ':' + pad(min % 60);
  const durLabel = m => m < 60 ? m + ' min' : (m % 60 ? Math.floor(m / 60) + ' h ' + (m % 60) : (m / 60) + ' h');
  const api = p => CFG.base + p;

  /* ---------------- Reloj (hora de la sala, anclado al servidor) ---------------- */
  const clock = { nowMs: null, offsetMin: 0, at: 0 };
  const localMs = () => clock.nowMs == null
    ? Date.now() - new Date().getTimezoneOffset() * 60000
    : clock.nowMs + (performance.now() - clock.at) + clock.offsetMin * 60000;
  function nowParts() {
    const d = new Date(localMs());
    return { y: d.getUTCFullYear(), m: d.getUTCMonth() + 1, d: d.getUTCDate(), hh: d.getUTCHours(), mm: d.getUTCMinutes() };
  }
  const nowMin = () => { const p = nowParts(); return p.hh * 60 + p.mm; };

  /* ---------------- Fechas (cadenas yyyy-MM-dd, cálculo en UTC para evitar DST) ---------------- */
  const ymd = (y, m, d) => `${y}-${pad(m)}-${pad(d)}`;
  const toDate = s => { const [y, m, d] = s.split('-').map(Number); return new Date(Date.UTC(y, m - 1, d)); };
  const fromDate = dt => ymd(dt.getUTCFullYear(), dt.getUTCMonth() + 1, dt.getUTCDate());
  const addDays = (s, n) => { const dt = toDate(s); dt.setUTCDate(dt.getUTCDate() + n); return fromDate(dt); };
  const fmt = (s, o) => new Intl.DateTimeFormat('es-ES', Object.assign({ timeZone: 'UTC' }, o)).format(toDate(s));
  const cap = t => t.charAt(0).toUpperCase() + t.slice(1);
  const longDate = s => cap(fmt(s, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }));
  const midDate = s => cap(fmt(s, { weekday: 'long', day: 'numeric', month: 'long' }));
  const shortDate = s => fmt(s, { weekday: 'short', day: 'numeric', month: 'short' }).replace(/[.,]/g, '');
  const todayStr = () => { const p = nowParts(); return ymd(p.y, p.m, p.d); };
  const maxDateStr = () => addDays(todayStr(), CFG.maxDays);

  /* ---------------- Estado ---------------- */
  let status = null;       // última respuesta de /api/status
  const meetById = {};     // reuniones mostradas (para el detalle al pulsar)
  let selected = null;     // día consultado (null = hoy)
  let version = null;
  let refreshing = false;
  const viewDate = () => {
    const t = todayStr();
    if (!selected || selected < t) selected = null;
    return selected || t;
  };

  /* ---------------- Utilidades de UI ---------------- */
  function setText(id, t) { $(id).textContent = t; }
  function show(id, on) { $(id).classList.toggle('d-none', !on); }
  function showError(msg) { setText('errorBar', msg || ''); show('errorBar', !!msg); }
  const icon = id => `<svg aria-hidden="true"><use href="#${id}"/></svg>`;

  function openOv(el) { el.classList.add('open'); el.setAttribute('aria-hidden', 'false'); }
  function closeOv(el) { el.classList.remove('open'); el.setAttribute('aria-hidden', 'true'); }
  const isOpen = el => el.classList.contains('open');
  /* Reinicia una animación CSS añadiendo la clase de nuevo */
  function replay(el, cls, remove = []) {
    el.classList.remove(cls, ...remove); void el.offsetWidth; el.classList.add(cls);
  }
  let lastState = null, lastRenderedDate = null;

  /* =========================================================
     Panel principal y agenda
     ========================================================= */
  async function refresh() {
    if (refreshing) return;
    refreshing = true;
    const wanted = viewDate();
    try {
      const r = await fetch(`${api('api/status')}?date=${wanted}`, { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const s = await r.json();
      if (version && s.version && s.version !== version) { location.reload(); return; } // el servidor se ha actualizado
      version = s.version;
      clock.nowMs = s.nowMs; clock.offsetMin = s.utcOffsetMinutes; clock.at = performance.now();
      if (s.date !== viewDate()) { refreshing = false; return refresh(); }  // el usuario cambió de día mientras cargaba
      status = s;
      render();
    } catch (e) {
      showError('Sin conexión con el servidor. Reintentando…');
    } finally {
      refreshing = false;
    }
  }

  function render() {
    const s = status;
    if (!s) return;
    const v = viewDate(), today = todayStr(), isToday = v === today;
    const busy = s.isBusy;

    $('app').classList.remove('is-loading');
    $('app').classList.toggle('is-busy', busy);
    $('app').classList.toggle('is-free', !busy);
    setText('stateLabel', busy ? 'Ocupada' : 'Libre');
    if (lastState !== null && lastState !== busy) { replay($('stateLabel'), 'swap'); replay($('stateSub'), 'swap'); }
    lastState = busy;
    setText('roomName', s.roomName || '');
    showError(s.error || '');

    // Subtítulo
    let sub;
    if (busy) sub = s.current.isAllDay ? 'Ocupada todo el día' : `Libre a partir de las ${s.current.endLocal}`;
    else sub = s.freeUntil ? `Libre hasta las ${s.freeUntil}` : 'Libre el resto del día';
    setText('stateSub', sub);

    // Reunión actual / siguiente
    const f = busy ? s.current : s.next;
    show('focusCard', !!f);
    $('focusCard').dataset.id = f ? f.id : '';
    if (f) {
      meetById[f.id] = f;
      setText('focusLabel', busy ? 'Reunión actual' : 'Siguiente reunión');
      setText('focusTitle', f.subject);
      setText('focusTime', f.isAllDay ? 'Todo el día' : `${f.startLocal} – ${f.endLocal}`);
      setText('focusOrg', f.organizer ? 'Organiza: ' + f.organizer : '');
    }

    // Navegación de día
    setText('dayShort', isToday ? 'Hoy' : (v === addDays(today, 1) ? 'Mañana' : cap(shortDate(v))));
    setText('dayLong', midDate(v));
    $('btnPrev').disabled = v <= today;
    $('btnNext').disabled = v >= maxDateStr();
    show('btnToday', !isToday);
    show('viewing', !isToday);
    if (!isToday) setText('viewingText', 'Viendo ' + shortDate(v));
    setText('bookLabel', isToday ? 'Reservar sala' : 'Reservar el ' + shortDate(v));

    // Agenda del día mostrado
    const nm = nowMin();
    let items = s.meetings || [];
    if (isToday) items = items.filter(m => m.endMin > nm && !(s.current && m.id === s.current.id));
    setText('agendaTitle', isToday ? 'Próximas reuniones' : 'Reuniones del día');
    setText('agendaEmpty', isToday ? 'No hay más reuniones hoy' : 'Sin reuniones este día');
    show('agendaEmpty', items.length === 0);

    const ul = $('agenda');
    if (lastRenderedDate && s.date !== lastRenderedDate) {
      const cls = s.date > lastRenderedDate ? 'go-next' : 'go-prev', other = cls === 'go-next' ? 'go-prev' : 'go-next';
      replay(ul, cls, [other]); replay(document.querySelector('.daylabel'), cls, [other]);
    }
    lastRenderedDate = s.date;
    ul.textContent = '';
    items.forEach((m, i) => {
      const li = document.createElement('li');
      meetById[m.id] = m; li.dataset.id = m.id; li.tabIndex = 0; li.setAttribute('role', 'button');
      if (isToday && i === 0) li.className = 'is-next';
      const t = document.createElement('div');
      const a = document.createElement('span'); a.className = 'a-time';
      a.textContent = m.isAllDay ? 'Todo el día' : m.startLocal;
      t.append(a);
      if (!m.isAllDay) { const e = document.createElement('span'); e.className = 'a-end'; e.textContent = '→ ' + m.endLocal; t.append(e); }
      const d = document.createElement('div');
      const ti = document.createElement('span'); ti.className = 'a-title'; ti.textContent = m.subject; d.append(ti);
      if (m.organizer) { const o = document.createElement('span'); o.className = 'a-org'; o.textContent = m.organizer; d.append(o); }
      li.append(t, d);
      ul.append(li);
    });
    const p = nowParts();
    setText('updated', `${pad(p.hh)}:${pad(p.mm)}`);
  }

  /* Detalle de la reunión al pulsarla (reunión actual, siguiente o cualquiera de la agenda) */
  function openMeeting(id) {
    const m = meetById[id]; if (!m || !status) return;
    const d = status.date, cur = status.current && status.current.id === m.id;
    const dur = m.endMin - m.startMin, h = Math.floor(dur / 60), mi = dur % 60;
    const badge = $('mtBadge'); badge.classList.toggle('is-now', !!cur);
    badge.textContent = cur ? 'En curso' : (status.isToday && m.endMin <= nowMin() ? 'Finalizada' : 'Programada');
    setText('mtTitle', m.subject);
    setText('mtDate', longDate(d));
    setText('mtTime', m.isAllDay ? 'Todo el día' : `${m.startLocal} – ${m.endLocal}`);
    setText('mtDur', m.isAllDay ? '—' : (h ? h + ' h' : '') + (h && mi ? ' ' : '') + (mi ? mi + ' min' : ''));
    setText('mtOrg', m.organizer || '—');
    setText('mtRoom', status.roomName || '—');
    setText('mtTeams', m.isTeams ? 'Con enlace de Teams' : 'Sin Teams');
    openOv($('meetOverlay'));
  }
  const closeMeeting = () => closeOv($('meetOverlay'));
  const pickMeeting = e => { const el = e.target.closest('[data-id]'); if (el && el.dataset.id) openMeeting(el.dataset.id); };
  $('focusCard').setAttribute('role', 'button'); $('focusCard').tabIndex = 0;
  $('focusCard').addEventListener('click', pickMeeting);
  $('agenda').addEventListener('click', pickMeeting);
  [$('focusCard'), $('agenda')].forEach(el => el.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pickMeeting(e); } }));
  $('mtClose').onclick = $('mtOk').onclick = closeMeeting;
  $('meetOverlay').querySelector('.scrim').onclick = closeMeeting;

  /* Reloj: se repinta cada segundo; cada minuto se reevalúa la agenda */
  let lastMin = -1;
  function tick() {
    const p = nowParts();
    setText('time', `${pad(p.hh)}:${pad(p.mm)}`);
    setText('date', longDate(todayStr()));
    if (p.mm !== lastMin) {
      lastMin = p.mm;
      if (status) {
        render();
        const nm = p.hh * 60 + p.mm;
        const crossed = (status.current && nm >= status.current.endMin) || (status.next && nm >= status.next.startMin);
        if (crossed) refresh();
      }
      if (p.hh === 3 && p.mm === 0) location.reload(); // recarga diaria de mantenimiento
    }
  }

  function goDay(date) { selected = date; refresh(); render(); bump(); }
  $('btnPrev').onclick = () => goDay(addDays(viewDate(), -1));
  $('btnNext').onclick = () => goDay(addDays(viewDate(), 1));
  $('btnToday').onclick = $('btnBackToday').onclick = () => goDay(todayStr());
  $('btnCal').onclick = () => openCalendar(viewDate(), d => goDay(d));

  /* =========================================================
     Calendario (selector de día)
     ========================================================= */
  let cal = null; // { value, month, onPick }
  const WEEK = ['lun', 'mar', 'mié', 'jue', 'vie', 'sáb', 'dom'];
  $('calWeek').innerHTML = WEEK.map(w => `<span>${w}</span>`).join('');

  function openCalendar(value, onPick) {
    cal = { value, month: value.slice(0, 7) + '-01', onPick };
    renderCal();
    openOv($('calOverlay'));
  }
  function closeCal() { closeOv($('calOverlay')); cal = null; }

  function renderCal() {
    const today = todayStr(), max = maxDateStr();
    const first = toDate(cal.month);
    setText('calTitle', cap(fmt(cal.month, { month: 'long', year: 'numeric' })));
    const lead = (first.getUTCDay() + 6) % 7; // lunes primero
    const days = new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 1, 0)).getUTCDate();
    const g = $('calGrid');
    g.textContent = '';
    for (let i = 0; i < lead; i++) { const b = document.createElement('button'); b.className = 'blank'; b.disabled = true; b.tabIndex = -1; g.append(b); }
    for (let d = 1; d <= days; d++) {
      const s = ymd(first.getUTCFullYear(), first.getUTCMonth() + 1, d);
      const b = document.createElement('button');
      b.type = 'button'; b.textContent = d;
      if (s < today || s > max) b.disabled = true;
      if (s === today) b.classList.add('today');
      if (s === cal.value) b.classList.add('sel');
      b.onclick = () => { const cb = cal.onPick; closeCal(); cb(s); };
      g.append(b);
    }
    $('calPrev').disabled = cal.month <= today.slice(0, 7) + '-01';
    $('calNext').disabled = cal.month >= max.slice(0, 7) + '-01';
  }
  function shiftMonth(n) {
    const d = toDate(cal.month); d.setUTCMonth(d.getUTCMonth() + n); cal.month = fromDate(d); renderCal();
  }
  $('calPrev').onclick = () => shiftMonth(-1);
  $('calNext').onclick = () => shiftMonth(1);
  $('calClose').onclick = closeCal;
  $('calOverlay').querySelector('.scrim').onclick = closeCal;
  $('calToday').onclick = () => { const cb = cal.onPick; closeCal(); cb(todayStr()); };

  /* =========================================================
     Reserva
     ========================================================= */
  const bk = { date: null, meetings: [], minutes: 30, start: null, busy: false, seq: 0 };
  const DURATIONS = [15, 30, 45, 60, 90, 120, 180, 240];

  function openBook() {
    bk.date = viewDate();
    bk.minutes = Math.min(Math.max(30, CFG.min), CFG.max);
    bk.start = null;
    bk.meetings = status && status.date === bk.date ? status.meetings : [];
    $('fTitle').value = ''; $('fName').value = '';
    hideMsg(); show('done', false);
    openOv($('bookSheet'));
    renderBook();
    loadBookDay();
  }
  function closeBook() { VK.close(); closeOv($('bookSheet')); }

  async function loadBookDay() {
    const mine = ++bk.seq, date = bk.date;
    try {
      const r = await fetch(`${api('api/status')}?date=${date}`, { cache: 'no-store' });
      const s = await r.json();
      if (mine !== bk.seq) return;
      clock.nowMs = s.nowMs; clock.offsetMin = s.utcOffsetMinutes; clock.at = performance.now();
      bk.meetings = s.meetings || [];
      if (s.error) showMsg(s.error, true);
    } catch (e) {
      if (mine === bk.seq) showMsg('No se puede comprobar la disponibilidad. Reintenta.', true);
    }
    if (mine === bk.seq) renderBook();
  }

  const free = (s, minutes) => !bk.meetings.some(m => m.startMin < s + minutes && m.endMin > s);

  function slotList() {
    const isToday = bk.date === todayStr(), nm = nowMin(), out = [];
    if (isToday) {
      const ok = nm >= CFG.workStart && nm + bk.minutes <= CFG.workEnd && free(nm, bk.minutes);
      out.push({ key: 'now', label: 'Ahora', min: nm, ok, now: true });
    }
    for (let s = CFG.workStart; s + bk.minutes <= CFG.workEnd; s += CFG.slot)
      out.push({ key: s, label: hhmm(s), min: s, ok: !(isToday && s < nm) && free(s, bk.minutes) });
    return out;
  }

  function renderBook() {
    const today = todayStr();
    setText('bkDayText', (bk.date === today ? 'Hoy · ' : '') + midDate(bk.date));
    $('bkPrev').disabled = bk.date <= today;
    $('bkNext').disabled = bk.date >= maxDateStr();

    // Duraciones
    const dBox = $('durations'); dBox.textContent = '';
    DURATIONS.filter(m => m >= CFG.min && m <= CFG.max).forEach(m => {
      const b = document.createElement('button');
      b.type = 'button'; b.className = 'chip' + (m === bk.minutes ? ' on' : ''); b.textContent = durLabel(m);
      b.onclick = () => { bk.minutes = m; renderBook(); };
      dBox.append(b);
    });

    // Hora de inicio en dos pasos para que todo quepa en la tablet sin desplazarse:
    // 1) «Ahora» + una fila de horas en punto, 2) los cuartos (:00 :15 :30 :45) de la hora elegida.
    const slots = slotList();
    if (!slots.some(x => x.key === bk.start && x.ok)) bk.start = (slots.find(x => x.ok) || {}).key ?? null;
    const quarters = slots.filter(x => !x.now);
    const hourOf = x => Math.floor(x.min / 60);
    if (bk.start !== 'now' && bk.start != null) bk.hour = Math.floor(bk.start / 60);
    else if (bk.start === 'now') bk.hour = Math.floor(nowMin() / 60);
    const hours = [...new Set(quarters.map(hourOf))];

    const sBox = $('slots'); sBox.textContent = '';
    const mk = (label, cls, ok, on, onclick) => {
      const b = document.createElement('button');
      b.type = 'button'; b.className = cls + (on ? ' on' : ''); b.textContent = label; b.disabled = !ok; b.onclick = onclick;
      return b;
    };
    const nowSlot = slots.find(x => x.now);
    if (nowSlot) sBox.append(mk('Ahora · ' + hhmm(nowSlot.min), 'slot now', nowSlot.ok, bk.start === 'now',
      () => { bk.start = 'now'; renderBook(); }));
    hours.forEach(h => {
      const qs = quarters.filter(x => hourOf(x) === h), free = qs.filter(x => x.ok);
      const b = mk(pad(h), 'slot hour' + (free.length && free.length < qs.length ? ' partial' : ''), free.length > 0,
        bk.start !== 'now' && bk.start != null && Math.floor(bk.start / 60) === h,
        () => { bk.start = free[0].key; renderBook(); });
      sBox.append(b);
    });

    const qBox = $('quarters'); qBox.textContent = '';
    quarters.filter(x => hourOf(x) === bk.hour).forEach(x =>
      qBox.append(mk(x.label, 'slot', x.ok, x.key === bk.start, () => { bk.start = x.key; renderBook(); })));
    const anyOk = slots.some(x => x.ok);
    setText('slotsHint', anyOk ? '' : '— no hay huecos libres con esta duración');

    // Resumen
    const sum = $('summary'); sum.textContent = '';
    const line = (k, v) => { const d = document.createElement('div'); d.append(k + ' '); const b = document.createElement('b'); b.textContent = v; d.append(b); sum.append(d); };
    line('Día', shortDate(bk.date));
    if (bk.start != null) {
      const st = bk.start === 'now' ? nowMin() : bk.start;
      line('Hora', `${hhmm(st)} – ${hhmm(st + bk.minutes)}`);
    }
    line('Duración', durLabel(bk.minutes));
    updateConfirm();
  }

  function updateConfirm() {
    const falta = [];
    if ($('fTitle').value.trim().length < 2) falta.push('el título');
    if ($('fName').value.trim().length < 2) falta.push('tu nombre');
    if (bk.start == null) falta.push('la hora');
    $('btnConfirm').disabled = bk.busy || falta.length > 0;
    setText('missing', falta.length ? 'Falta indicar ' + falta.join(' y ').replace(/ y (?=.* y )/, ', ') : '');
    show('missing', falta.length > 0);
  }
  $('fTitle').addEventListener('input', updateConfirm);
  $('fName').addEventListener('input', updateConfirm);

  function showMsg(t, warn) { const m = $('bookMsg'); m.textContent = t; m.classList.toggle('warn', !!warn); m.classList.remove('d-none'); }
  function hideMsg() { $('bookMsg').classList.add('d-none'); }

  async function confirmBooking() {
    if (bk.busy) return;
    bk.busy = true; hideMsg();
    $('btnConfirm').textContent = 'Reservando…'; updateConfirm();
    try {
      const tk = await (await fetch(api('api/token'), { cache: 'no-store' })).json();
      const r = await fetch(api('api/book'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tk.token },
        body: JSON.stringify({
          title: $('fTitle').value.trim(),
          organizer: $('fName').value.trim(),
          date: bk.date,
          start: bk.start === 'now' ? 'now' : hhmm(bk.start),
          minutes: bk.minutes
        })
      });
      const data = await r.json().catch(() => ({}));
      if (r.ok && data.success) {
        VK.close();
        const m = data.meeting;
        setText('doneText', `${$('fTitle').value.trim()} · ${midDate(bk.date)} · ${m ? m.startLocal + ' – ' + m.endLocal : ''}`);
        show('done', true);
        refresh();
        setTimeout(closeBook, 2800);
      } else {
        showMsg(data.message || 'No se pudo completar la reserva.');
        if (r.status === 409) loadBookDay();
      }
    } catch (e) {
      showMsg('Error de conexión. Inténtalo de nuevo.');
    } finally {
      bk.busy = false;
      $('btnConfirm').textContent = 'Confirmar reserva';
      updateConfirm();
    }
  }

  function setBookDate(d) { bk.date = d; bk.start = null; hideMsg(); bk.meetings = []; renderBook(); loadBookDay(); }
  $('btnBook').onclick = openBook;
  $('btnSheetClose').onclick = $('btnCancel').onclick = closeBook;
  $('btnConfirm').onclick = confirmBooking;
  $('bkPrev').onclick = () => setBookDate(addDays(bk.date, -1));
  $('bkNext').onclick = () => setBookDate(addDays(bk.date, 1));
  $('bkDay').onclick = () => openCalendar(bk.date, setBookDate);

  /* =========================================================
     Teclado táctil integrado (no depende del teclado del sistema)
     ========================================================= */
  const VK = (() => {
    const el = $('vk');
    const ACUTE = { a: 'á', e: 'é', i: 'í', o: 'ó', u: 'ú', A: 'Á', E: 'É', I: 'Í', O: 'Ó', U: 'Ú' };
    const DIAER = { u: 'ü', U: 'Ü' };
    const ROWS = [
      ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', { k: 'back' }],
      ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p'],
      ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'ñ'],
      [{ k: 'shift' }, 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '-'],
      [{ k: 'dead' }, '@', { k: 'space' }, '?', '!', { k: 'done' }]
    ];
    let target = null, shift = false, dead = 0; // dead: 0 ninguno, 1 acento, 2 diéresis

    function build() {
      el.textContent = '';
      ROWS.forEach(row => {
        const r = document.createElement('div'); r.className = 'vk-row';
        row.forEach(key => {
          const b = document.createElement('button');
          b.type = 'button'; b.tabIndex = -1;
          if (typeof key === 'string') { b.dataset.ch = key; b.textContent = key; }
          else {
            b.dataset.k = key.k;
            if (key.k === 'back') { b.className = 'wide'; b.innerHTML = icon('i-back'); b.setAttribute('aria-label', 'Borrar'); }
            if (key.k === 'shift') { b.className = 'wide'; b.innerHTML = icon('i-shift'); b.setAttribute('aria-label', 'Mayúsculas'); }
            if (key.k === 'dead') { b.className = 'wide'; b.textContent = '´'; b.setAttribute('aria-label', 'Acento'); }
            if (key.k === 'space') { b.className = 'space'; b.textContent = 'espacio'; b.style.fontSize = '1rem'; }
            if (key.k === 'done') { b.className = 'done-key'; b.textContent = 'Hecho'; }
          }
          r.append(b);
        });
        el.append(r);
      });
      el.addEventListener('mousedown', e => e.preventDefault());      // no robar el foco al input
      el.addEventListener('pointerdown', e => {
        const b = e.target.closest('button'); if (!b) return;
        e.preventDefault(); press(b);
      });
    }

    const needCap = () => {
      if (!target) return false;
      const s = target.selectionStart ?? target.value.length, before = target.value.slice(0, s);
      return before.length === 0 || /[.!?]\s$/.test(before);
    };
    const upper = () => shift || needCap();

    function paint() {
      const up = upper();
      el.querySelectorAll('button[data-ch]').forEach(b => {
        const c = b.dataset.ch; if (/[a-zñ]/.test(c)) b.textContent = up ? c.toUpperCase() : c;
      });
      const sh = el.querySelector('[data-k="shift"]'); if (sh) sh.classList.toggle('on', shift);
      const de = el.querySelector('[data-k="dead"]'); if (de) { de.classList.toggle('on', dead > 0); de.textContent = dead === 2 ? '¨' : '´'; }
    }

    function insert(ch) {
      if (!target) return;
      const s = target.selectionStart ?? target.value.length, e = target.selectionEnd ?? s;
      if (target.maxLength > 0 && target.value.length - (e - s) + ch.length > target.maxLength) return;
      target.value = target.value.slice(0, s) + ch + target.value.slice(e);
      target.setSelectionRange(s + ch.length, s + ch.length);
      target.dispatchEvent(new Event('input', { bubbles: true }));
    }
    function backspace() {
      if (!target) return;
      let s = target.selectionStart ?? target.value.length; const e = target.selectionEnd ?? s;
      if (s === e) { if (s === 0) return; s -= 1; }
      target.value = target.value.slice(0, s) + target.value.slice(e);
      target.setSelectionRange(s, s);
      target.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function press(b) {
      const k = b.dataset.k;
      if (k === 'back') backspace();
      else if (k === 'shift') shift = !shift;
      else if (k === 'dead') dead = (dead + 1) % 3;
      else if (k === 'space') { insert(' '); }
      else if (k === 'done') { close(); if (target) target.blur(); return; }
      else if (b.dataset.ch != null) {
        let c = b.dataset.ch;
        if (/[a-zñ]/.test(c) && upper()) c = c.toUpperCase();
        if (dead === 1 && ACUTE[c]) c = ACUTE[c];
        else if (dead === 2 && DIAER[c]) c = DIAER[c];
        insert(c);
        dead = 0; shift = false;
      }
      paint();
    }

    function open(t) {
      target = t; shift = false; dead = 0;
      if (!el.firstChild) build();
      el.classList.add('open'); el.setAttribute('aria-hidden', 'false');
      document.body.classList.add('kb-open');
      paint();
      setTimeout(() => t.scrollIntoView({ block: 'nearest' }), 50);
    }
    function close() {
      el.classList.remove('open'); el.setAttribute('aria-hidden', 'true');
      document.body.classList.remove('kb-open');
    }

    document.addEventListener('focusin', e => { if (e.target.classList && e.target.classList.contains('vk-input')) open(e.target); });
    document.addEventListener('input', e => { if (e.target === target) paint(); });
    document.addEventListener('pointerdown', e => {
      if (!el.classList.contains('open')) return;
      if (el.contains(e.target) || (e.target.classList && e.target.classList.contains('vk-input'))) return;
      close();
    }, true);
    document.addEventListener('keyup', () => paint());
    return { close };
  })();

  /* =========================================================
     Modo kiosco: pantalla completa, sin zoom, sin reposo, vuelta a «hoy»
     ========================================================= */
  let fsAuto = true;
  try {
    const q = new URLSearchParams(location.search).get('fs');
    if (q === '0') localStorage.setItem('sala.fs', '0');
    if (q === '1') localStorage.setItem('sala.fs', '1');
    fsAuto = localStorage.getItem('sala.fs') !== '0';
  } catch (e) { /* sin almacenamiento: seguimos con el valor por defecto */ }

  const inFs = () => !!(document.fullscreenElement || document.webkitFullscreenElement);
  function enterFs() {
    const el = document.documentElement, f = el.requestFullscreen || el.webkitRequestFullscreen;
    if (!f) return;
    try { const p = f.call(el); if (p && p.catch) p.catch(() => {}); } catch (e) { /* lo intentamos en el siguiente toque */ }
  }
  function exitFs() {
    const f = document.exitFullscreen || document.webkitExitFullscreen;
    if (f) { try { f.call(document); } catch (e) { } }
  }
  // El navegador solo permite pantalla completa tras un gesto del usuario: se pide en cada toque si no lo está.
  document.addEventListener('pointerdown', () => { if (fsAuto && !inFs()) enterFs(); }, true);
  $('btnFs').onclick = () => { if (inFs()) { fsAuto = false; exitFs(); } else { fsAuto = true; enterFs(); } };

  let wake = null;
  async function keepAwake() {
    try {
      if ('wakeLock' in navigator && document.visibilityState === 'visible' && !wake) {
        wake = await navigator.wakeLock.request('screen');
        wake.addEventListener('release', () => { wake = null; });
      }
    } catch (e) { /* no soportado: configurar el reposo en el sistema (ver README) */ }
  }
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { keepAwake(); refresh(); } });

  // Sin zoom por gestos, sin menú contextual, sin arrastrar
  ['gesturestart', 'gesturechange', 'gestureend', 'contextmenu', 'dragstart'].forEach(ev =>
    document.addEventListener(ev, e => e.preventDefault()));
  document.addEventListener('wheel', e => { if (e.ctrlKey) e.preventDefault(); }, { passive: false });
  document.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && ['+', '-', '=', '0'].includes(e.key)) e.preventDefault(); });

  // Si nadie toca la pantalla durante un rato, vuelve a «hoy» y cierra cualquier ventana
  let idleTimer = null;
  function idleReset() {
    const dirty = isOpen($('bookSheet')) || isOpen($('calOverlay')) || isOpen($('meetOverlay')) || selected;
    if (!dirty) return;
    closeMeeting(); closeCal(); closeBook(); selected = null; refresh(); render();
  }
  function bump() { clearTimeout(idleTimer); idleTimer = setTimeout(idleReset, CFG.idleMs); }
  ['pointerdown', 'keydown', 'touchstart'].forEach(ev => document.addEventListener(ev, bump, { passive: true, capture: true }));

  /* ---------------- Arranque ---------------- */
  tick();
  setInterval(tick, 1000);
  refresh();
  setInterval(refresh, CFG.refreshMs);
  keepAwake();
  bump();
})();
