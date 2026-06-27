namespace DataverseFileManager;

/// <summary>
/// Verified logical names for the <c>cr19f_fileitem</c> table (captured via describe after
/// provisioning — Dataverse underscored the display names).
/// </summary>
public static class FileItemColumns
{
    public const string Table = "cr19f_fileitem";
    public const string Id = "cr19f_fileitemid";
    public const string Name = "cr19f_name";
    public const string Path = "cr19f_path";
    public const string ParentPath = "cr19f_parent_path";
    public const string IsFolder = "cr19f_is_folder";
    public const string FileContent = "cr19f_file_content";
    public const string SizeBytes = "cr19f_size_bytes";
    public const string Extension = "cr19f_extension";
    public const string MimeType = "cr19f_mime_type";
    public const string CreatedOn = "createdon";
    public const string ModifiedOn = "modifiedon";
}
