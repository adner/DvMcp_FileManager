using System.Text;

namespace DataverseFileManager.Explorer;

/// <summary>
/// Encodes/decodes the opaque <c>FileIdentity</c> blob that cfapi round-trips on every placeholder.
/// We stash the node's <b>virtual path</b> (e.g. <c>/Documents/report.pdf</c>) so a hydration
/// callback can recover which Dataverse node to fetch without any side table.
/// </summary>
internal static class FileIdentity
{
    public static byte[] Encode(string virtualPath) => Encoding.Unicode.GetBytes(virtualPath);

    public static unsafe string Decode(void* blob, uint lengthBytes)
        => blob is null || lengthBytes == 0
            ? "/"
            : new string((char*)blob, 0, (int)(lengthBytes / sizeof(char)));
}
