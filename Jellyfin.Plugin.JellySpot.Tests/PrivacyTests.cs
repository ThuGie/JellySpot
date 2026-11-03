using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellySpot.Tests;

public class PrivacyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyspot-tests", Guid.NewGuid().ToString("N"));
    private readonly JellySpotStore _store;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _viewer = Guid.NewGuid();
    private string Db => Path.Combine(_directory, "test.db");
    public PrivacyTests() { Directory.CreateDirectory(_directory); _store = Open(); }
    private JellySpotStore Open() => new(NullLogger<JellySpotStore>.Instance, Db);
    private UserCollection Collection(string source = "playlist") => new() { OwnerId = _owner, SourceId = source, Name = "Same name", Tracks = [new() { Id = "track", Name = "Song" }] };
    private QueueItem Job(Guid user, string status = "Pending") => new() { JellyfinUserId = user, SpotifyTrackId = "track", Status = status, PlaylistId = "private", PlaylistName = "Secret", RelativePath = "private/path" };
    private async Task Sql(string sql) { await using var c = new SqliteConnection($"Data Source={Db}"); await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }

    [Fact] public async Task PrivateByDefaultEvenWhenCallerRequestsShared()
    {
        var collection = Collection(); collection.Shared = true;
        await _store.SaveCollectionAsync(collection);
        Assert.Empty(await _store.GetCollectionsAsync(_viewer, "shared"));
        Assert.Null(await _store.GetCollectionAsync(collection.Id, _viewer));
        Assert.NotNull(await _store.GetCollectionAsync(collection.Id, _owner));
        Assert.False(await _store.SetSharingAsync(collection.Id, _viewer, true));
    }
    [Fact] public async Task ShareFollowUnshareReshareAndLiveUpdates()
    {
        var collection = Collection(); await _store.SaveCollectionAsync(collection);
        Assert.True(await _store.SetSharingAsync(collection.Id, _owner, true));
        Assert.True(await _store.FollowCollectionAsync(collection.Id, _viewer, true));
        collection.Tracks.Add(new() { Id = "second" }); collection.Name = "Renamed";
        await _store.SaveCollectionAsync(collection);
        Assert.Equal(2, (await _store.GetCollectionAsync(collection.Id, _viewer))!.Tracks.Count);
        Assert.Equal("Renamed", Assert.Single(await _store.GetCollectionsAsync(_viewer, "following")).Name);
        await _store.SetSharingAsync(collection.Id, _owner, false);
        Assert.Null(await _store.GetCollectionAsync(collection.Id, _viewer));
        Assert.Empty(await _store.GetCollectionsAsync(_viewer, "following"));
        await _store.SetSharingAsync(collection.Id, _owner, true);
        Assert.Single(await _store.GetCollectionsAsync(_viewer, "following"));
        await _store.FollowCollectionAsync(collection.Id, _viewer, false);
        Assert.Empty(await _store.GetCollectionsAsync(_viewer, "following"));
    }
    [Fact] public async Task EqualNamesAndLikedSongsDoNotCollide()
    {
        await _store.SaveCollectionAsync(Collection()); await _store.SaveCollectionAsync(Collection("liked"));
        var other = Collection(); other.OwnerId = _viewer; await _store.SaveCollectionAsync(other);
        Assert.Equal(2, (await _store.GetCollectionsAsync(_owner)).Count); Assert.Single(await _store.GetCollectionsAsync(_viewer));
    }
    [Fact] public async Task DeletedOwnersAndCollectionsBecomeUnavailable()
    {
        var c = Collection(); await _store.SaveCollectionAsync(c); await _store.SetSharingAsync(c.Id, _owner, true);
        Assert.False(await _store.DeleteCollectionAsync(c.Id, _viewer));
        await _store.RemoveMissingOwnersAsync(_ => false, default);
        Assert.Null(await _store.GetCollectionAsync(c.Id, _owner));
    }
    [Fact] public async Task ConcurrentRequestsDeduplicateAndKeepPersonalAssociations()
    {
        await Task.WhenAll(Enumerable.Range(0,20).Select(i => _store.EnqueueAsync(Job(i % 2 == 0 ? _owner : _viewer))));
        Assert.Single(await _store.GetQueueAsync());
        Assert.Single(await _store.GetUserQueueAsync(_owner, null)); Assert.Single(await _store.GetUserQueueAsync(_viewer, null));
        Assert.Empty(await _store.GetUserQueueAsync(Guid.NewGuid(), null));
        var personal = Assert.Single(await _store.GetUserQueueAsync(_viewer, null)); Assert.Null(personal.RelativePath); Assert.Null(personal.PlaylistId);
    }
    [Fact] public async Task RetryChecksOwnershipAndDoesNotResetCompletedOrActiveWork()
    {
        var job = Job(_owner,"Failed"); await _store.EnqueueAsync(job);
        Assert.False(await _store.RetryJobAsync(job.Id, _viewer));
        Assert.True(await _store.RetryJobAsync(job.Id, _owner)); Assert.Equal("Pending", (await _store.GetQueueItemAsync(job.Id))!.Status);
        job.Status = "Completed"; await _store.UpdateQueueItemAsync(job);
        await _store.RetryJobAsync(job.Id,_owner); Assert.Equal("Completed", (await _store.GetQueueItemAsync(job.Id))!.Status);
        await _store.EnqueueAsync(Job(_viewer)); Assert.Equal("Completed", (await _store.GetQueueItemAsync(job.Id))!.Status);
    }
    [Fact] public async Task AlreadyDownloadedRequestsAreRecordedWithoutPendingDownload()
    {
        Assert.Equal(EnqueueResult.AlreadyDownloaded, await _store.EnqueueAsync(Job(_owner, "Completed")));
        Assert.Equal("Completed", Assert.Single(await _store.GetUserQueueAsync(_owner,null)).Status);
    }
    [Fact] public async Task OAuthStateExpiresAndIsConsumedAtomically()
    {
        await _store.SaveOAuthStateAsync("expired",_owner,"verifier");
        await Sql("UPDATE oauth_state SET created_at='2000-01-01T00:00:00.0000000Z'");
        Assert.Null(await _store.TakeOAuthStateAsync("expired"));
        await _store.SaveOAuthStateAsync("valid",_owner,"verifier");
        var results = await Task.WhenAll(Enumerable.Range(0,10).Select(_ => _store.TakeOAuthStateAsync("valid")));
        Assert.Single(results, x => x.HasValue);
    }
    [Fact] public async Task UnlinkRemovesLegacyTokenAndOAuthAndCache()
    {
        await _store.SaveTokensAsync(_owner,new() { AccessToken="access" });
        await Sql($"UPDATE user_tokens SET jellyfin_user_id='{_owner:D}'");
        await _store.SaveOAuthStateAsync("pending",_owner,"verifier");
        await _store.SetCacheAsync(_owner.ToString("N")+":old", "secret", TimeSpan.FromDays(1));
        await _store.DeleteTokensAsync(_owner); await _store.ClearUserCacheAsync(_owner);
        Assert.Null(await _store.GetTokensAsync(_owner)); Assert.Null(await _store.TakeOAuthStateAsync("pending"));
        Assert.Null(await _store.GetCacheAsync(_owner.ToString("N")+":old"));
    }
    [Fact] public async Task RestartPreservesCollectionsAndRecoversJobs()
    {
        var c = Collection(); await _store.SaveCollectionAsync(c);
        var job = Job(_owner); await _store.EnqueueAsync(job); job.Status="Downloading"; await _store.UpdateQueueItemAsync(job);
        var reopened = Open(); Assert.NotNull(await reopened.GetCollectionAsync(c.Id,_owner));
        Assert.Equal("Pending",(await reopened.GetQueueItemAsync(job.Id))!.Status);
        Open(); Assert.Single(await _store.GetCollectionsAsync(_owner)); Assert.True(File.Exists(Db+".pre-privacy.bak"));
    }
    [Fact] public async Task MigrationNormalizesLegacyTokensAndSeedsPrivateCollections()
    {
        await _store.SaveTokensAsync(_owner,new() { AccessToken="preserved" });
        await _store.SaveUserSettingsAsync(new() { JellyfinUserId=_owner,SyncLikedSongs=true,MonitoredPlaylistIds=["playlist"] });
        await Sql($"PRAGMA user_version=0; UPDATE user_tokens SET jellyfin_user_id='{_owner:D}',json=json_remove(json,'$.Generation');");
        var reopened=Open(); var token=await reopened.GetTokensAsync(_owner);
        Assert.Equal("preserved",token!.AccessToken); Assert.Equal(token.Generation,(await reopened.GetTokensAsync(_owner))!.Generation);
        Assert.Equal(2,(await reopened.GetCollectionsAsync(_owner)).Count); Assert.Empty(await reopened.GetCollectionsAsync(_viewer,"shared"));
    }
    [Fact] public async Task LegacyDuplicateJobsRetainEveryKnownRequester()
    {
        var first = Job(_owner); await _store.EnqueueAsync(first);
        var second = Job(_viewer);
        var json = System.Text.Json.JsonSerializer.Serialize(second).Replace("'", "''");
        await Sql($"DROP INDEX ux_queue_spotify_track_id; PRAGMA user_version=0; INSERT INTO queue(id,json,status,updated_at,spotify_track_id) VALUES('{second.Id}','{json}','Pending','2099-01-01','track');");
        var reopened=Open(); Assert.Single(await reopened.GetQueueAsync());
        Assert.Single(await reopened.GetUserQueueAsync(_owner,null)); Assert.Single(await reopened.GetUserQueueAsync(_viewer,null));
    }

    [Fact] public async Task VerifiedMissingAudioCanReopenCompletedJobWithoutDuplicatingIt()
    {
        var job = Job(_owner,"Completed"); await _store.EnqueueAsync(job);
        Assert.Equal(EnqueueResult.Retried,await _store.EnqueueAsync(Job(_viewer),missingAudio:true));
        Assert.Equal("Pending",Assert.Single(await _store.GetQueueAsync()).Status);
        Assert.Single(await _store.GetUserQueueAsync(_owner,null)); Assert.Single(await _store.GetUserQueueAsync(_viewer,null));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory,true); }
}
