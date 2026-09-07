export default function (view) {
  function ensureCss() {
    if (document.getElementById('jellyspot-css')) return;
    var link = document.createElement('link');
    link.id = 'jellyspot-css';
    link.rel = 'stylesheet';
    link.href = ApiClient.getUrl('web/ConfigurationPage') + '?name=JellySpotCss';
    document.head.appendChild(link);
  }

  function setStatus(msg) {
    view.querySelector('#jellyspotBrowseStatus').textContent = msg || '';
  }

  function coverOf(item) {
    if (item.CoverUrl) return item.CoverUrl;
    if (item.ImageUrl) return item.ImageUrl;
    if (item.album && item.album.images && item.album.images[0]) return item.album.images[0].url;
    if (item.images && item.images[0]) return item.images[0].url;
    return '';
  }

  function renderCards(items, kind) {
    var root = view.querySelector('#jellyspotBrowseResults');
    root.innerHTML = '';
    items.forEach(function (item) {
      var id = item.Id || item.id;
      var name = item.Name || item.name || 'Untitled';
      var meta = '';
      if (kind === 'track') {
        meta = (item.Artists || (item.artists || []).map(function (a) { return a.name; })).toString();
      } else if (kind === 'playlist') {
        meta = (item.TrackCount || item.tracks && item.tracks.total || 0) + ' tracks';
      } else if (kind === 'album') {
        meta = ((item.artists || []).map(function (a) { return a.name; })).join(', ');
      }

      var card = document.createElement('div');
      card.className = 'jellyspot-item';
      var img = coverOf(item);
      card.innerHTML =
        (img ? '<img src="' + img + '" alt="" />' : '<div style="aspect-ratio:1;background:#333;border-radius:8px;"></div>') +
        '<strong>' + name + '</strong>' +
        '<div class="meta">' + meta + '</div>' +
        '<button is="emby-button" type="button" class="raised button-submit"><span>Download / Monitor</span></button>';
      card.querySelector('button').addEventListener('click', function () {
        queueItem(kind, id, name);
      });
      root.appendChild(card);
    });
  }

  function queueItem(kind, id, name) {
    Dashboard.showLoadingMsg();
    var req;
    if (kind === 'track') {
      req = ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('JellySpot/Queue/Tracks'),
        data: JSON.stringify({ TrackIds: [id], PlaylistName: 'Manual' }),
        contentType: 'application/json'
      });
    } else if (kind === 'playlist') {
      req = ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('JellySpot/Queue/Playlist/' + encodeURIComponent(id)),
        contentType: 'application/json'
      });
    } else if (kind === 'album') {
      req = ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('JellySpot/Queue/Album/' + encodeURIComponent(id)),
        contentType: 'application/json'
      });
    } else if (kind === 'liked') {
      req = ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('JellySpot/Queue/Liked'),
        contentType: 'application/json'
      });
    }

    req.then(function (res) {
      Dashboard.hideLoadingMsg();
      setStatus('Queued ' + (res && res.Queued != null ? res.Queued : name));
    }).catch(function (err) {
      Dashboard.hideLoadingMsg();
      setStatus((err && err.message) || 'Queue failed — link Spotify in JellySpot Sync first.');
    });
  }

  function search() {
    var q = view.querySelector('#jellyspotSearchInput').value.trim();
    if (!q) return;
    Dashboard.showLoadingMsg();
    ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('JellySpot/Search?q=' + encodeURIComponent(q) + '&type=track,album,playlist&limit=10'),
      dataType: 'json'
    }).then(function (data) {
      Dashboard.hideLoadingMsg();
      var cards = [];
      var kinds = [];
      if (data.tracks && data.tracks.items) {
        data.tracks.items.forEach(function (t) { cards.push(t); kinds.push('track'); });
      }
      if (data.albums && data.albums.items) {
        data.albums.items.forEach(function (a) { cards.push(a); kinds.push('album'); });
      }
      if (data.playlists && data.playlists.items) {
        data.playlists.items.forEach(function (p) { cards.push(p); kinds.push('playlist'); });
      }

      // Normalize Spotify search JSON casing
      var root = view.querySelector('#jellyspotBrowseResults');
      root.innerHTML = '';
      for (var i = 0; i < cards.length; i++) {
        (function (item, kind) {
          var id = item.id;
          var name = item.name;
          var meta = kind;
          var img = coverOf(item);
          var card = document.createElement('div');
          card.className = 'jellyspot-item';
          card.innerHTML =
            (img ? '<img src="' + img + '" alt="" />' : '<div style="aspect-ratio:1;background:#333;border-radius:8px;"></div>') +
            '<strong>' + name + '</strong>' +
            '<div class="meta">' + meta + '</div>' +
            '<button is="emby-button" type="button" class="raised button-submit"><span>Download / Monitor</span></button>';
          card.querySelector('button').addEventListener('click', function () {
            queueItem(kind, id, name);
          });
          root.appendChild(card);
        })(cards[i], kinds[i]);
      }
      setStatus(cards.length + ' results');
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      setStatus('Search failed. Link Spotify under JellySpot Sync.');
    });
  }

  view.querySelector('#jellyspotSearchBtn').addEventListener('click', search);
  view.querySelector('#jellyspotSearchInput').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') search();
  });

  view.querySelector('#jellyspotLoadPlaylistsBtn').addEventListener('click', function () {
    Dashboard.showLoadingMsg();
    ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('JellySpot/Playlists'),
      dataType: 'json'
    }).then(function (playlists) {
      Dashboard.hideLoadingMsg();
      renderCards(playlists, 'playlist');
      setStatus(playlists.length + ' playlists');
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      setStatus('Could not load playlists.');
    });
  });

  view.querySelector('#jellyspotLikedBtn').addEventListener('click', function () {
    queueItem('liked', 'liked', 'Liked Songs');
  });

  view.addEventListener('viewshow', function () {
    ensureCss();
  });
}
