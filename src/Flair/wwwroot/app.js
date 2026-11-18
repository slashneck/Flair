// Flair's window: the sidebar with the presets, and whichever view is open.
(() => {
  const { h, icon, toast, menu, hotkeyText, kindOf } = window.ui;
  const flair = window.flair;
  const main = document.getElementById('main');
  const list = document.getElementById('preset-list');

  let version = '';
  let update = null;
  let current = null;       // the open view's controller
  let route = { view: 'editor', id: null };
  const apps = new Map();   // application ID -> lookup promise / result

  const store = {
    snapshot: null,
    preset: (id) => store.snapshot.presets.find((p) => p.id === id),
    patchPreset(saved) {
      const i = store.snapshot.presets.findIndex((p) => p.id === saved.id);
      if (i >= 0) store.snapshot.presets[i] = structuredClone(saved);
      renderSidebarSoon();
    },
    appIdFor(p) { return (p && p.appId) || store.snapshot.settings.appId || store.snapshot.defaultAppId; },
    cachedApp(id) {
      const hit = apps.get(id);
      return hit && !(hit instanceof Promise) ? hit : null;
    },
    lookup(id) {
      if (!id) return Promise.resolve(null);
      const hit = apps.get(id);
      if (hit) return Promise.resolve(hit);
      const p = flair.call('app.lookup', { appId: id })
        .then((r) => { apps.set(id, r); renderSidebar(); return r; })
        .catch(() => { apps.delete(id); return { found: true, assets: {} }; });
      apps.set(id, p);
      return p;
    },
    show(view, id) { navigate(view, id); },
    imported(r) { afterImport(r); },
    async create() {
      const p = await flair.call('preset.create');
      store.snapshot.presets.push(p);
      navigate('editor', p.id);
      current?.focusName?.();
    },
    async duplicate(id) {
      const p = await flair.call('preset.duplicate', { id });
      if (p) { await refreshSnapshot(); navigate('editor', p.id); toast('Duplicated'); }
    },
    async remove(id) {
      const p = store.preset(id);
      const index = store.snapshot.presets.indexOf(p);
      if (route.id === id) current?.leave?.(), (current = null);
      await flair.call('preset.delete', { id });
      store.snapshot.presets = store.snapshot.presets.filter((x) => x.id !== id);
      const next = store.snapshot.presets[Math.min(index, store.snapshot.presets.length - 1)];
      navigate('editor', next?.id ?? null);
      toast(`Deleted ${p?.name ?? 'preset'}`);
    },
    async exportPresets(ids) {
      try {
        const path = await flair.call('presets.export', { ids });
        if (path) toast('Exported. Send the file to a friend, they open it with Flair.');
      } catch (e) { toast(e.message, 'error'); }
    },
  };
  window.store = store;

  async function refreshSnapshot() {
    store.snapshot = (await flair.call('app.init')).snapshot;
  }

  // ---------- routing ----------
  function navigate(view, id) {
    if (current) current.leave?.();
    current = null;
    route = { view, id: view === 'editor' ? id : null };
    document.querySelectorAll('.sidebar-foot .nav-item').forEach((b) => b.classList.toggle('active', b.dataset.view === view));
    if (view === 'editor') {
      const p = id && store.preset(id);
      if (!p) {
        const first = store.snapshot.presets[0];
        if (first) return navigate('editor', first.id);
        renderEmpty();
      } else {
        current = window.editor.open(main, store, id);
      }
    } else if (view === 'rotation') {
      current = window.views.rotation(main, store);
    } else if (view === 'settings') {
      current = window.views.settings(main, store, version, update);
    } else if (view === 'gallery') {
      current = window.views.gallery(main, store);
    } else if (view === 'guide') {
      current = window.views.guide(main, store);
    }
    renderSidebar();
    renderBanner();
  }

  function renderEmpty() {
    main.replaceChildren(h('section.view', null, h('div.empty', null,
      h('img', { src: 'logo.png', alt: '' }),
      h('h2', null, 'No presets yet'),
      h('p', null, 'A preset is one status: the text, pictures, buttons and timer your friends see.'),
      h('button.btn.primary', { onclick: () => store.create() }, icon('plus'), 'New preset'),
      h('button.btn.ghost', { onclick: () => importDialog() }, icon('import'), 'Import a .flair file'))));
  }

  // ---------- sidebar ----------
  let dragId = null;
  function renderSidebar() {
    const snap = store.snapshot;
    if (!snap) return;
    const st = snap.state;
    list.replaceChildren(...snap.presets.map((p) => {
      const live = st.live && st.activePresetId === p.id;
      const hk = hotkeyText(p.hotkey);
      const item = h('button.nav-item.preset' + (route.view === 'editor' && route.id === p.id ? '.active' : ''), {
        draggable: true,
        onclick: () => navigate('editor', p.id),
        ondblclick: () => flair.call('preset.goLive', { id: p.id }),
        oncontextmenu: (e) => {
          e.preventDefault();
          menu(item, [
            { label: live ? 'Live now' : 'Show this', icon: 'play', run: () => flair.call('preset.goLive', { id: p.id }) },
            { label: 'Duplicate', icon: 'copy', run: () => store.duplicate(p.id) },
            { label: 'Export to share', icon: 'share', run: () => store.exportPresets([p.id]) },
            '-',
            { label: 'Delete', icon: 'trash', danger: true, run: () => store.remove(p.id) },
          ]);
        },
        ondragstart: (e) => { dragId = p.id; item.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/flair-preset', p.id); },
        ondragend: () => { dragId = null; item.classList.remove('dragging'); clearDrop(); },
        ondragover: (e) => {
          if (!dragId || dragId === p.id) return;
          e.preventDefault();
          const r = item.getBoundingClientRect();
          clearDrop();
          item.classList.add(e.clientY < r.top + r.height / 2 ? 'drop-before' : 'drop-after');
        },
        ondrop: (e) => {
          if (!dragId) return;
          e.preventDefault();
          e.stopPropagation();
          const after = item.classList.contains('drop-after');
          clearDrop();
          const ids = snap.presets.map((x) => x.id).filter((x) => x !== dragId);
          ids.splice(ids.indexOf(p.id) + (after ? 1 : 0), 0, dragId);
          snap.presets.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id));
          renderSidebar();
          flair.call('preset.reorder', { ids });
        },
        title: 'Double-click to show it on Discord',
      },
      window.views.thumb(p, store),
      h('span.nav-label', null, p.name, h('span.nav-sub', null, [kindOf(p.kind).verb, p.details].filter(Boolean).join(' · ') || 'Empty')),
      hk ? h('kbd.hk', null, hk.replace(/ \+ /g, '+')) : null,
      live ? h('span.on-air', { title: 'Live on Discord' }) : null);
      return item;
    }));
    for (const p of snap.presets) store.lookup(store.appIdFor(p)); // thumbnails for asset pictures
    renderStatus();
  }
  let sidebarFrame = 0;
  function renderSidebarSoon() {
    cancelAnimationFrame(sidebarFrame);
    sidebarFrame = requestAnimationFrame(renderSidebar);
  }
  function clearDrop() { list.querySelectorAll('.drop-before,.drop-after').forEach((x) => x.classList.remove('drop-before', 'drop-after')); }

  function renderStatus() {
    const snap = store.snapshot;
    const st = snap.state;
    const d = st.discord;
    const box = document.getElementById('live-status');
    const text = document.getElementById('live-text');
    let state = 'off', line = 'Status off';
    if (st.live) {
      ({ state, line } = {
        live: { state: 'live', line: 'Live on Discord' },
        connecting: { state: 'wait', line: 'Connecting' },
        nodiscord: { state: 'wait', line: 'Waiting for Discord' },
        noapp: { state: 'error', line: 'Needs an application ID' },
        error: { state: 'error', line: 'Discord said no' },
      }[d.state] || { state: 'wait', line: 'Connecting' });
    }
    box.dataset.state = state;
    text.textContent = line;
    box.title = d.error || '';

    const btn = document.getElementById('go-live');
    btn.dataset.on = st.live;
    btn.disabled = snap.presets.length === 0;
    document.getElementById('go-live-label').textContent = st.live ? 'Turn off' : 'Go live';
    const kbd = document.getElementById('go-live-kbd');
    kbd.textContent = hotkeyText(snap.settings.toggleHotkey);
    kbd.hidden = !kbd.textContent;
    document.getElementById('rotation-badge').hidden = !(st.live && st.rotating);
  }

  // Problems worth a line above the view
  function renderBanner() {
    main.querySelector('.banner')?.remove();
    const snap = store.snapshot;
    const st = snap.state;
    if (!st.live) return;
    let b = null;
    if (st.discord.state === 'nodiscord') {
      b = h('div.banner', null, icon('warn'), h('div.text', null, 'Discord isn\'t running. Your status shows up as soon as you open the Discord app.'));
    } else if (st.discord.state === 'noapp') {
      b = h('div.banner.error', null, icon('warn'), h('div.text', null, 'Add your Discord application ID to go live.'),
        h('button.btn', { onclick: () => navigate('guide') }, 'Show me how'));
    } else if (st.discord.state === 'error') {
      b = h('div.banner.error', null, icon('warn'), h('div.text', null, st.discord.error || 'Discord didn\'t accept this status.'),
        /application id/i.test(st.discord.error || '') ? h('button.btn', { onclick: () => navigate('settings') }, 'Settings') : null);
    }
    if (b) main.prepend(b);
  }

  // ---------- events ----------
  flair.on('state', (snap) => {
    const oldSettings = store.snapshot?.settings;
    // the open editor keeps its own copy while typing, so the sidebar shows that one
    const typing = route.view === 'editor' && current?.draft?.();
    if (typing) snap.presets = snap.presets.map((p) => (p.id === typing.id ? structuredClone(typing) : p));
    store.snapshot = snap;
    if (route.view === 'editor' && route.id && !store.preset(route.id)) navigate('editor', null);
    else current?.refresh?.(oldSettings);
    renderSidebar();
    renderBanner();
  });

  flair.on('updateChanged', (u) => { update = u; current?.updateChanged?.(u); });
  flair.on('imported', (r) => afterImport(r));
  flair.on('toast', (t) => toast(t.text, t.kind));

  function afterImport(r) {
    if (!r) return;
    refreshSnapshot().then(() => {
      if (r.first) navigate('editor', r.first);
      const names = r.names || [];
      toast(names.length === 1 ? `Imported ${names[0]}` : `Imported ${names.length} presets`);
    });
  }

  async function importDialog() {
    try { afterImport(await flair.call('presets.import')); }
    catch (e) { toast(e.message, 'error'); }
  }

  document.getElementById('new-btn').addEventListener('click', () => store.create());
  document.getElementById('import-btn').addEventListener('click', importDialog);
  document.getElementById('go-live').addEventListener('click', (e) => {
    const btn = e.currentTarget;
    btn.classList.remove('flash');
    void btn.offsetWidth;
    btn.classList.add('flash');
    const st = store.snapshot.state;
    if (!st.live && route.view === 'editor' && route.id) flair.call('preset.goLive', { id: route.id });
    else flair.call('status.setLive', { live: !st.live });
  });
  document.querySelectorAll('.sidebar-foot .nav-item').forEach((b) => b.addEventListener('click', () => navigate(b.dataset.view)));

  // Ctrl+N new preset, Ctrl+D duplicate, Delete... the usual
  document.addEventListener('keydown', (e) => {
    const typing = /INPUT|TEXTAREA/.test(document.activeElement?.tagName) || document.activeElement?.classList.contains('hotkey');
    if (e.ctrlKey && e.key.toLowerCase() === 'n') { e.preventDefault(); store.create(); }
    else if (e.ctrlKey && e.key.toLowerCase() === 'd' && route.id) { e.preventDefault(); store.duplicate(route.id); }
    else if (e.ctrlKey && e.key.toLowerCase() === 'o') { e.preventDefault(); importDialog(); }
    else if (!typing && (e.key === 'ArrowDown' || e.key === 'ArrowUp') && route.view === 'editor') {
      const ps = store.snapshot.presets;
      const at = ps.findIndex((p) => p.id === route.id);
      const next = ps[at + (e.key === 'ArrowDown' ? 1 : -1)];
      if (next) { e.preventDefault(); navigate('editor', next.id); }
    }
  });

  // Drop a .flair file anywhere to import it
  let dropBox = null, dragDepth = 0;
  const hasFiles = (e) => [...(e.dataTransfer?.types || [])].includes('Files');
  window.addEventListener('dragenter', (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    if (dragDepth++ === 0) {
      dropBox = h('div.drop', null, h('div.drop-box', null, h('b', null, 'Drop to import'), 'Flair preset files (.flair)'));
      document.body.append(dropBox);
    }
  });
  window.addEventListener('dragleave', (e) => {
    if (!hasFiles(e)) return;
    if (--dragDepth <= 0) { dragDepth = 0; dropBox?.remove(); }
  });
  window.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
  window.addEventListener('drop', async (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    dropBox?.remove();
    const files = [...e.dataTransfer.files].filter((f) => /\.flair$/i.test(f.name));
    if (!files.length) return toast('Only .flair preset files can be dropped here', 'error');
    for (const f of files) {
      try {
        if (f.size > 512 * 1024) throw new Error('That file is too big to be a Flair preset');
        afterImport(await flair.call('presets.importText', { text: await f.text() }));
      } catch (err) { toast(err.message, 'error'); }
    }
  });

  // ---------- start ----------
  (async () => {
    const init = await flair.call('app.init');
    store.snapshot = init.snapshot;
    window.preview.defaultAppId = init.snapshot.defaultAppId;
    version = init.version;
    update = init.update;
    const s = store.snapshot;
    navigate('editor', s.state.activePresetId || s.presets[0]?.id);
    if (!s.settings.guideSeen) flair.call('settings.save', { settings: { ...s.settings, guideSeen: true } }).catch(() => {});
  })();
})();
