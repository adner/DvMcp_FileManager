namespace DataverseFileManager;

/// <summary>
/// Filesystem-shaped surface over a Dataverse-backed virtual file system. Designed so a later
/// Windows File Manager / Cloud Filter integration can consume it without redesign.
/// </summary>
public interface IDataverseFileSystem
{
    /// <summary>Lists the immediate children of a folder (files and subfolders).</summary>
    Task<IReadOnlyList<FileItem>> ListFolderAsync(string path, CancellationToken ct = default);

    /// <summary>Resolves a single node by its full path, or null if it does not exist.</summary>
    Task<FileItem?> GetItemAsync(string path, CancellationToken ct = default);

    /// <summary>Creates a folder (and any missing ancestor folders). Idempotent.</summary>
    Task<FileItem> CreateFolderAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Uploads a local file to the given virtual path, creating ancestors as needed. Upsert: if a
    /// file already exists at the path its content is replaced in place (same record); a folder at
    /// the path is a conflict. Returns the resulting <see cref="FileItem"/>.
    /// </summary>
    Task<FileItem> UploadAsync(string localPath, string remotePath, CancellationToken ct = default);

    /// <summary>Downloads a file to a local path.</summary>
    Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default);

    /// <summary>Opens a read stream over a file's content (on-demand hydration).</summary>
    Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Deletes a file, or a folder and all its descendants (recursive).</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Moves a node to a new full path (different parent and/or new leaf name), relocating a folder's
    /// entire subtree. Fails if the destination exists or would nest a folder inside itself.
    /// </summary>
    Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default);

    /// <summary>Renames a node's leaf in place (a move within the same parent).</summary>
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
}
