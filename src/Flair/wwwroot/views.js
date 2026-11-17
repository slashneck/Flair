// Rotation, Settings and the Guide.
(() => {
  const { h, icon, segmented, toggle, hotkeyInput, clock, kindOf, toast, debounce } = window.ui;

  function head(title, ...extra) {
    return h('div.view-head', null, h('h1.view-title', null, title), h('span.spacer'), ...extra);
  }

  function thumb(p, store) {
    const app = store.cachedApp(store.appIdFor(p));
    const url = window.preview.imageUrl(p.largeImage, app);
    const t = h('span.thumb');
    if (url) t.style.backgroundImage = `url("${url.replace(/"/g, '%22')}")`;
    else t.append(icon(kindOf(p.kind).icon));
    return t;
  }

  // ---------- rotation ----------
  const INTERVALS = [15, 30, 60, 120, 300, 900, 1800, 3600];
  const intervalLabel = (s) => (s < 60 ? `${s}s` : s < 3600 ? `${s / 60} min` : `${s / 3600} h`);

  function rotation(main, store) {
    const s0 = store.snapshot.settings;
    let ids = [...s0.rotatePresetIds];
    let seconds = s0.rotateSeconds;
    let shuffle = s0.rotateShuffle;
    let enabled = s0.rotateEnabled;

    const push = debounce(() => window.flair.call('rotation.set', { enabled, ids, seconds, shuffle }).catch((e) => toast(e.message, 'error')), 150);

    const enabledToggle = toggle(enabled, (v) => { enabled = v; push(); paint(); });
    const list = h('div.rot-list');
    const chips = h('div.interval-chips');
    const orderSeg = segmented([{ value: 'order', label: 'In order' }, { value: 'shuffle', label: 'Shuffled' }], shuffle ? 'shuffle' : 'order',
      (v) => { shuffle = v === 'shuffle'; push(); });
    const custom = h('input.input.small', {
      type: 'number', min: 15, value: seconds, title: 'Seconds',
      onchange: (e) => { seconds = Math.max(15, parseInt(e.target.value, 10) || 60); e.target.value = seconds; push(); paintChips(); },
    });
    const big = h('div.big');
    const sub = h('div.hint');
    const fill = h('div');
    const startBtn = h('button.btn.primary', { onclick: () => {
      enabled = !enabled || !store.snapshot.state.live;
      enabledToggle.set(enabled);
      push.flush();
      if (enabled && !store.snapshot.state.live && ids.length) window.flair.call('preset.goLive', { id: ids[0] });
    } });

    function paintChips() {
      chips.replaceChildren(...INTERVALS.map((v) => h('button' + (v === seconds ? '.on' : ''), {
        type: 'button', onclick: () => { seconds = v; custom.value = v; push(); paintChips(); },
      }, intervalLabel(v))));
    }

    let dragId = null;
    function paintList() {
      const presets = store.snapshot.presets;
      // chosen ones first, in their rotation order, then the rest
      const chosen = ids.map((id) => presets.find((p) => p.id === id)).filter(Boolean);
      const rest = presets.filter((p) => !ids.includes(p.id));
      list.replaceChildren(...[...chosen, ...rest].map((p) => {
        const on = ids.includes(p.id);
        const live = store.snapshot.state.live && store.snapshot.state.activePresetId === p.id;
        const item = h('div.rot-item' + (on ? '.on' : ''), {
          draggable: on,
          onclick: () => {
            ids = on ? ids.filter((x) => x !== p.id) : [...ids, p.id];
            push();
            paintList();
            paint();
          },
          ondragstart: (e) => { dragId = p.id; item.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; },
          ondragend: () => { dragId = null; item.classList.remove('dragging'); },
          ondragover: (e) => { if (dragId && on && dragId !== p.id) e.preventDefault(); },
          ondrop: (e) => {
            e.preventDefault();
            const from = ids.indexOf(dragId), to = ids.indexOf(p.id);
            if (from < 0 || to < 0) return;
            ids.splice(to, 0, ids.splice(from, 1)[0]);
            push();
            paintList();
          },
        },
        h('span.check', null, icon('check')),
        h('span.num', null, on ? String(ids.indexOf(p.id) + 1) : ''),
        thumb(p, store),
        h('span.nav-label', null, p.name, h('span.nav-sub', null, [kindOf(p.kind).verb, p.details].filter(Boolean).join(' · '))),
        live ? h('span.badge', null, 'Live') : null,
        on ? h('span.grip', { title: 'Drag to reorder' }, icon('grip')) : null);
        return item;
      }));
    }

    function paint() {
      const st = store.snapshot.state;
      const running = st.live && st.rotating;
      startBtn.replaceChildren(icon(running ? 'x' : 'play'), running ? 'Stop rotating' : 'Start rotating');
      startBtn.className = 'btn ' + (running ? '' : 'primary');
      startBtn.disabled = ids.length < 2;
      if (ids.length < 2) {
        big.textContent = 'Off';
        sub.textContent = 'Pick at least two presets.';
      } else if (!running) {
        big.textContent = 'Off';
        sub.textContent = st.live ? 'Turn it on to start switching.' : 'Starts as soon as your status is live.';
      }
      tick();
    }
    function tick() {
      const st = store.snapshot.state;
      if (!(st.live && st.rotating) || !st.nextRotateMs) { fill.style.width = '0%'; return; }
      const left = st.nextRotateMs - Date.now();
      big.textContent = clock(left);
      const next = nextName();
      sub.textContent = next ? `until ${next}` : 'until the next one';
      fill.style.width = Math.min(100, Math.max(0, (1 - left / (seconds * 1000)) * 100)) + '%';
    }
    function nextName() {
      if (shuffle && ids.length > 2) return null;
      const at = ids.indexOf(store.snapshot.state.activePresetId);
      const p = store.preset(ids[(at + 1) % ids.length]);
      return p?.name;
    }

    paintChips();
    paintList();
    const view = h('section.view', null,
      head('Rotation', h('span.hint', null, 'Switch between presets on a timer'), enabledToggle),
      h('div.view-body', null, h('div.rotation', null,
        h('div.form', null,
          h('section.card', null, h('h3.card-title', null, 'Presets', h('span.spacer'), h('span.hint', null, 'Click to add, drag to reorder')), list)),
        h('div.form', null,
          h('section.card', null, h('div.countdown', null, h('h3.card-title', { style: { marginBottom: '4px' } }, 'Next change'), big, sub, h('div.bar', null, fill)),
            h('div', { style: { marginTop: '16px' } }, startBtn)),
          h('section.card', null,
            h('h3.card-title', null, 'Switch every'), chips,
            h('div.row', { style: { marginTop: '10px' } }, custom, h('span.hint', null, 'seconds. Discord needs at least 15.'))),
          h('section.card', null, h('h3.card-title', null, 'Order'), orderSeg,
            h('p.hint', { style: { margin: '10px 0 0' } }, 'Shuffled plays every preset once in a random order, then starts a new order.'))))));
    main.replaceChildren(view);
    paint();
    const ticker = setInterval(tick, 1000);
    return {
      refresh() {
        const s = store.snapshot.settings;
        enabled = s.rotateEnabled;
        enabledToggle.set(enabled);
        ids = ids.filter((id) => store.preset(id));
        paintList();
        paint();
      },
      leave() { if (push.pending()) push.flush(); clearInterval(ticker); },
    };
  }

  // ---------- settings ----------
  function settings(main, store, version, update) {
    let draft = structuredClone(store.snapshot.settings);
    const save = debounce(async () => {
      try { await window.flair.call('settings.save', { settings: draft }); }
      catch (e) { toast(e.message, 'error'); }
    }, 300);
    const set = (mutate) => { mutate(draft); save(); };

    const appCheck = h('div.app-check');
    const appInput = h('input.input.mono', {
      value: draft.appId, placeholder: 'Empty shows Playing Flair', inputMode: 'numeric', maxLength: 24, spellcheck: false,
      oninput: (e) => { e.target.value = e.target.value.replace(/\D/g, ''); set((d) => { d.appId = e.target.value; }); check(); },
    });
    const check = debounce(async () => {
      const id = appInput.value;
      if (!id) { appCheck.className = 'app-check'; appCheck.replaceChildren('Your status shows as Playing Flair. For a different name, make your own app; the Guide shows how.'); return; }
      if (id.length < 15) { appCheck.className = 'app-check bad'; appCheck.replaceChildren(icon('warn'), 'That\'s too short for an application ID.'); return; }
      appCheck.className = 'app-check';
      appCheck.replaceChildren('Checking…');
      const app = await store.lookup(id);
      if (appInput.value !== id) return;
      if (app.found === false) { appCheck.className = 'app-check bad'; appCheck.replaceChildren(icon('warn'), 'Discord doesn\'t know this application ID.'); return; }
      appCheck.className = 'app-check ok';
      appCheck.replaceChildren(app.iconUrl ? h('img', { src: app.iconUrl, alt: '' }) : icon('sparkle'),
        h('span', null, 'Shows as ', h('b', null, 'Playing ' + (app.name || 'your app')),
          Object.keys(app.assets || {}).length ? ` · ${Object.keys(app.assets).length} pictures uploaded` : ''));
    }, 250);

    const updateBox = h('div.text');
    const updateBtn = h('button.btn');
    function paintUpdate(u) {
      update = u || update;
      const st = update?.state;
      const line = {
        checking: 'Checking GitHub…', latest: 'You have the latest version.', downloading: `Downloading ${update.available}… ${Math.round((update.progress || 0) * 100)}%`,
        ready: `Version ${update?.available} is ready.`, available: `Version ${update?.available} is out.`, error: update?.error || 'The check didn\'t work.',
      }[st] || 'Flair looks for new versions on GitHub every few hours.';
      updateBox.replaceChildren(h('div.title', null, `Flair ${version}`), h('div.hint', null, line));
      if (st === 'ready') { updateBtn.textContent = 'Restart to update'; updateBtn.onclick = () => window.flair.call('update.install'); }
      else if (st === 'available' && !update.canInstall) { updateBtn.textContent = 'Open GitHub'; updateBtn.onclick = () => window.flair.call('link.open', { url: update.pageUrl }); }
      else { updateBtn.textContent = 'Check now'; updateBtn.onclick = async () => paintUpdate(await window.flair.call('update.check')); }
      updateBtn.disabled = st === 'checking' || st === 'downloading';
    }

    function row(title, hint, control) {
      return h('div.setting-row', null, h('div.text', null, h('div.title', null, title), hint ? h('div.hint', null, hint) : null), control);
    }

    const view = h('section.view', null,
      head('Settings'),
      h('div.view-body', null, h('div.narrow', null,
        h('section.card', null,
          h('h3.card-title', null, 'Discord application'),
          h('label.field', null, h('span.field-label', null, 'Application ID'), appInput,
            h('span.hint', null, 'Your status runs as this app, and its name is what shows after "Playing". ',
              h('a', { onclick: () => store.show('guide') }, 'How to make one'))),
          appCheck),
        h('section.card', null,
          h('h3.card-title', null, 'Hotkeys'),
          row('Turn the status on or off', null, hotkeyInput(draft.toggleHotkey, (hk) => set((d) => { d.toggleHotkey = hk; }))),
          row('Next preset', 'The next one in the rotation, or in the list', hotkeyInput(draft.nextHotkey, (hk) => set((d) => { d.nextHotkey = hk; }))),
          h('p.hint', { style: { margin: '14px 0 0' } }, 'Each preset can have its own hotkey too, at the bottom of its page. They work everywhere, also in games.')),
        h('section.card', null,
          h('h3.card-title', null, 'App'),
          row('Start with Windows', 'Starts quietly in the tray and brings your last status back', toggle(draft.startWithWindows, (v) => set((d) => { d.startWithWindows = v; }))),
          row('Check for updates', 'The only thing Flair asks the internet for besides your pictures', toggle(draft.checkForUpdates, (v) => set((d) => { d.checkForUpdates = v; })))),
        h('section.card', null,
          h('h3.card-title', null, 'About'),
          h('div.setting-row', null, updateBox, updateBtn),
          h('div.setting-row', null,
            h('div.text', null, h('div.hint', null, 'Free software under the GPL 3.0. Flair only talks to the Discord app on this PC through its official Rich Presence connection. No account, no telemetry, and your presets never leave this PC unless you export them.')),
            h('div', { style: { display: 'flex', gap: '6px' } },
              h('button.btn.ghost', { onclick: () => window.flair.call('link.open', { url: 'https://github.com/slashneck/Flair' }) }, 'GitHub'),
              h('button.btn.ghost', { onclick: () => window.flair.call('log.open') }, 'Log')))))));
    main.replaceChildren(view);
    check.flush();
    paintUpdate(update);
    return {
      refresh() {},
      updateChanged: paintUpdate,
      leave() { if (save.pending()) save.flush(); },
    };
  }

  // ---------- guide ----------
  function guide(main, store) {
    const appInput = h('input.input.mono', {
      value: store.snapshot.settings.appId, placeholder: 'Paste the Application ID here, or leave it empty for Flair', inputMode: 'numeric', maxLength: 24, spellcheck: false,
      style: { maxWidth: '320px' },
    });
    const status = h('span.hint');
    const save = debounce(async () => {
      const id = appInput.value;
      const next = { ...store.snapshot.settings, appId: id, guideSeen: true };
      await window.flair.call('settings.save', { settings: next });
      if (!id) { status.textContent = 'Empty, so your status shows as Playing Flair.'; stepThree.classList.remove('done'); return; }
      const app = await store.lookup(id);
      status.replaceChildren(app.found === false ? 'Discord doesn\'t know this ID yet. Check that you copied the Application ID.'
        : h('span', null, 'Saved. Your status shows as ', h('b', { style: { color: 'var(--text)' } }, 'Playing ' + (app.name || 'your app')), '.'));
      stepThree.classList.toggle('done', app.found !== false);
    }, 400);
    appInput.addEventListener('input', () => { appInput.value = appInput.value.replace(/\D/g, ''); save(); });
    const stepThree = h('div.step' + (store.snapshot.settings.appId ? '.done' : ''), null, h('div', null,
      h('h3', null, 'Copy its Application ID'),
      h('p', null, 'It\'s on the app\'s General Information page. Paste it here:'),
      h('div.row', null, appInput), h('p', { style: { marginTop: '8px' } }, status)));

    // the guide counts as seen once opened
    if (!store.snapshot.settings.guideSeen) window.flair.call('settings.save', { settings: { ...store.snapshot.settings, guideSeen: true } }).catch(() => {});

    const view = h('section.view', null,
      head('Guide'),
      h('div.view-body', null, h('div.narrow', null,
        h('section.card', null,
          h('h3.card-title', null, 'A different name after "Playing"'),
          h('p.hint', { style: { margin: '0 0 16px', fontSize: '13px' } },
            "Out of the box your status shows as Playing Flair, and you don't need to set anything up. Discord always shows the name of a Discord application there, so for a name of your own, make your own app. It's free, takes a minute, and your friends don't need to install anything."),
          h('div.steps', null,
            h('div.step', null, h('div', null,
              h('h3', null, 'Open the Discord Developer Portal'),
              h('p', null, 'Sign in with your normal Discord account in the browser.'),
              h('button.btn', { onclick: () => window.flair.call('link.open', { url: 'https://discord.com/developers/applications' }) }, icon('link'), 'Open the Developer Portal'))),
            h('div.step', null, h('div', null,
              h('h3', null, 'Click "New Application" and name it'),
              h('p', null, 'Pick the name you want on your profile, like ', h('code', null, 'my lil studio'), ' or ', h('code', null, 'touching grass'),
                '. You can rename it whenever you like. For several names, make several apps and give a preset its own ID.'))),
            stepThree,
            h('div.step', null, h('div', null,
              h('h3', null, 'Optional: upload pictures'),
              h('p', null, 'Under ', h('code', null, 'Rich Presence → Art Assets'), ', upload square images (512 × 512 or bigger) and give each a name. Type that name into a picture field, or click it in the editor. Uploads can take a few minutes to show up.'),
              h('p', null, 'Or skip this and paste any https picture link, GIFs included.')))))),
        h('div.tips', null,
          h('div.tip', null, h('h4', null, icon('eye-off'), 'Don\'t see it?'),
            h('p', null, 'In Discord, open User Settings → Activity Privacy and turn on "Share your detected activities with others". Flair needs the Discord desktop app, not the browser.')),
          h('div.tip', null, h('h4', null, icon('link'), 'Buttons'),
            h('p', null, 'Discord hides your own buttons from you. Your friends see them, so ask one to check.')),
          h('div.tip', null, h('h4', null, icon('clock'), 'Updates take a moment'),
            h('p', null, 'Discord takes a few changes per 20 seconds. Flair waits and sends the latest one, so nothing gets lost.')),
          h('div.tip', null, h('h4', null, icon('shield'), 'Safe for your account'),
            h('p', null, 'Flair uses the same official Rich Presence connection games use. It never touches your login, token or custom status.')))));
    main.replaceChildren(view);
    return { refresh() {}, leave() { if (save.pending()) save.flush(); } };
  }

  window.views = { rotation, settings, guide, thumb };
})();
