using System.Diagnostics;
using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellySpot.Tests;

public sealed class BrowserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jellyspot-browser-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _owner = Guid.NewGuid(), _other = Guid.NewGuid();
    private readonly JellySpotStore _store;
    public BrowserTests() { Directory.CreateDirectory(_dir); _store = Open(); }
    private JellySpotStore Open() => new(NullLogger<JellySpotStore>.Instance, Path.Combine(_dir, "test.db"));
    private Task<EnqueueResult> Enqueue(Guid user, string track = "track", string status = "Pending") => _store.EnqueueAsync(new() { JellyfinUserId = user, SpotifyTrackId = track, Title = track, Status = status });
    [Fact]
    public async Task SharedPauseCancelAndPriorityArePersonal()
    {
        await Enqueue(_owner); await Enqueue(_other); var mine = Assert.Single((await _store.RequestPageAsync(_owner, new())).Items); var other = Assert.Single((await _store.RequestPageAsync(_other, new())).Items);
        Assert.Equal(0, await _store.ChangeRequestsAsync(_other, new() { Action = "pause", Ids = [mine.Id] }));
        await _store.ChangeRequestsAsync(_owner, new() { Action = "pause", Ids = [mine.Id] }); Assert.Single(await _store.EligibleJobsAsync());
        await _store.ChangeRequestsAsync(_other, new() { Action = "cancel", Ids = [other.Id] }); Assert.Empty(await _store.EligibleJobsAsync());
        await _store.ChangeRequestsAsync(_owner, new() { Action = "resume", Ids = [mine.Id] }); Assert.Single(await _store.EligibleJobsAsync());
        await _store.ChangeRequestsAsync(_owner, new() { Action = "pause", PauseAll = true }); Assert.Empty(await _store.EligibleJobsAsync());
        await _store.ChangeRequestsAsync(_owner, new() { Action = "resume", PauseAll = false }); Assert.Single(await _store.EligibleJobsAsync());
        var job = Assert.Single(await _store.GetQueueAsync()); job.Status = "Completed"; await _store.UpdateQueueItemAsync(job);
        await _store.ChangeRequestsAsync(_owner, new() { Action = "pause", Ids = [mine.Id] }); Assert.Equal("Completed", Assert.Single((await _store.RequestPageAsync(_owner, new())).Items).Status);
        Assert.Equal("Cancelled", Assert.Single((await _store.RequestPageAsync(_other, new())).Items).Status);
    }
    [Fact]
    public async Task HighestEligiblePriorityWinsWithoutResettingActiveJobs()
    {
        await Enqueue(_owner, "normal"); await Enqueue(_owner, "high"); await Enqueue(_other, "normal");
        var page = await _store.RequestPageAsync(_owner, new()); var high = page.Items.Single(r => r.TrackId == "high");
        await _store.ChangeRequestsAsync(_owner, new() { Action = "priority", Ids = [high.Id], Priority = 1 });
        Assert.Equal("high", (await _store.EligibleJobsAsync())[0].SpotifyTrackId);
        await _store.ChangeRequestsAsync(_owner, new() { Action = "pause", Ids = [high.Id] }); Assert.Equal("normal", Assert.Single(await _store.EligibleJobsAsync()).SpotifyTrackId);
    }
    [Fact]
    public async Task OperationAcceptanceIsIdempotentAndOwnerScoped()
    {
        var first = await _store.AcceptOperationAsync(_owner, "generation", new() { Kind = "tracks", TrackIds = ["one"], IdempotencyKey = "click" });
        var second = await _store.AcceptOperationAsync(_owner, "generation", new() { Kind = "tracks", TrackIds = ["one"], IdempotencyKey = "click" }); Assert.Equal(first.Id, second.Id);
        Assert.Null(await _store.OperationAsync(first.Id, _other)); Assert.False(await _store.CancelOperationAsync(first.Id, _other));
        Assert.True(await _store.CancelOperationAsync(first.Id, _owner)); first.Stage = "Progress races cancellation"; await _store.SaveOperationAsync(first); Assert.True((await _store.OperationAsync(first.Id, _owner))!.CancelRequested);
        first.Status = "Running"; await _store.SaveOperationAsync(first); var reopened = Open(); Assert.Equal("Queued", (await reopened.OperationAsync(first.Id, _owner))!.Status);
        Assert.True(File.Exists(Path.Combine(_dir, "test.db.pre-browser.bak")));
        var summary = Assert.Single((await reopened.OperationPageAsync(_owner, new())).Items);
        Assert.True(summary.CancelRequested); Assert.Equal(first.Id, summary.Id);
        Assert.Empty((await reopened.OperationPageAsync(_other, new())).Items);
    }
    [Fact]
    public async Task SyncAndMetadataOperationsCoalescePerUserAndSource()
    {
        var first = await _store.AcceptOperationAsync(_owner, "gen", new() { Kind = "sync" }); var same = await _store.AcceptOperationAsync(_owner, "gen", new() { Kind = "sync" }); Assert.Equal(first.Id, same.Id);
        var other = await _store.AcceptOperationAsync(_other, "gen", new() { Kind = "sync" }); Assert.NotEqual(first.Id, other.Id);
        var a = await _store.AcceptOperationAsync(_owner, "gen", new() { Kind = "inspect-album", SourceId = "a" }); var b = await _store.AcceptOperationAsync(_owner, "gen", new() { Kind = "inspect-album", SourceId = "b" }); Assert.NotEqual(a.Id, b.Id);
    }
    [Fact]
    public async Task CompleteDatasetSortingKeepsUnknownDatesLastAndDuplicateEntries()
    {
        var collection = new UserCollection { OwnerId = _owner, SourceId = "playlist", Name = "Ordered", Tracks = [new() { Id = "repeat", Name = "Z", AddedAtUtc = null }, new() { Id = "repeat", Name = "A", AddedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, new() { Id = "third", Name = "B", AddedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }] }; await _store.SaveCollectionAsync(collection);
        var empty = new Dictionary<string, string>(); var asc = await _store.TrackPageAsync(collection.Id, new() { Sort = "added", PageSize = 1 }, empty); Assert.Equal(3, asc.Total); Assert.Equal("A", Assert.Single(asc.Items).Track.Name);
        var desc = await _store.TrackPageAsync(collection.Id, new() { Sort = "added", Direction = "desc", Page = 3, PageSize = 1 }, empty); Assert.Null(Assert.Single(desc.Items).Track.AddedAtUtc);
        Assert.Equal(3, (await _store.ScopeTracksAsync(collection.Id)).Select(t => t.EntryId).Distinct().Count());
        var filtered = await _store.TrackPageAsync(collection.Id, new() { Filter = "available" }, new Dictionary<string, string> { { "repeat", "audio" } }); Assert.Equal(2, filtered.Total);
    }
    [Fact]
    public async Task CatalogSnapshotsStayAccountScopedAndSummaryPagesDoNotContainMembership()
    {
        await _store.SaveCatalogAsync(_owner, "old", "albums", new object[] { new SpotifyAlbumInfo { Id = "album", Name = "Old account" } });
        Assert.Empty((await _store.CatalogPageAsync(_owner, "new", "albums", new())).Items); Assert.False((await _store.CatalogPageAsync(_owner, "new", "albums", new())).Complete);
        var collection = new UserCollection { OwnerId = _owner, SourceId = "playlist", Name = "Private", Tracks = [new() { Id = "secret" }] }; await _store.SaveCollectionAsync(collection);
        Assert.Empty((await _store.CollectionPageAsync(_other, "mine", new(), _ => "User")).Items);
        await _store.SetSharingAsync(collection.Id, _owner, true); var page = await _store.CollectionPageAsync(_other, "shared", new(), _ => "User"); Assert.Equal(1, Assert.Single(page.Items).TrackCount); Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(page));
    }
    [Fact]
    public async Task LargeLibraryAndPlaylistPagesAreBoundedAndBenchmarkWarmReads()
    {
        var tracks = Enumerable.Range(0, 10000).Select(i => new SpotifyTrackInfo { Id = "t" + i, Name = $"Track {i:D5}", Artists = ["Artist"], Album = "Album" }).ToList();
        await _store.SaveCatalogAsync(_owner, "gen", "liked", tracks.Cast<object>(), tracks);
        var collection = new UserCollection { OwnerId = _owner, SourceId = "large", Name = "Large", Tracks = tracks.Take(5000).ToList() }; await _store.SaveCollectionAsync(collection);
        var available = new Dictionary<string, string>(); await _store.TrackPageAsync(collection.Id, new(), available);
        var watch = Stopwatch.StartNew(); var page = await _store.TrackPageAsync(collection.Id, new() { Sort = "title", Direction = "desc", Page = 2, PageSize = 1000 }, available); watch.Stop();
        Assert.Equal(5000, page.Total); Assert.Equal(100, page.Items.Count); Assert.Equal("Track 04899", page.Items[0].Track.Name);
        Console.WriteLine($"Warm 5,000-entry playlist page: {watch.ElapsedMilliseconds} ms, {page.Items.Count} rows (target <300 ms).");
        for (var i = 0; i < 1000; i++) await Enqueue(_owner, "r" + i);
        watch.Restart(); var requests = await _store.RequestPageAsync(_owner, new() { PageSize = 50 }); watch.Stop(); Assert.Equal(1000, requests.Total); Assert.Equal(50, requests.Items.Count); Console.WriteLine($"Warm 1,000-request page: {watch.ElapsedMilliseconds} ms (target <300 ms).");
    }
    [Fact]
    public async Task RecoveredOperationCannotResurrectAnAlreadyCancelledRequest()
    {
        var job = new QueueItem { JellyfinUserId = _owner, SpotifyTrackId = "checkpoint", Title = "Checkpoint" };
        await _store.EnqueueAsync(job, operationId: "operation");
        var request = Assert.Single((await _store.RequestPageAsync(_owner, new())).Items);
        await _store.ChangeRequestsAsync(_owner, new() { Action = "cancel", Ids = [request.Id] });
        await _store.EnqueueAsync(new() { JellyfinUserId = _owner, SpotifyTrackId = "checkpoint" }, operationId: "operation");
        Assert.Equal("Cancelled", Assert.Single((await _store.RequestPageAsync(_owner, new())).Items).Status);
        Assert.Empty(await _store.EligibleJobsAsync());
        await _store.EnqueueAsync(new() { JellyfinUserId = _owner, SpotifyTrackId = "checkpoint" }, operationId: "new-explicit-request");
        Assert.Single(await _store.EligibleJobsAsync());
    }

    [Fact]
    public async Task OperationCheckpointsPreserveIndependentSourceAssociations()
    {
        await _store.EnqueueAsync(new() { JellyfinUserId = _owner, SpotifyTrackId = "same", PlaylistId = "first" }, operationId: "sync");
        await _store.EnqueueAsync(new() { JellyfinUserId = _owner, SpotifyTrackId = "same", PlaylistId = "second" }, operationId: "sync");
        Assert.Single(await _store.GetQueueAsync()); Assert.Equal(2, (await _store.RequestPageAsync(_owner, new())).Total);
    }

    [Fact]
    public async Task AdministratorRetryCanScheduleWorkWithNoEligiblePersonalRequest()
    {
        await Enqueue(_owner, status: "Failed"); var job = Assert.Single(await _store.GetQueueAsync()); var request = Assert.Single((await _store.RequestPageAsync(_owner, new())).Items);
        await _store.ChangeRequestsAsync(_owner, new() { Action = "cancel", Ids = [request.Id] }); Assert.Empty(await _store.EligibleJobsAsync());
        await _store.RetryJobAsync(job.Id, null); Assert.Single(await _store.EligibleJobsAsync()); Assert.True(await _store.ClaimJobAsync(job.Id));
        Assert.Equal("Cancelled", Assert.Single((await _store.RequestPageAsync(_owner, new())).Items).Status);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }
}
