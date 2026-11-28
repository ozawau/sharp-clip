using System.Text.Json.Serialization;

namespace SharpClip;

/// <summary>
/// Same JSON shape as Clipbara's <c>TransferDocument</c>.
/// Property names match Swift's default encoder, including <c>pinboardID</c> and <c>itemID</c>.
/// </summary>
internal sealed class TransferDocument
{
    public int Version { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<TransferItem> Items { get; set; } = [];
    public List<TransferBoard> Pinboards { get; set; } = [];
    public List<TransferEntry> Entries { get; set; } = [];
    public List<TransferExclusion> Exclusions { get; set; } = [];
}

internal sealed class TransferItem
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = "plainText";
    public byte[] RawData { get; set; } = [];
    public string? TextContent { get; set; }
    public byte[]? ThumbnailData { get; set; }
    public string? SourceAppName { get; set; }
    public string? SourceAppBundleId { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CopiedAt { get; set; }
    public string? UserTitle { get; set; }
    public bool IsPinned { get; set; }
}

internal sealed class TransferBoard
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int DisplayOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class TransferEntry
{
    [JsonPropertyName("pinboardID")]
    public Guid PinboardId { get; set; }

    [JsonPropertyName("itemID")]
    public Guid ItemId { get; set; }

    public int DisplayOrder { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class TransferExclusion
{
    public string BundleId { get; set; } = "";
    public string AppName { get; set; } = "";
}
