export default function (view) {
  view.addEventListener('viewshow', function () {
    Dashboard.showLoadingMsg();
    ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('JellySpot/Configuration'),
      dataType: 'json'
    }).then(function (config) {
      view.querySelector('#storageRootPath').value = config.StorageRootPath || '';
      view.querySelector('#spotifyClientId').value = config.SpotifyClientId || '';
      view.querySelector('#spotifyClientSecret').value = config.SpotifyClientSecret || '';
      view.querySelector('#spotifyRedirectUri').value = config.SpotifyRedirectUri || '';
      view.querySelector('#spotifyRps').value = config.SpotifyRequestsPerSecond ?? 2;
      view.querySelector('#downloadConcurrency').value = config.DownloadConcurrency ?? 2;
      view.querySelector('#preferredFormat').value = config.PreferredFormat || 'm4a';
      view.querySelector('#minMatchScore').value = config.MinMatchScore ?? 80;
      view.querySelector('#syncIntervalMinutes').value = config.SyncIntervalMinutes ?? 60;
      view.querySelector('#ffmpegPath').value = config.FfmpegPath || '';
      view.querySelector('#triggerLibraryRefresh').checked = !!config.TriggerLibraryRefresh;
      Dashboard.hideLoadingMsg();
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Failed to load JellySpot configuration');
    });
  });

  view.querySelector('#jellyspotConfigForm').addEventListener('submit', function (e) {
    e.preventDefault();
    Dashboard.showLoadingMsg();
    var body = {
      StorageRootPath: view.querySelector('#storageRootPath').value,
      SpotifyClientId: view.querySelector('#spotifyClientId').value,
      SpotifyClientSecret: view.querySelector('#spotifyClientSecret').value,
      SpotifyRedirectUri: view.querySelector('#spotifyRedirectUri').value,
      SpotifyRequestsPerSecond: Number(view.querySelector('#spotifyRps').value),
      DownloadConcurrency: Number(view.querySelector('#downloadConcurrency').value),
      PreferredFormat: view.querySelector('#preferredFormat').value,
      MinMatchScore: Number(view.querySelector('#minMatchScore').value),
      SyncIntervalMinutes: Number(view.querySelector('#syncIntervalMinutes').value),
      FfmpegPath: view.querySelector('#ffmpegPath').value,
      TriggerLibraryRefresh: view.querySelector('#triggerLibraryRefresh').checked,
      SpotifyMaxConcurrentRequests: 2,
      CacheTtlDays: 10
    };
    ApiClient.ajax({
      type: 'POST',
      url: ApiClient.getUrl('JellySpot/Configuration'),
      data: JSON.stringify(body),
      contentType: 'application/json'
    }).then(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Settings saved');
    }).catch(function () {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Failed to save settings');
    });
  });
}
