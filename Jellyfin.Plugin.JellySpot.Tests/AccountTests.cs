using System.Net;
using System.Security.Claims;
using System.Text;
using Jellyfin.Plugin.JellySpot.Controllers;
using Jellyfin.Plugin.JellySpot.Models;
using Jellyfin.Plugin.JellySpot.Services.Spotify;
using Jellyfin.Plugin.JellySpot.Services.Storage;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.JellySpot.Tests;

public class AccountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyspot-tests", Guid.NewGuid().ToString("N"));
    private readonly JellySpotStore _store;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Mock<IUserManager> _users = new();
    private readonly StubHandler _handler = new();
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _api;
    public AccountTests()
    {
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        paths.SetupGet(p => p.ProgramDataPath).Returns(_directory);
        _ = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
        Plugin.Instance!.UpdateConfiguration(new Jellyfin.Plugin.JellySpot.Configuration.PluginConfiguration());
        Plugin.Instance.Configuration.SpotifyClientId = "client";
        Plugin.Instance.Configuration.SpotifyRequestsPerSecond = 100;
        _store = new(NullLogger<JellySpotStore>.Instance, Path.Combine(_directory,"test.db"));
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler,false));
        _auth = new(_store,factory.Object,NullLogger<SpotifyAuthService>.Instance);
        _api = new(factory.Object,_auth,new(NullLogger<SpotifyRateLimiter>.Instance),new(_store),NullLogger<SpotifyApiClient>.Instance);
        _users.Setup(u => u.GetUserById(_user)).Returns(new User("test","provider","reset") { Id=_user });
    }
    private SpotifyTokens Tokens(bool expired=false) => new() { AccessToken="old",RefreshToken="refresh",SpotifyUserId="spotify",Scope="user-library-read",ExpiresAtUtc=DateTime.UtcNow.AddHours(expired?-1:1) };
    private JellySpotController Controller(Guid? id=null, bool admin=false)
    {
        var controller = new JellySpotController(_auth,_api,_store,null!,null!,_users.Object,null!,null!,null!,null!,NullLogger<JellySpotController>.Instance);
        var claims = new List<Claim> { new("Jellyfin-UserId",(id??_user).ToString("N")) };
        if(admin) claims.Add(new(ClaimTypes.Role,"Administrator"));
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity(claims,"test")) } };
        return controller;
    }
    [Fact] public async Task ConcurrentRefreshUsesOneExchangeAndPreservesScopeAndGeneration()
    {
        var original=Tokens(true);await _store.SaveTokensAsync(_user,original);
        _handler.Respond = async _ => { await Task.Delay(30); return Json(HttpStatusCode.OK,"{\"access_token\":\"new\",\"expires_in\":3600}"); };
        var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>_auth.GetValidTokensAsync(_user)));
        Assert.Equal(1,_handler.Count);Assert.All(results,t=> { Assert.Equal("new",t!.AccessToken);Assert.Equal(original.Generation,t.Generation);Assert.Equal(original.Scope,t.Scope); });
    }
    [Fact] public async Task RejectedUnexpiredTokenIsForciblyRefreshedOnce()
    {
        await _store.SaveTokensAsync(_user,Tokens());
        _handler.Respond=request=>Task.FromResult(request.RequestUri!.Host=="accounts.spotify.com" ? Json(HttpStatusCode.OK,"{\"access_token\":\"new\",\"expires_in\":3600}") : request.Headers.Authorization?.Parameter=="old" ? Json(HttpStatusCode.Unauthorized,"{}") : Json(HttpStatusCode.OK,"{\"tracks\":{\"items\":[]}}"));
        Assert.NotNull(await _api.SearchAsync(_user,"song","track",10,0)); Assert.Equal(3,_handler.Count);
    }
    [Fact] public async Task RevokedGrantRequiresRelinkWhileTemporaryErrorsPreserveLink()
    {
        await _store.SaveTokensAsync(_user,Tokens(true));
        _handler.Respond=_=>Task.FromResult(Json(HttpStatusCode.ServiceUnavailable,"{}"));
        await Assert.ThrowsAsync<HttpRequestException>(()=>_auth.GetValidTokensAsync(_user));
        Assert.False((await _store.GetTokensAsync(_user))!.RelinkRequired);
        _handler.Respond=_=>Task.FromResult(Json(HttpStatusCode.BadRequest,"{\"error\":\"invalid_grant\"}"));
        Assert.Null(await _auth.GetValidTokensAsync(_user)); Assert.True((await _store.GetTokensAsync(_user))!.RelinkRequired);
    }
    [Fact] public async Task UnlinkWaitsForRefreshAndCannotBeResurrected()
    {
        await _store.SaveTokensAsync(_user,Tokens(true));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Respond=async _=> { entered.SetResult();await release.Task;return Json(HttpStatusCode.OK,"{\"access_token\":\"new\",\"expires_in\":3600}"); };
        var refresh=_auth.GetValidTokensAsync(_user);await entered.Task;
        var unlink=_auth.UnlinkAsync(_user);release.SetResult();await refresh;await unlink;
        Assert.Null(await _store.GetTokensAsync(_user));
    }
    [Fact] public async Task ChangedAccountCannotReadOldCacheOrAcceptOldResponses()
    {
        var old=Tokens();await _store.SaveTokensAsync(_user,old);
        await _store.SetCacheAsync(_user.ToString("N")+":"+old.Generation+":search:track:song:10:0","old-account",TimeSpan.FromDays(1));
        var replacement=Tokens();await _store.SaveTokensAsync(_user,replacement);
        _handler.Respond=_=>Task.FromResult(Json(HttpStatusCode.OK,"{\"tracks\":{\"items\":[]}}"));
        Assert.DoesNotContain("old-account",(await _api.SearchAsync(_user,"song","track",10,0))!); Assert.Equal(1,_handler.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>_auth.EnsureGenerationAsync(_user,old.Generation,default));
    }
    [Fact] public async Task AdminCannotReadOrShareAnotherUsersPrivateCollection()
    {
        var c=new UserCollection { OwnerId=_user,SourceId="playlist",Name="Secret" };await _store.SaveCollectionAsync(c);
        var other=Guid.NewGuid();_users.Setup(u=>u.GetUserById(other)).Returns(new User("admin","provider","reset") {Id=other});
        var controller=Controller(other,true);
        Assert.IsType<NotFoundResult>(await controller.Collection(c.Id,default));
        Assert.IsType<NotFoundResult>(await controller.ShareCollection(c.Id,new(){Shared=true},default));
    }
    [Fact] public async Task SessionIdentifierCannotReplaceAuthenticatedUserIdentity()
    {
        var controller=Controller(); controller.HttpContext.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,_user.ToString())],"test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>controller.GetMe(default));
    }
    [Fact] public void EveryGlobalQueueMutationRequiresElevation()
    {
        foreach(var method in typeof(JellySpotController).GetMethods().Where(m=>m.Name is "AdminQueue" or "AdminRetry" or "AdminRetryFailed" or "AdminRematch" or "Rematch"))
            Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute),true).Cast<AuthorizeAttribute>(), a=>a.Policy=="RequiresElevation");
    }
    [Fact] public async Task SharedPlaybackResolvesOnlyItemsVisibleToViewer()
    {
        var viewer = new User("viewer", "provider", "reset") { Id = _user };
        var collection = new UserCollection { OwnerId=_user, SourceId="playlist", Name="Shared", Tracks = [
            new() { Id="available", Name="Song", Artists=["Artist"], DurationMs=180000 },
            new() { Id="restricted", Name="Restricted", Artists=["Artist"], DurationMs=180000 }
        ] };
        await _store.SaveCollectionAsync(collection);
        var libraries = new Mock<ILibraryManager>();
        libraries.Setup(l=>l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns([new MediaBrowser.Controller.Entities.Audio.Audio { Id=Guid.NewGuid(),Name="Song",Artists=["Artist"], RunTimeTicks=TimeSpan.FromMinutes(3).Ticks }]);
        var controller = Controller();
        var playback = new JellySpotController(_auth,_api,_store,null!,null!,_users.Object,libraries.Object,null!,null!,null!,NullLogger<JellySpotController>.Instance) { ControllerContext=controller.ControllerContext };
        var result=Assert.IsType<OkObjectResult>(await playback.Collection(collection.Id,default));
        var json=System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"Available\":true",json);Assert.Contains("\"Available\":false",json);Assert.DoesNotContain("Path",json);
        libraries.Verify(l=>l.GetItemList(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(q=>q.User != null && q.User.Id==_user)),Times.Once);
    }

    [Fact] public async Task GlobalDownloadViewDoesNotExposePrivateCollectionContext()
    {
        await _store.EnqueueAsync(new() { JellyfinUserId=_user,SpotifyTrackId="track",Title="Song",PlaylistId="private-id",PlaylistName="Secret membership" });
        var result=Assert.IsType<OkObjectResult>(await Controller(admin:true).AdminQueue(null,default));
        var json=System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("Secret membership",json); Assert.DoesNotContain("private-id",json); Assert.DoesNotContain("JellyfinUserId",json);
    }

    [Fact] public async Task Repeated401DoesNotLoopAndMarksAccountForRelinking()
    {
        await _store.SaveTokensAsync(_user,Tokens());
        _handler.Respond=request=>Task.FromResult(request.RequestUri!.Host=="accounts.spotify.com" ? Json(HttpStatusCode.OK,"{\"access_token\":\"new\",\"expires_in\":3600}") : Json(HttpStatusCode.Unauthorized,"{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>_api.SearchAsync(_user,"song","track",10,0));
        Assert.Equal(3,_handler.Count); Assert.True((await _store.GetTokensAsync(_user))!.RelinkRequired);
    }

    [Fact] public async Task CallbackCannotRestoreAnUnlinkedPendingAuthorization()
    {
        await _store.SaveOAuthStateAsync("pending",_user,"verifier");
        await _auth.UnlinkAsync(_user);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>_auth.HandleCallbackAsync("code","pending"));
        Assert.Equal(0,_handler.Count);Assert.Null(await _store.GetTokensAsync(_user));
    }

    [Fact] public async Task FailedSyncPreservesLastSuccessfulPrivateMembership()
    {
        await _store.SaveTokensAsync(_user,Tokens());
        await _store.SaveUserSettingsAsync(new() { JellyfinUserId=_user,SyncLikedSongs=true });
        var collection=new UserCollection { OwnerId=_user,SourceId="liked",Name="Liked Songs",Tracks=[new(){Id="preserved"}] };
        await _store.SaveCollectionAsync(collection);
        _handler.Respond=_=>Task.FromResult(Json(HttpStatusCode.ServiceUnavailable,"{}"));
        var sync=new Jellyfin.Plugin.JellySpot.Services.Sync.SyncEngine(_api,_users.Object,_auth,_store,null!,NullLogger<Jellyfin.Plugin.JellySpot.Services.Sync.SyncEngine>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(()=>sync.SyncUserAsync(_user));
        Assert.Equal("preserved",Assert.Single((await _store.GetCollectionAsync(collection.Id,_user))!.Tracks).Id);
        Assert.Equal("Failed",(await _store.GetUserSettingsAsync(_user)).LastSyncStatus);
    }

    [Fact] public async Task ScheduledSyncSkipsUnlinkedAndDeletedUsersWithoutUpstreamCalls()
    {
        await _store.SaveUserSettingsAsync(new() { JellyfinUserId=_user,SyncLikedSongs=true });
        await _store.SaveUserSettingsAsync(new() { JellyfinUserId=Guid.NewGuid(),SyncLikedSongs=true });
        var sync=new Jellyfin.Plugin.JellySpot.Services.Sync.SyncEngine(_api,_users.Object,_auth,_store,null!,NullLogger<Jellyfin.Plugin.JellySpot.Services.Sync.SyncEngine>.Instance);
        await sync.SyncAllEnabledUsersAsync(); Assert.Equal(0,_handler.Count);
    }

    private static HttpResponseMessage Json(HttpStatusCode status,string json)=>new(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    private sealed class StubHandler:HttpMessageHandler
    {
        public int Count; public Func<HttpRequestMessage,Task<HttpResponseMessage>> Respond {get;set;}=_=>Task.FromResult(Json(HttpStatusCode.OK,"{}"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Interlocked.Increment(ref Count);return Respond(request); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools();Directory.Delete(_directory,true); }
}
