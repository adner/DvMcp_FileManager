namespace DataverseFileManager;

/// <summary>A node in the Dataverse-backed virtual file system: a file or a folder.</summary>
public sealed record FileItem
{
    /// <summary>Full virtual path, e.g. <c>/Documents/2026/report.pdf</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Leaf name, e.g. <c>report.pdf</c> or <c>2026</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Containing folder's path, e.g. <c>/Documents/2026</c> (empty for root).</summary>
    public string? ParentPath { get; init; }

    public bool IsFolder { get; init; }
    public long? SizeBytes { get; init; }
    public string? Extension { get; init; }
    public string? MimeType { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }
    public DateTimeOffset? ModifiedOn { get; init; }

    /// <summary>Dataverse record id (<c>cr19f_fileitemid</c>).</summary>
    public Guid RecordId { get; init; }
}
