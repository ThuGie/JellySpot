const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require(process.env.JELLYSPOT_JSDOM || 'jsdom');
const base = path.resolve(__dirname, '..');
const tick = () => new Promise(resolve => setTimeout(resolve, 15));
(async () => {
    // Dashboard boots and navigates with no personal account APIs available.
    const config = new JSDOM(fs.readFileSync(path.join(base, 'Jellyfin.Plugin.JellySpot/Configuration/config.html'), 'utf8'), { runScripts: 'outside-only', url: 'http://server/web/' });
    const w = config.window, calls = [];
    w.ApiClient = { getUrl: p => '/' + p, getPluginConfiguration: async () => ({}), updatePluginConfiguration: async () => ({}), ajax: async o => {
        calls.push(o.url);
        if (o.url.includes('admin/health')) return { libraries: [], ffmpegReady: true, storageWritable: true, fileTransformation: true, legacyPlaylistExports: ['/music/Playlists/old.m3u8'] };
        if (o.url.includes('admin/Queue')) return [];
        throw new Error('Unexpected account-dependent dashboard request: ' + o.url);
    } };
    w.Dashboard = new Proxy({}, {get: () => () => {}});
    for (const script of w.document.scripts) w.eval(script.textContent);
    await tick();
    assert.equal(w.document.querySelectorAll('.jellyspot-tab').length, 3);
    assert.equal(w.document.querySelector('[data-tab="sync"]'), null);
    assert.equal(w.document.querySelector('[data-tab="browse"]'), null);
    w.document.querySelector('[data-tab="health"]').click(); await tick();
    assert.match(w.document.querySelector('#jellyspotLegacyExports').textContent, /old.m3u8/);
    w.document.querySelector('[data-tab="queue"]').click(); await tick();
    assert(calls.some(p => p.includes('admin/Queue')));
    assert(calls.every(p => p.includes('/admin/')));
    config.window.close();

    // Execute actual injected UI in a DOM with a fake authenticated Jellyfin API.
    const dom = new JSDOM('<body><div id="root"></div></body>', { runScripts: 'outside-only', url: 'http://server/web/#/home.html' });
    const v = dom.window; let user = 'user1', shared = true, followed = false, played = null, owned = false;
    v.console = { log() {}, warn() {}, error() {} };
    const data = () => ({ Id: 'collection', Name: '<Private title>', Shared: shared, Owned: owned, Following: followed, OwnerName: 'Owner', Tracks: [{ Id: 'track1', Name: 'Song', Artists: ['Artist'], ItemId: 'audio1' }, { Id: 'missing', Name: 'Missing', Artists: [], ItemId: null }] });
    v.ApiClient = { serverId: () => 'server', getCurrentUserId: () => user, getUrl: p => '/' + p,
        ajax: async o => {
            if (o.url.endsWith('/Me')) return { Linked: false };
            if (o.url.endsWith('/Collections?view=shared') || o.url.endsWith('/Collections?view=following')) return shared ? [{ ...data(), TrackCount: 2 }] : [];
            if (o.url.endsWith('/Collections/collection/Sharing')) { shared = JSON.parse(o.data).Shared; return; }
            if (o.url.endsWith('/Collections/collection/Follow')) { followed = o.type === 'POST'; return; }
            if (o.url.endsWith('/Collections/collection')) { if (!shared && !owned) throw Error('private'); return data(); }
            if (o.url.includes('/Queue')) return [];
            throw Error('Unexpected Spotify request for unlinked user: ' + o.url);
        } };
    // Simulate Jellyfin's registered item container handling native playback actions.
    v.document.addEventListener('click', event => {
        const action = event.target.closest('.itemAction[data-action="playallfromhere"]');
        if (action) { played = { ids: [...action.parentElement.querySelectorAll('[data-id]')].map(el => el.dataset.id) }; event.preventDefault(); }
    });
    v.eval(fs.readFileSync(path.join(base, 'Jellyfin.Plugin.JellySpot/Inject/jellyspot-tabs.js'), 'utf8'));
    const plugin = v.jellySpotPlugin; plugin.init = () => {}; plugin.notify = (_, message) => { plugin.message = message; };
    const root = v.document.querySelector('#root'); plugin.renderBrowse(root); await tick();
    assert(root.textContent.includes('Shared Playlists')); assert(root.textContent.includes('Open playlist')); assert.equal(root.querySelector('private'), null);
    root.querySelector('.jellyspot-collection-card button').click(); await tick();
    assert(root.textContent.includes('Unavailable'));
    [...root.querySelectorAll('button')].find(b => b.textContent === 'Follow playlist').click(); await tick();
    assert(followed); assert(root.textContent.includes('Unfollow'));
    [...root.querySelectorAll('button')].find(b => b.textContent === 'Play all available').click(); await tick();
    assert.deepEqual(Array.from(played.ids), ['audio1']);
    shared = false; played = null; plugin.playCollection(root, 'collection'); await tick();
    assert.equal(played, null); assert.match(plugin.message, /private/);
    owned = true; plugin.openCollection(root, 'collection', 'mine'); await tick();
    [...root.querySelectorAll('button')].find(b => b.textContent === 'Share playlist').click(); await tick();
    assert(shared); assert(root.textContent.includes('Make private'));
    [...root.querySelectorAll('button')].find(b => b.textContent === 'Make private').click(); await tick();
    assert.equal(shared, false); assert(root.textContent.includes('Share playlist'));
    // Account changes clear mounted personal DOM and timers.
    user = 'user2'; plugin.checkAccountIdentity();
    assert.equal(root.textContent, '');
    dom.window.close();
    console.log('UI checks passed: dashboard without Spotify; unlinked shared browsing; follow; playback filtering; unshare denial; account cleanup.');
})().catch(e => { console.error(e); process.exitCode = 1; });
