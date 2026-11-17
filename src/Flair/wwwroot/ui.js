// Small DOM helpers shared by every view.
(() => {
  /** h('div.card', { onclick }, child, 'text') */
  function h(tag, props, ...children) {
    const [name, ...classes] = tag.split('.');
    const el = document.createElement(name || 'div');
    if (classes.length) el.className = classes.join(' ');
    if (props) {
      for (const [k, v] of Object.entries(props)) {
        if (v == null || v === false) continue;
        if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
        else if (k === 'class') el.className += ' ' + v;
        else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
        else if (k === 'html') el.innerHTML = v;
        else if (k in el && k !== 'list' && k !== 'type') el[k] = v;
        else el.setAttribute(k, v === true ? '' : v);
      }
    }
    for (const c of children.flat()) {
      if (c == null || c === false) continue;
      el.append(c instanceof Node ? c : document.createTextNode(String(c)));
    }
    return el;
  }

  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#i-' + name);
    svg.append(use);
    return svg;
  }

  function toast(text, kind = 'ok') {
    const el = h('div.toast.' + kind, null, icon(kind === 'error' ? 'warn' : 'check'), text);
    document.getElementById('toasts').append(el);
    setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 260); }, kind === 'error' ? 4200 : 2400);
  }

  let openMenu = null;
  function closeMenu() { openMenu?.remove(); openMenu = null; }
  /** A small dropdown under an anchor. items: [{ label, icon, run, danger } | '-'] */
  function menu(anchor, items) {
    closeMenu();
    const box = h('div.menu');
    for (const it of items) {
      if (it === '-') { box.append(h('hr')); continue; }
      box.append(h('button' + (it.danger ? '.danger' : ''), { onclick: () => { closeMenu(); it.run(); } }, it.icon ? icon(it.icon) : null, it.label));
    }
    document.body.append(box);
    const r = anchor.getBoundingClientRect();
    const w = box.offsetWidth, ht = box.offsetHeight;
    box.style.left = Math.min(window.innerWidth - w - 8, Math.max(8, r.right - w)) + 'px';
    box.style.top = (r.bottom + ht + 6 > window.innerHeight ? r.top - ht - 6 : r.bottom + 6) + 'px';
    openMenu = box;
    setTimeout(() => document.addEventListener('pointerdown', function away(e) {
      if (!box.contains(e.target)) { closeMenu(); document.removeEventListener('pointerdown', away, true); }
    }, true));
  }
  window.addEventListener('blur', closeMenu);
  window.addEventListener('resize', closeMenu);

  /** Segmented control with a sliding knob. options: [{ value, label }] */
  function segmented(options, value, onChange) {
    const knob = h('span.knob');
    const box = h('div.seg', null, knob);
    const buttons = options.map((o) => {
      const b = h('button', { type: 'button', onclick: () => set(o.value, true) }, o.label);
      b.dataset.value = o.value;
      box.append(b);
      return b;
    });
    function place() {
      const on = buttons.find((b) => b.classList.contains('on'));
      if (!on || !on.offsetWidth) return;
      knob.style.left = on.offsetLeft + 'px';
      knob.style.width = on.offsetWidth + 'px';
    }
    function set(v, fire) {
      buttons.forEach((b) => b.classList.toggle('on', b.dataset.value === String(v)));
      place();
      if (fire) onChange(v);
    }
    set(value, false);
    new ResizeObserver(place).observe(box);
    box.set = (v) => set(v, false);
    return box;
  }

  function toggle(on, onChange) {
    const t = h('button.toggle' + (on ? '.on' : ''), { type: 'button', role: 'switch' });
    t.setAttribute('aria-checked', on);
    t.addEventListener('click', (e) => {
      e.stopPropagation();
      on = !on;
      t.classList.toggle('on', on);
      t.setAttribute('aria-checked', on);
      onChange(on);
    });
    t.set = (v) => { on = v; t.classList.toggle('on', v); };
    return t;
  }

  // ---------- hotkeys ----------
  // Keys travel as the WinForms Keys value: a number when just recorded, its name when it came from C#.
  const KEY_NAMES = {
    next: 'Page Down', prior: 'Page Up', pageDown: 'Page Down', pageUp: 'Page Up', oemcomma: ',', oemPeriod: '.', oemMinus: '-', oemplus: '+',
    oemQuestion: '/', oemSemicolon: ';', oemQuotes: "'", oemOpenBrackets: '[', oemCloseBrackets: ']', oemPipe: '\\', oemtilde: '`',
    space: 'Space', back: 'Backspace', capital: 'Caps Lock', escape: 'Esc', scroll: 'Scroll Lock', pause: 'Pause', insert: 'Insert',
    delete: 'Delete', home: 'Home', end: 'End', up: 'Up', down: 'Down', left: 'Left', right: 'Right', multiply: 'Num *', add: 'Num +',
    subtract: 'Num -', divide: 'Num /', decimal: 'Num .',
  };
  function keyLabel(key, code) {
    if (typeof key === 'number') return code || String.fromCharCode(key);
    if (/^d\d$/.test(key)) return key.slice(1);
    if (/^numPad\d$/.test(key)) return 'Num ' + key.slice(6);
    if (/^f\d+$/.test(key)) return key.toUpperCase();
    if (KEY_NAMES[key]) return KEY_NAMES[key];
    return key.length === 1 ? key.toUpperCase() : key[0].toUpperCase() + key.slice(1);
  }
  function hotkeyText(hk) {
    if (!hk || !hk.key || hk.key === 'none' || hk.key === 0) return '';
    const parts = [];
    if (hk.ctrl) parts.push('Ctrl');
    if (hk.alt) parts.push('Alt');
    if (hk.shift) parts.push('Shift');
    if (hk.win) parts.push('Win');
    parts.push(hk.label || keyLabel(hk.key));
    return parts.join(' + ');
  }
  function eventKeyLabel(e) {
    if (/^Key[A-Z]$/.test(e.code)) return e.code.slice(3);
    if (/^Digit\d$/.test(e.code)) return e.code.slice(5);
    if (/^Numpad\d$/.test(e.code)) return 'Num ' + e.code.slice(6);
    if (/^F\d+$/.test(e.key)) return e.key;
    const map = { ' ': 'Space', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', PageUp: 'Page Up', PageDown: 'Page Down' };
    return map[e.key] || (e.key.length === 1 ? e.key.toUpperCase() : e.key);
  }

  /** A button that records a key combination. Needs Ctrl, Alt or Win, so typing never fires a status change. */
  function hotkeyInput(value, onChange) {
    let hk = value && hotkeyText(value) ? { ...value } : null;
    const label = h('span');
    const clear = h('span.clear', { title: 'Remove', onclick: (e) => { e.stopPropagation(); hk = null; render(); onChange({ key: 0 }); } }, icon('x'));
    const btn = h('button.hotkey', { type: 'button' }, label, clear);
    let recording = false;
    function render() {
      btn.classList.toggle('recording', recording);
      btn.classList.toggle('empty', !hk && !recording);
      label.textContent = recording ? 'Press a combination…' : hk ? hotkeyText(hk) : 'Not set';
      clear.hidden = !hk || recording;
    }
    function stop() {
      if (!recording) return;
      recording = false;
      window.flair.call('hotkeys.pause', { paused: false });
      render();
    }
    btn.addEventListener('click', () => {
      if (recording) return stop();
      recording = true;
      window.flair.call('hotkeys.pause', { paused: true });
      render();
    });
    btn.addEventListener('blur', stop);
    btn.addEventListener('keydown', (e) => {
      if (!recording) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape') return stop();
      if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(e.key)) return;
      if (!e.ctrlKey && !e.altKey && !e.metaKey) {
        label.textContent = 'Add Ctrl, Alt or Win';
        return;
      }
      hk = { key: e.keyCode, ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, label: eventKeyLabel(e) };
      stop();
      onChange({ key: hk.key, ctrl: hk.ctrl, alt: hk.alt, shift: hk.shift, win: hk.win });
    });
    render();
    return btn;
  }

  // ---------- time ----------
  function clock(ms) {
    const s = Math.max(0, Math.floor(ms / 1000));
    const hh = Math.floor(s / 3600), mm = Math.floor((s % 3600) / 60), ss = s % 60;
    const pad = (n) => String(n).padStart(2, '0');
    return hh > 0 ? `${hh}:${pad(mm)}:${pad(ss)}` : `${mm}:${pad(ss)}`;
  }
  function nextTimeOfDay(hhmm, now = new Date()) {
    const [hh, mm] = String(hhmm || '20:00').split(':').map((x) => parseInt(x, 10) || 0);
    const at = new Date(now);
    at.setHours(hh, mm, 0, 0);
    if (at <= now) at.setDate(at.getDate() + 1);
    return at.getTime();
  }

  function debounce(fn, ms) {
    let t = null;
    const d = (...args) => { clearTimeout(t); t = setTimeout(() => { t = null; fn(...args); }, ms); };
    d.flush = (...args) => { clearTimeout(t); t = null; return fn(...args); };
    d.cancel = () => { clearTimeout(t); t = null; };
    d.pending = () => t !== null;
    return d;
  }

  const KINDS = [
    { value: 'playing', label: 'Playing', icon: 'gamepad', verb: 'Playing' },
    { value: 'listening', label: 'Listening', icon: 'headphones', verb: 'Listening to' },
    { value: 'watching', label: 'Watching', icon: 'tv', verb: 'Watching' },
    { value: 'competing', label: 'Competing', icon: 'trophy', verb: 'Competing in' },
  ];
  const kindOf = (v) => KINDS.find((k) => k.value === v) || KINDS[0];

  window.ui = { h, icon, toast, menu, closeMenu, segmented, toggle, hotkeyInput, hotkeyText, clock, nextTimeOfDay, debounce, KINDS, kindOf };
})();
