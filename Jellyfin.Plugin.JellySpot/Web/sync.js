export default function (view) {
  function ensureCss() {
    if (document.getElementById('jellyspot-css')) return;
    var link = document.createElement('link');
    link.id = 'jellyspot-css';
    link.rel = 'stylesheet';
    link.href = ApiClient.getUrl('web/ConfigurationPage') + '?name=JellySpotCss';
    document.head.appendChild(link);
  }

  function splitCsv(value) {
    return (value || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean);
  }

  function load() {
    Dashboard.showLoadingMsg();
    Promise.all([
      ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Me'), dataType: 'json' }),
      ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('JellySpot/Playlists'), dataType: 'json' }).catch(function () { return []; })
    ]).then(function (results) {
      var me = results[0];
      var playlists = results[1] || [];
      var settings = me.Settings || {};
      view.querySelector('#jellyspotLinkStatus').textContent = me.Linked
        ? ('Linked as ' + (me.SpotifyDisplayName || me.SpotifyUserId))
        : 'Spotify is not linked';
      view.querySelector('#jellyspotEnabled').checked = settings.Enabled !== false;
      view.querySelector('#jellyspotLiked').checked = !!settings.SyncLikedSongs;
      view.querySelector('#jellyspotInclude').value = (settings.IncludeArtistFilters || []).join(', ');
      view.querySelector('#jellyspotExclude').value = (settings.ExcludeArtistFilters || []).join(', ');
      view.querySelector('#jellyspotSyncMeta').textContent =
        'Last sync: ' + (settings.LastSyncUtc || 'never') +
        (settings.LastSyncStatus ? ' (' + settings.LastSyncStatus + ')' : '') +
        (settings.LastError ? ' — ' + settings.LastError : '');

      var box = view.querySelector('#jellyspotMonitoredPlaylists');
      box.innerHTML = '';
      var monitored = settings.MonitoredPlaylistIds || [];
      playlists.forEach(function (p) {
        var id = p.Id || p.id;
        var label = document.createElement('label');
        label.className = 'checkboxContainer';
        label.style.display = 'block';
        label.innerHTML = '<input type="checkbox" data-playlist-id="' + id + '" ' +
          (monitored.indexOf(id) >= 0 ? 'checked' : '') + ' /> <span>' + (p.Name || p.name) + '</span>';
        box.appendChild(label);
      });
      Dashboard.hideLoadingMsg();
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      view.querySelector('#jellyspotLinkStatus').textContent = 'Unable to load status. Configure Spotify Client ID in admin settings first.';
    });
  }

  view.querySelector('#jellyspotLinkBtn').addEventListener('click', function () {
    ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('JellySpot/OAuth/Start'),
      dataType: 'json'
    }).then(function (res) {
      window.open(res.Url, '_blank', 'noopener');
      Dashboard.alert('Complete linking in the Spotify window, then reload this page.');
    }).catch(function () {
      Dashboard.alert('Could not start OAuth. Check admin Client ID / redirect URI.');
    });
  });

  view.querySelector('#jellyspotUnlinkBtn').addEventListener('click', function () {
    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('JellySpot/OAuth/Unlink') }).then(load);
  });

  view.querySelector('#jellyspotSyncNowBtn').addEventListener('click', function () {
    Dashboard.showLoadingMsg();
    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('JellySpot/Sync/Now') }).then(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Sync started. Check JellySpot Queue for progress.');
      load();
    }).catch(function (err) {
      Dashboard.hideLoadingMsg();
      Dashboard.alert((err && err.message) || 'Sync failed');
    });
  });

  view.querySelector('#jellyspotSaveSettingsBtn').addEventListener('click', function () {
    var playlistIds = [];
    view.querySelectorAll('#jellyspotMonitoredPlaylists input[type=checkbox]').forEach(function (cb) {
      if (cb.checked) playlistIds.push(cb.getAttribute('data-playlist-id'));
    });
    var body = {
      Enabled: view.querySelector('#jellyspotEnabled').checked,
      SyncLikedSongs: view.querySelector('#jellyspotLiked').checked,
      PlaylistIds: playlistIds,
      IncludeArtistFilters: splitCsv(view.querySelector('#jellyspotInclude').value),
      ExcludeArtistFilters: splitCsv(view.querySelector('#jellyspotExclude').value)
    };
    Dashboard.showLoadingMsg();
    ApiClient.ajax({
      type: 'POST',
      url: ApiClient.getUrl('JellySpot/Settings'),
      data: JSON.stringify(body),
      contentType: 'application/json'
    }).then(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Settings saved');
      load();
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Failed to save');
    });
  });

  view.addEventListener('viewshow', function () {
    ensureCss();
    load();
  });
}
