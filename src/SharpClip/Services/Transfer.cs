namespace SharpClip;

/// <summary>
/// Same JSON shape as Clipbara's backup, so a Mac export can be imported here.
/// </summary>
internal sealed class TransferDocument
{
    public int Version { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<TransferItem> Items { get; set; } = [];
    public List<TransferBoard> Pinboards { get; set; } = [];
}

internal sealed class TransferItem
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = "plainText";
    public byte[] RawData { get; set; } = [];
    public string? TextContent { get; set; }
    public string? SourceAppName { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CopiedAt { get; set; }
    public string? UserTitle { get; set; }
}

internal sealed class TransferBoard
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int DisplayOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
