// The live preview: a Discord profile popout and member list row showing the preset as your friends see it.
(() => {
  const { h, icon, clock, nextTimeOfDay, kindOf } = window.ui;

  // A picture as Discord would show it: an https link as is, an asset key from the app's uploaded art.
  function imageUrl(value, app) {
    const v = (value || '').trim();
    if (!v) return null;
    if (/^https:\/\//i.test(v)) return v;
    if (/^[a-z]+:\/\//i.test(v)) return null;
    const key = v.toLowerCase();
    if (app?.assets?.[key]) return app.assets[key];
    // Flair's own app has the sparkle uploaded as "flair"; Discord's public list can lag behind an upload
    if (key === 'flair' && app?.id && app.id === window.preview.defaultAppId) return 'flair-art.png';
    return null;
  }

  function avatar(user, size) {
    if (user?.avatarUrl) return h('img', { src: user.avatarUrl, alt: '', width: size, height: size, referrerPolicy: 'no-referrer' });
    return h('div.face', null, (user?.displayName || 'Y')[0].toUpperCase());
  }

  let tip = null;
  function withTip(el, text) {
    if (!text) return el;
    el.addEventListener('mouseenter', () => {
      tip?.remove();
      tip = h('div.dc-tip.dc', null, text);
      document.body.append(tip);
      const r = el.getBoundingClientRect();
      tip.style.left = Math.max(8, r.left + r.width / 2 - tip.offsetWidth / 2) + 'px';
      tip.style.top = r.top - tip.offsetHeight - 8 + 'px';
    });
    el.addEventListener('mouseleave', () => { tip?.remove(); tip = null; });
    return el;
  }

  /** Times for the timer line, or null when the preset has no timer. */
  function timing(p, ctx, now) {
    const liveSince = ctx.live && ctx.isActive && ctx.liveSince ? ctx.liveSince : ctx.openedAt;
    const shownSince = ctx.live && ctx.isActive && ctx.shownSince ? ctx.shownSince : ctx.openedAt;
    switch (p.timer) {
      case 'elapsed': return { start: liveSince };
      case 'countdown': return { start: shownSince, end: shownSince + p.countdownMinutes * 60000 };
      case 'until': return { start: shownSince, end: nextTimeOfDay(p.untilTime, new Date(now)) };
      default: return null;
    }
  }

  /** Builds the preview into `root` and returns { update(preset, ctx), tick() }. */
  function mount(root) {
    const card = h('div.dc.dc-backdrop');
    root.append(card);
    let preset = null, ctx = {};
    let timerEl = null, progress = null;

    function render() {
      card.replaceChildren();
      tip?.remove();
      if (!preset) return;
      const p = preset;
      const app = ctx.app || {};
      const user = ctx.user;
      const kind = kindOf(p.kind);
      const appName = app.name || (ctx.appId ? 'Your app' : 'Your app name');
      const large = imageUrl(p.largeImage, app);
      const small = imageUrl(p.smallImage, app);
      const details = (p.details || '').trim();
      const state = (p.state || '').trim();
      const party = p.partyMax > 0 && state ? ` (${Math.max(1, p.partySize)} of ${p.partyMax})` : '';
      const buttons = (p.buttons || []).filter((b) => b.label.trim() && /^https?:\/\//i.test(b.url.trim()));

      // popout
      const art = h('div.dc-art', null,
        withTip(large ? h('img.large', { src: large, alt: '', referrerPolicy: 'no-referrer', onerror: (e) => e.target.replaceWith(placeholder(kind)) })
          : placeholder(kind), p.largeText),
        small ? withTip(h('img.small', { src: small, alt: '', referrerPolicy: 'no-referrer', onerror: (e) => e.target.remove() }), p.smallText) : null);
      timerEl = null;
      progress = null;
      const t = timing(p, ctx, Date.now());
      const lines = h('div.dc-lines', null,
        h('div.dc-title', null, appName),
        details ? h('div.dc-line', null, details) : null,
        state ? h('div.dc-line', null, state + party) : null);
      // Listening and watching with an end time get Discord's progress bar, everything else the green timer.
      const bar = t && t.end && (p.kind === 'listening' || p.kind === 'watching');
      if (t && !bar) {
        timerEl = h('div.dc-line.timer', null, icon(p.kind === 'playing' ? 'gamepad' : 'clock'), h('span'));
        lines.append(timerEl);
      }
      const activity = h('div.dc-activity' + (ctx.live && ctx.isActive ? '' : ''), null,
        h('div.dc-kind', null, kind.verb, h('span.more', null, '···')),
        h('div.dc-row', null, art, lines));
      if (bar) {
        progress = { fill: h('div.fill'), a: h('span'), b: h('span') };
        activity.append(h('div.dc-progress', null, h('div.track', null, progress.fill), h('div.times', null, progress.a, progress.b)));
      }
      if (buttons.length) {
        activity.append(h('div.dc-buttons', null, buttons.map((b) =>
          h('div.dc-button', { title: b.url, onclick: ctx.shared ? null : () => window.flair.call('link.open', { url: b.url.trim() }).catch(() => {}) }, b.label))));
      }

      const banner = h('div.dc-banner');
      const name = user?.displayName || 'You';
      card.append(...[
        h('div.dc-popout', null,
          banner,
          h('div.dc-head', null, h('div.dc-avatar', null, avatar(user, 80), h('span.status'))),
          h('div.dc-body', null,
            h('div.dc-name', null, name),
            h('div.dc-user', null, user?.userName ? user.userName : 'your profile'),
            activity)),
        ctx.shared ? null : h('div.dc-member', null,
          h('div.pic', null, avatar(user, 32)),
          h('div.who', null,
            h('div.n', null, name),
            h('div.a', null, kind.verb + ' ', h('b', null, listName(p, appName, details, state)))),
          h('span.icon', null, icon(kind.icon)))].filter(Boolean));
      tick();
    }

    function listName(p, appName, details, state) {
      if (p.listShows === 'details' && details) return details;
      if (p.listShows === 'state' && state) return state;
      return appName;
    }

    function placeholder(kind) {
      return h('div.large.placeholder', null, icon(kind.icon));
    }

    function tick() {
      if (!preset) return;
      const now = Date.now();
      const t = timing(preset, ctx, now);
      if (!t) return;
      if (timerEl) {
        const span = timerEl.querySelector('span');
        span.textContent = t.end ? clock(t.end - now) + ' left' : clock(now - t.start);
      }
      if (progress && t.end) {
        const total = t.end - t.start, done = Math.min(total, Math.max(0, now - t.start));
        progress.fill.style.width = (total > 0 ? (done / total) * 100 : 0) + '%';
        progress.a.textContent = clock(done);
        progress.b.textContent = clock(total);
      }
    }

    return {
      update(p, c) {
        preset = p;
        ctx = c;
        render();
      },
      tick,
    };
  }

  window.preview = { mount, imageUrl };
})();
