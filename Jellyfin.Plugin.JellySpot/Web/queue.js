export default function (view) {
  function ensureCss() {
    if (document.getElementById('jellyspot-css')) return;
    var link = document.createElement('link');
    link.id = 'jellyspot-css';
    link.rel = 'stylesheet';
    link.href = ApiClient.getUrl('web/ConfigurationPage') + '?name=JellySpotCss';
    document.head.appendChild(link);
  }

  function load() {
    var status = view.querySelector('#jellyspotQueueFilter').value;
    var url = ApiClient.getUrl('JellySpot/Queue') + (status ? ('?status=' + encodeURIComponent(status)) : '');
    Dashboard.showLoadingMsg();
    ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (items) {
      var tbody = view.querySelector('#jellyspotQueueTable tbody');
      tbody.innerHTML = '';
      (items || []).forEach(function (item) {
        var tr = document.createElement('tr');
        tr.innerHTML =
          '<td><strong>' + (item.Title || '') + '</strong><div class="fieldDescription">' + (item.Artists || '') + '</div></td>' +
          '<td><span class="jellyspot-status ' + (item.Status || '') + '">' + (item.Status || '') + '</span></td>' +
          '<td>' + (item.MatchScore != null ? Number(item.MatchScore).toFixed(1) : '-') + '</td>' +
          '<td>' + (item.Error || item.YoutubeVideoId || '') + '</td>' +
          '<td></td>';
        if (item.Status === 'Failed' || item.Status === 'Completed') {
          var btn = document.createElement('button');
          btn.setAttribute('is', 'emby-button');
          btn.className = 'raised';
          btn.type = 'button';
          btn.innerHTML = '<span>Rematch</span>';
          btn.addEventListener('click', function () {
            ApiClient.ajax({
              type: 'POST',
              url: ApiClient.getUrl('JellySpot/Queue/' + item.Id + '/Rematch')
            }).then(load);
          });
          tr.children[4].appendChild(btn);
        }
        tbody.appendChild(tr);
      });
      Dashboard.hideLoadingMsg();
    }).catch(function () {
      Dashboard.hideLoadingMsg();
    });
  }

  view.querySelector('#jellyspotQueueRefresh').addEventListener('click', load);
  view.querySelector('#jellyspotQueueFilter').addEventListener('change', load);
  view.addEventListener('viewshow', function () {
    ensureCss();
    load();
  });
}
