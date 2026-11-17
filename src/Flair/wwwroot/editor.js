// The preset editor: the form on the left, the live Discord preview on the right. Every change saves itself.
(() => {
  const { h, icon, segmented, toggle, hotkeyInput, debounce, KINDS, kindOf, menu, toast } = window.ui;

  const TIMERS = [
    { value: 'none', label: 'None' },
    { value: 'elapsed', label: 'Elapsed' },
    { value: 'countdown', label: 'Countdown' },
    { value: 'until', label: 'Until' },
  ];
  const LIST_SHOWS = [
    { value: 'name', label: 'App name' },
    { value: 'details', label: 'Details' },
    { value: 'state', label: 'State' },
  ];

  function open(main, store, id) {
    let draft = structuredClone(store.preset(id));
    if (!draft) return null;
    const openedAt = Date.now();
    let app = null;

    const save = debounce(async () => {
      try {
        const saved = await window.flair.call('preset.update', { preset: draft });
        // keep what the person is typing; take only what C# corrected (like a too long name)
        if (saved && saved.id === draft.id && !save.pending() && saved.name !== draft.name && document.activeElement !== nameInput) {
          draft.name = nameInput.value = saved.name;
          store.patchPreset(draft);
        }
      } catch (err) {
        toast(err.message, 'error');
      }
    }, 350);

    function change(mutate, opts = {}) {
      mutate(draft);
      store.patchPreset(draft);
      if (!opts.quiet) refreshPreview();
      save();
    }

    // ---------- header ----------
    const nameInput = h('input.name-input', {
      value: draft.name, maxLength: 60, spellcheck: false,
      oninput: (e) => change((d) => { d.name = e.target.value; }),
      onblur: () => { if (!draft.name.trim()) { nameInput.value = draft.name = 'Untitled'; save(); } },
      onkeydown: (e) => { if (e.key === 'Enter') e.target.blur(); },
    });
    const liveBtn = h('button.btn.primary', { onclick: () => goLive() });
    const moreBtn = h('button.icon-btn', { title: 'More', onclick: () => menu(moreBtn, [
      { label: 'Duplicate', icon: 'copy', run: () => store.duplicate(draft.id) },
      { label: 'Export to share', icon: 'share', run: () => store.exportPresets([draft.id]) },
      '-',
      { label: 'Delete', icon: 'trash', danger: true, run: () => store.remove(draft.id) },
    ]) }, icon('more'));
    const head = h('div.view-head', null, nameInput, liveBtn,
      h('button.btn.ghost', { title: 'Duplicate', onclick: () => store.duplicate(draft.id) }, icon('copy')),
      h('button.btn.ghost', { title: 'Export to share', onclick: () => store.exportPresets([draft.id]) }, icon('share')),
      moreBtn);

    async function goLive() {
      if (save.pending()) await save.flush();
      await window.flair.call('preset.goLive', { id: draft.id });
    }

    function refreshHeader() {
      const s = store.snapshot.state;
      const isLive = s.live && s.activePresetId === draft.id;
      liveBtn.className = 'btn ' + (isLive ? 'live' : 'primary');
      liveBtn.replaceChildren(isLive ? icon('check') : icon('play'), isLive ? 'Live' : 'Show this');
      liveBtn.title = isLive ? 'This is on your profile right now' : 'Put this on your Discord profile';
    }

    // ---------- form ----------
    function field(label, input, opts = {}) {
      const count = opts.max ? h('span.count') : null;
      const el = h('label.field', null, h('span.field-label', null, label, opts.extra || null, count), input, opts.hint ? h('span.hint', null, opts.hint) : null);
      if (count) {
        const update = () => {
          const n = input.value.length;
          count.textContent = `${n}/${opts.max}`;
          count.classList.toggle('near', n > opts.max - 12);
        };
        input.addEventListener('input', update);
        update();
      }
      return el;
    }
    function text(key, placeholder, max = 128) {
      return h('input.input', {
        value: draft[key] || '', placeholder, maxLength: max, spellcheck: false,
        oninput: (e) => change((d) => { d[key] = e.target.value; }),
      });
    }

    // Activity
    const kindSeg = segmented(KINDS.map((k) => ({ value: k.value, label: k.label })), draft.kind, (v) => { change((d) => { d.kind = v; }); paintVerb(); });
    const appLine = h('div.app-line');
    const appIdInput = h('input.input.mono', {
      value: draft.appId, placeholder: store.snapshot.settings.appId ? 'Uses the one from Settings' : "Uses Flair's app", inputMode: 'numeric', maxLength: 24,
      oninput: (e) => {
        e.target.value = e.target.value.replace(/\D/g, '');
        change((d) => { d.appId = e.target.value; });
        lookupApp();
      },
    });
    const activityCard = h('section.card', null,
      h('h3.card-title', null, 'Activity'),
      kindSeg,
      appLine,
      h('details.more', { open: !!draft.appId }, h('summary', null, icon('chevron'), 'Use a different Discord app for this preset'),
        h('div.more-body', null, field('Application ID', appIdInput, { hint: 'Its name is what shows after "Playing". Make one app per name you want to show; the Guide explains how.' }))));

    // Text
    const textCard = h('section.card', null,
      h('h3.card-title', null, 'Text'),
      field('Details', text('details', 'First line, like "Ranked grind"'), { max: 128 }),
      field('State', text('state', 'Second line, like "Looking for a duo"'), { max: 128 }));

    // Pictures
    const assetChips = h('div.chips');
    function imageField(key, textKey, title, small) {
      const well = h('div.image-well' + (small ? '.small' : ''), null, icon('image'));
      const input = h('input.input', {
        value: draft[key] || '', placeholder: 'https://… or an asset name', spellcheck: false, maxLength: 300,
        oninput: (e) => { change((d) => { d[key] = e.target.value; }); paint(); },
      });
      const hover = h('input.input', {
        value: draft[textKey] || '', placeholder: 'Hover text', maxLength: 128, spellcheck: false,
        oninput: (e) => change((d) => { d[textKey] = e.target.value; }),
      });
      function paint() {
        const v = input.value.trim();
        const url = window.preview.imageUrl(v, app);
        well.style.backgroundImage = url ? `url("${url.replace(/"/g, '%22')}")` : '';
        well.firstChild.style.display = url ? 'none' : '';
        const bad = v && !url && (/^[a-z]+:\/\//i.test(v) && !/^https:\/\//i.test(v) || (app && app.found !== false && !/^https:/i.test(v) && Object.keys(app.assets || {}).length > 0));
        input.classList.toggle('bad', !!bad);
        input.title = bad ? (/^http:\/\//i.test(v) ? 'Discord needs https links' : 'Your app has no picture with this name') : '';
      }
      const el = h('div.field', null,
        h('span.field-label', null, title),
        h('div.image-field', null, well, h('div', { style: { display: 'flex', flexDirection: 'column', gap: '8px', minWidth: 0 } }, input, hover)));
      el.paint = paint;
      el.input = input;
      paint();
      return el;
    }
    const largeField = imageField('largeImage', 'largeText', 'Large picture', false);
    const smallField = imageField('smallImage', 'smallText', 'Small picture', true);
    const picturesCard = h('section.card', null,
      h('h3.card-title', null, 'Pictures'),
      largeField, smallField,
      h('div.field', null, h('span.hint', null, 'Any https picture link works, like a GIF or an image from Imgur. Pictures uploaded to your Discord app work by their name.'), assetChips));
    function renderAssets() {
      const keys = Object.keys(app?.assets || {});
      assetChips.replaceChildren();
      if (!keys.length) return;
      assetChips.append(h('span.hint', { style: { width: '100%' } }, 'Your app\'s pictures. Click to use as the large one, Shift+click for the small one.'));
      for (const k of keys.slice(0, 40)) {
        assetChips.append(h('button.chip', {
          type: 'button',
          onclick: (e) => {
            const target = e.shiftKey ? smallField : largeField;
            target.input.value = k;
            target.input.dispatchEvent(new Event('input'));
          },
        }, h('img', { src: app.assets[k], alt: '' }), k));
      }
    }

    // Timer
    const timerExtra = h('div');
    const timerSeg = segmented(TIMERS, draft.timer, (v) => { change((d) => { d.timer = v; }); renderTimerExtra(); });
    function renderTimerExtra() {
      timerExtra.replaceChildren();
      timerExtra.style.marginTop = draft.timer === 'none' ? '0' : '12px';
      if (draft.timer === 'elapsed') timerExtra.append(h('span.hint', null, 'Counts up from when the status went live, and keeps counting through restarts and rotation.'));
      if (draft.timer === 'countdown') {
        timerExtra.append(h('div.row', null,
          h('input.input.small', { type: 'number', min: 1, max: 10080, value: draft.countdownMinutes, oninput: (e) => change((d) => { d.countdownMinutes = Math.max(1, parseInt(e.target.value, 10) || 1); }) }),
          h('span.hint', null, 'minutes, starting each time this preset comes up')));
      }
      if (draft.timer === 'until') {
        timerExtra.append(h('div.row', null,
          h('input.input', { type: 'time', value: draft.untilTime, style: { width: '120px' }, oninput: (e) => change((d) => { d.untilTime = e.target.value || '20:00'; }) }),
          h('span.hint', null, 'Counts down to this time, today or tomorrow')));
      }
    }
    const timerCard = h('section.card', null, h('h3.card-title', null, 'Timer'), timerSeg, timerExtra);
    renderTimerExtra();

    // Party
    const partyBody = h('div');
    function stepper(get, set, min, max) {
      const input = h('input.input', { type: 'number', min, max, value: get(), oninput: (e) => { set(parseInt(e.target.value, 10) || min); } });
      const bump = (n) => { set(Math.min(max(), Math.max(min, get() + n))); input.value = get(); };
      return h('div.stepper', null, h('button', { type: 'button', onclick: () => bump(-1) }, '−'), input, h('button', { type: 'button', onclick: () => bump(1) }, '+'));
    }
    function renderParty() {
      partyBody.replaceChildren();
      if (draft.partyMax <= 0) return;
      partyBody.style.marginTop = '12px';
      partyBody.append(h('div.row', null,
        stepper(() => draft.partySize, (v) => change((d) => { d.partySize = Math.min(v, d.partyMax); }), 1, () => draft.partyMax),
        h('span.of', null, 'of'),
        stepper(() => draft.partyMax, (v) => change((d) => { d.partyMax = Math.max(1, Math.min(999, v)); d.partySize = Math.min(d.partySize, d.partyMax); }), 1, () => 999)),
        h('span.hint', { style: { display: 'block', marginTop: '8px' } }, 'Shows next to the state line, so the state needs some text.'));
    }
    const partyToggle = toggle(draft.partyMax > 0, (on) => {
      change((d) => { if (on) { d.partyMax = d.partyMax || 5; d.partySize = d.partySize || 1; } else { d.partyMax = 0; d.partySize = 0; } });
      renderParty();
    });
    const partyCard = h('section.card', null,
      h('div.toggle-row', { onclick: () => partyToggle.click() },
        h('div.text', null, h('div.title', null, 'Party size'), h('div.hint', null, 'Like "2 of 5" in a lobby')), partyToggle),
      partyBody);
    renderParty();

    // Buttons
    const buttonsBody = h('div');
    function renderButtons() {
      buttonsBody.replaceChildren();
      draft.buttons.forEach((b, i) => {
        const url = h('input.input', {
          value: b.url, placeholder: 'https://…', spellcheck: false, maxLength: 512,
          oninput: (e) => { change((d) => { d.buttons[i].url = e.target.value; }); check(); },
        });
        const check = () => url.classList.toggle('bad', !!url.value.trim() && !/^https?:\/\/[^\s.]+\.[^\s]+$/i.test(url.value.trim()));
        check();
        buttonsBody.append(h('div.button-row', null,
          h('input.input', { value: b.label, placeholder: 'Label', maxLength: 32, oninput: (e) => change((d) => { d.buttons[i].label = e.target.value; }) }),
          url,
          h('button.icon-btn', { type: 'button', title: 'Remove', onclick: () => { change((d) => d.buttons.splice(i, 1)); renderButtons(); } }, icon('x'))));
      });
      if (draft.buttons.length < 2) {
        buttonsBody.append(h('button.btn.ghost', { type: 'button', style: { marginTop: draft.buttons.length ? '10px' : '0', paddingLeft: '8px' },
          onclick: () => { change((d) => d.buttons.push({ label: '', url: '' })); renderButtons(); buttonsBody.querySelector('.button-row:last-of-type input')?.focus(); } },
        icon('plus'), draft.buttons.length ? 'Add a second button' : 'Add a button'));
      }
    }
    const buttonsCard = h('section.card', null,
      h('h3.card-title', null, 'Buttons', h('span.spacer'), h('span.hint', null, 'Only your friends see them')),
      buttonsBody);
    renderButtons();

    // Member list and hotkey
    const listSeg = segmented(LIST_SHOWS, draft.listShows, (v) => change((d) => { d.listShows = v; }));
    const extrasCard = h('section.card', null,
      h('h3.card-title', null, 'More'),
      field('Member list shows', listSeg, { hint: 'What appears under your name in server member lists and DMs.' }),
      field('Hotkey to show this preset', h('div', null, hotkeyInput(draft.hotkey, (hk) => change((d) => { d.hotkey = hk; }, { quiet: true })))));

    // ---------- preview ----------
    const stageBox = h('div');
    const preview = window.preview.mount(stageBox);
    const stage = h('div.stage', null,
      h('div.stage-head', null, 'Preview'),
      stageBox,
      h('p.stage-note', null, h('b', null, 'Buttons are hidden on your own profile. '), 'Discord only shows them to others, so check with a friend.'));

    function ctx() {
      const s = store.snapshot.state;
      return {
        user: store.snapshot.user, app, appId: store.appIdFor(draft),
        live: s.live, isActive: s.activePresetId === draft.id, liveSince: s.liveSinceMs, shownSince: s.shownSinceMs, openedAt,
      };
    }
    function refreshPreview() { preview.update(draft, ctx()); }

    async function lookupApp() {
      const id = store.appIdFor(draft);
      if (!id) {
        app = null;
        appLine.replaceChildren(icon('warn'), h('span', null, 'Add your Discord application ID in ', h('a.link', { onclick: () => store.show('settings') }, 'Settings'), ' to go live.'));
        refreshPreview();
        return;
      }
      const result = await store.lookup(id);
      if (store.appIdFor(draft) !== id) return;
      app = result;
      const verb = kindOf(draft.kind).verb;
      if (app?.found === false) {
        appLine.replaceChildren(icon('warn'), h('span', null, 'Discord doesn\'t know the application ', h('code', null, id), '.'));
      } else {
        appLine.replaceChildren(app?.iconUrl ? h('img', { src: app.iconUrl, alt: '' }) : icon('sparkle'),
          h('span', null, 'Shows as ', h('b', null, `${verb} ${app?.name || '…'}`)));
      }
      renderAssets();
      largeField.paint();
      smallField.paint();
      refreshPreview();
    }

    function paintVerb() {
      const b = appLine.querySelector('b');
      if (b && app) b.textContent = `${kindOf(draft.kind).verb} ${app.name || '…'}`;
    }

    const form = h('div.form', null, activityCard, textCard, picturesCard, timerCard, partyCard, buttonsCard, extrasCard);
    const body = h('div.view-body', null, h('div.editor', null, form, stage));
    const view = h('section.view', null, head, body);
    main.replaceChildren(view);
    refreshHeader();
    lookupApp();
    refreshPreview();

    const ticker = setInterval(() => preview.tick(), 1000);

    return {
      id: draft.id,
      /** New data from C#: what's live changed, or a preset was edited elsewhere. */
      refresh(oldSettings) {
        refreshHeader();
        if (oldSettings && oldSettings.appId !== store.snapshot.settings.appId) lookupApp();
        else refreshPreview();
      },
      leave() {
        save.pending() && save.flush();
        clearInterval(ticker);
      },
      focusName() { nameInput.focus(); nameInput.select(); },
      draft: () => draft,
    };
  }

  window.editor = { open };
})();
