using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace DataverseFileManager.Explorer;

/// <summary>
/// Registers (and unregisters) the Dataverse sync root with the Windows shell so it appears as a
/// branded node in Explorer's navigation pane (custom display name + <c>dvicon.ico</c>).
/// </summary>
/// <remarks>
/// <see cref="StorageProviderSyncRootManager.Register"/> requires the process to run with
/// <b>package identity</b> (the sparse package) — it throws <c>Class not registered</c> /
/// access-denied otherwise. Policy values mirror Microsoft's CloudMirror sample: full hydration
/// (bytes on demand) with auto-dehydration, and full population with lazy per-folder placeholder
/// creation driven by the <c>FETCH_PLACEHOLDERS</c> callback.
/// </remarks>
internal static class SyncRootRegistrar
{
    // Stable provider id; combined with the user SID to form a per-user sync-root id so two
    // Windows users on the same machine register distinct roots.
    private const string ProviderId = "DataverseFileManager";

    public static string SyncRootId =>
        $"{ProviderId}!{WindowsIdentity.GetCurrent().User!.Value}!Default";

    public static async Task RegisterAsync(string syncRootPath, string iconPath)
    {
        Directory.CreateDirectory(syncRootPath);

        // Always (re)register: Register is also what marks the folder ON DISK as a sync root, and a
        // lingering registry entry can outlive that marking (e.g. the folder was deleted/recreated),
        // which makes CfConnectSyncRoot fail with ERROR_CLOUD_FILE_NOT_UNDER_SYNC_ROOT (0x80070186).
        // Unregister-then-register reasserts both, so a stale state self-heals on the next run.
        if (IsRegistered())
        {
            try
            {
                StorageProviderSyncRootManager.Unregister(SyncRootId);
                Log.Info($"Re-registering: cleared stale registration for '{SyncRootId}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Unregister before re-register failed (continuing): {ex.Message}");
            }
        }

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

        StorageProviderSyncRootInfo info = new()
        {
            Id = SyncRootId,
            Path = folder,
            DisplayNameResource = "Dataverse",
            IconResource = $"{iconPath},0",
            Version = "1.0.0",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.Default,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            // Opaque provider context (round-tripped by the platform); useful for diagnostics.
            Context = CryptographicBuffer.ConvertStringToBinary(SyncRootId, BinaryStringEncoding.Utf8),
        };

        StorageProviderSyncRootManager.Register(info);
        Log.Info($"Registered sync root '{SyncRootId}' at '{syncRootPath}' (icon: {iconPath}).");
    }

    public static void Unregister()
    {
        if (IsRegistered())
        {
            StorageProviderSyncRootManager.Unregister(SyncRootId);
            Log.Info($"Unregistered sync root '{SyncRootId}'.");
        }
        else
        {
            Log.Info("Unregister: no matching sync root was registered.");
        }
    }

    private static bool IsRegistered()
    {
        foreach (StorageProviderSyncRootInfo root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            if (string.Equals(root.Id, SyncRootId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
