// Design preview only: when the page is opened in a normal browser instead of Flair, this stands in for the C# side.
(() => {
  if (window.chrome && window.chrome.webview) return;
  const id = () => Math.random().toString(16).slice(2, 14);
  const now = Date.now();
  const presets = [
    { id: id(), name: 'Locked in', appId: '', kind: 'playing', details: 'Ranked grind', state: 'Looking for a duo', largeImage: 'https://picsum.photos/seed/flair1/256', largeText: 'Season 4', smallImage: 'https://picsum.photos/seed/flair2/64', smallText: 'Diamond II', buttons: [{ label: 'Watch my clips', url: 'https://youtube.com' }, { label: 'Add me', url: 'https://steamcommunity.com' }], timer: 'elapsed', countdownMinutes: 60, untilTime: '20:00', partySize: 2, partyMax: 5, listShows: 'name', hotkey: { key: 'd1', ctrl: true, alt: true } },
    { id: id(), name: 'Making beats', appId: '', kind: 'listening', details: 'Cooking a new beat', state: 'FL Studio, 140 BPM', largeImage: 'https://picsum.photos/seed/flair3/256', largeText: '', smallImage: '', smallText: '', buttons: [], timer: 'countdown', countdownMinutes: 45, untilTime: '20:00', partySize: 0, partyMax: 0, listShows: 'details', hotkey: { key: 0 } },
    { id: id(), name: 'Back at 8', appId: '', kind: 'watching', details: 'Away for a bit', state: 'Back soon', largeImage: '', largeText: '', smallImage: '', smallText: '', buttons: [], timer: 'until', countdownMinutes: 60, untilTime: '20:00', partySize: 0, partyMax: 0, listShows: 'name', hotkey: { key: 0 } },
  ];
  const settings = {
    appId: '1306512345678901234', live: true, activePresetId: presets[0].id, liveSinceMs: now - 1000 * 60 * 47, rotateEnabled: false,
    rotatePresetIds: [presets[0].id, presets[1].id], rotateSeconds: 60, rotateShuffle: false,
    toggleHotkey: { key: 'f', ctrl: true, alt: true }, nextHotkey: { key: 'n', ctrl: true, alt: true }, startWithWindows: true, checkForUpdates: true, guideSeen: true,
  };
  const state = () => ({
    live: settings.live, activePresetId: settings.activePresetId, liveSinceMs: settings.liveSinceMs, shownSinceMs: now - 1000 * 60 * 5,
    discord: { state: settings.live ? 'live' : 'off', error: null }, rotating: settings.live && settings.rotateEnabled && settings.rotatePresetIds.length > 1,
    nextRotateMs: Date.now() + 42000,
  });
  const user = { id: '80351110224678912', userName: 'slashneck', globalName: 'slash', displayName: 'slash', avatarUrl: 'https://cdn.discordapp.com/embed/avatars/2.png' };
  const snapshot = () => ({ state: state(), user, presets: structuredClone(presets), settings: structuredClone(settings), defaultAppId: '1572290055270310030' });
  let emit = () => {};
  const changed = () => setTimeout(() => emit('state', snapshot()), 30);

  const handlers = {
    'app.init': () => ({ snapshot: snapshot(), version: '1.0.0', update: { state: 'latest', current: '1.0.0' }, dev: true }),
    'app.lookup': ({ appId }) => ({ id: appId, name: 'my lil studio', iconUrl: null, assets: {}, found: true }),
    'preset.create': () => { const p = { ...structuredClone(presets[2]), id: id(), name: 'New status', details: '', state: '', timer: 'elapsed', hotkey: { key: 0 } }; presets.push(p); changed(); return p; },
    'preset.update': ({ preset }) => { const i = presets.findIndex((p) => p.id === preset.id); if (i >= 0) presets[i] = structuredClone(preset); changed(); return preset; },
    'preset.duplicate': ({ id: pid }) => { const s = presets.find((p) => p.id === pid); const c = { ...structuredClone(s), id: id(), name: s.name + ' copy' }; presets.splice(presets.indexOf(s) + 1, 0, c); changed(); return c; },
    'preset.delete': ({ id: pid }) => { presets.splice(presets.findIndex((p) => p.id === pid), 1); changed(); },
    'preset.reorder': ({ ids }) => { presets.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id)); changed(); },
    'preset.goLive': ({ id: pid }) => { if (!settings.live) settings.liveSinceMs = Date.now(); settings.live = true; settings.activePresetId = pid; changed(); },
    'status.setLive': ({ live }) => { settings.live = live; if (live) settings.liveSinceMs = Date.now(); changed(); },
    'rotation.set': (p) => { settings.rotateEnabled = p.enabled; settings.rotatePresetIds = p.ids; settings.rotateSeconds = p.seconds; settings.rotateShuffle = p.shuffle; changed(); },
    'settings.save': ({ settings: s }) => { Object.assign(settings, s); changed(); return settings; },
    'hotkeys.pause': () => null,
    'presets.export': () => 'C:\\Users\\you\\Desktop\\Locked in.flair',
    'presets.import': () => null,
    'link.open': ({ url }) => { console.log('open', url); },
    'update.check': () => ({ state: 'latest', current: '1.0.0' }),
  };

  window.FlairMock = {
    call(method, params, emitter) {
      emit = emitter;
      const fn = handlers[method];
      if (!fn) return Promise.reject(new Error('Mock has no ' + method));
      try { return Promise.resolve(fn(params || {})); } catch (e) { return Promise.reject(e); }
    },
  };
})();
