namespace Jellyfin.Plugin.JellySpot.Models;

public sealed class PageQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string Sort { get; set; } = "name";
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string Direction { get; set; } = "asc";
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string Q { get; set; } = "";
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string Filter { get; set; } = "";
    public int Size => Math.Clamp(PageSize, 1, 100);
    public int Offset => (Math.Clamp(Page, 1, 100000) - 1) * Size;
}

public sealed record BrowserPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, string Revision, bool Complete = true);

public sealed class OperationInput
{
    public string Kind { get; set; } = "tracks";
    public string? SourceId { get; set; }
    public string? SourceName { get; set; }
    public List<string> TrackIds { get; set; } = [];
    public string? CatalogKind { get; set; }
    public string? CollectionId { get; set; }
    public string? Query { get; set; }
    public string? Filter { get; set; }
    public bool SelectAll { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class BrowserOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid UserId { get; set; }
    public string Generation { get; set; } = "";
    public OperationInput Input { get; set; } = new();
    public string Status { get; set; } = "Queued";
    public string Stage { get; set; } = "Preparing request";
    public string? CurrentCollection { get; set; }
    public int Discovered { get; set; }
    public int Processed { get; set; }
    public int Requested { get; set; }
    public int Existing { get; set; }
    public int Failed { get; set; }
    public bool TotalKnown { get; set; }
    public bool CancelRequested { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
    public List<OperationIssue> Issues { get; set; } = [];
}

public sealed record OperationIssue(string SourceId, string Name, string Message);

public sealed class RequestAction
{
    public List<string> Ids { get; set; } = [];
    public string Action { get; set; } = "pause";
    public int Priority { get; set; } = 0;
    public bool? PauseAll { get; set; }
}

public sealed class RequestRow
{
    public string Id { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artists { get; set; } = "";
    public string Album { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string SourceName { get; set; } = "";
    public string Status { get; set; } = "";
    public string JobStatus { get; set; } = "";
    public int Priority { get; set; }
    public double? Progress { get; set; }
    public string? Stage { get; set; }
    public string? Error { get; set; }
    public string? RequestedAtUtc { get; set; }
    public string? DownloadedAtUtc { get; set; }
    public string UpdatedAtUtc { get; set; } = "";
}

public sealed class CollectionSummary
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Kind { get; set; } = "playlist";
    public string Name { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string? OwnerName { get; set; }
    public bool Owned { get; set; }
    public bool Shared { get; set; }
    public bool Following { get; set; }
    public bool Monitored { get; set; }
    public int TrackCount { get; set; }
    public string? ImportedAtUtc { get; set; }
    public string? SourceAddedAtUtc { get; set; }
    public string? SharedAtUtc { get; set; }
    public string? FollowedAtUtc { get; set; }
    public string? UpdatedAtUtc { get; set; }
}

public sealed class BrowserTrack
{
    public string EntryId { get; set; } = "";
    public int Position { get; set; }
    public SpotifyTrackInfo Track { get; set; } = new();
    public string? ItemId { get; set; }
    public string Availability { get; set; } = "Checking";
}

public sealed record OperationSummary(string Id, string Kind, string Status, string Stage, string? CurrentCollection,
    int Processed, int Discovered, int Requested, int Existing, int Failed, bool TotalKnown, bool CancelRequested,
    DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? Error, IReadOnlyList<OperationIssue> Issues)
{
    public static OperationSummary From(BrowserOperation o) => new(o.Id, o.Input.Kind, o.Status, o.Stage, o.CurrentCollection, o.Processed, o.Discovered, o.Requested, o.Existing, o.Failed, o.TotalKnown, o.CancelRequested, o.CreatedAtUtc, o.StartedAtUtc, o.FinishedAtUtc, o.Error, o.Issues);
}
