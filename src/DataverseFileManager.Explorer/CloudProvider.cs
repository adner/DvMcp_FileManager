using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DataverseFileManager;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace DataverseFileManager.Explorer;

/// <summary>
/// The cfapi sync engine: connects to the registered sync root and answers the platform's
/// callbacks by delegating to <see cref="IDataverseFileSystem"/>.
/// </summary>
/// <remarks>
/// Read-only (milestone 4A): two callbacks are wired —
/// <list type="bullet">
/// <item><b>FETCH_PLACEHOLDERS</b> (folder expanded) → <see cref="IDataverseFileSystem.ListFolderAsync"/>
///   → <c>CfExecute(TRANSFER_PLACEHOLDERS)</c>, lazily populating that one level.</item>
/// <item><b>FETCH_DATA</b> (cloud-only file opened) → <see cref="IDataverseFileSystem.OpenReadAsync"/>
///   → <c>CfExecute(TRANSFER_DATA)</c> in chunks.</item>
/// </list>
/// State is static because the callbacks must be <see cref="UnmanagedCallersOnlyAttribute"/> statics
/// (the platform invokes raw function pointers); the host owns a single sync root, so a singleton fits.
/// Callbacks must never block the platform thread — both hop to a worker via <see cref="Task.Run(Action)"/>.
/// </remarks>
internal static class CloudProvider
{
    // FILE_ATTRIBUTE_* (winnt.h) — only the two we need.
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    // NTSTATUS values. STATUS_SUCCESS = 0; anything outside the STATUS_CLOUD_FILE_* range is mapped
    // by the platform to STATUS_CLOUD_FILE_UNSUCCESSFUL, so STATUS_UNSUCCESSFUL is fine to signal failure.
    private static readonly NTSTATUS StatusSuccess = (NTSTATUS)0;
    private static readonly NTSTATUS StatusUnsuccessful = (NTSTATUS)unchecked((int)0xC0000001);

    private const int ChunkSize = 1 << 20; // 1 MB — 4KB-aligned, satisfies the transfer alignment rule.

    private static IDataverseFileSystem _fs = null!;
    private static string _rootPath = null!;
    private static CF_CALLBACK_REGISTRATION[]? _callbackTable;
    private static GCHandle _tableHandle;
    private static CF_CONNECTION_KEY _connectionKey;
    private static bool _connected;
    private static FileSystemWatcher? _watcher;

    // Paths currently being processed (or just placeholder-converted by us) — suppresses the
    // watcher reacting to its own writes and to placeholder churn from FETCH callbacks.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public static unsafe void Connect(string syncRootPath, IDataverseFileSystem fs)
    {
        _fs = fs;
        _rootPath = syncRootPath.TrimEnd('\\');

        // Pinned for the process lifetime: cfapi retains the table pointer until disconnect.
        _callbackTable =
        [
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = &OnFetchPlaceholders },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = &OnNotifyDelete },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = &OnNotifyRename },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null }, // CF_CALLBACK_REGISTRATION_END
        ];
        _tableHandle = GCHandle.Alloc(_callbackTable, GCHandleType.Pinned);

        Log.Info($"CfConnectSyncRoot: {_rootPath}");
        HRESULT hr = PInvoke.CfConnectSyncRoot(
            _rootPath,
            _callbackTable,
            null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey);
        if (hr.Failed)
        {
            Log.Error($"CfConnectSyncRoot failed: HRESULT 0x{(uint)hr.Value:X8}");
            hr.ThrowOnFailure();
        }

        _connected = true;
        Log.Info("CfConnectSyncRoot: connected.");

        StartWatcher();
    }

    public static void Disconnect()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_connected)
        {
            HRESULT hr = PInvoke.CfDisconnectSyncRoot(_connectionKey);
            Log.Info($"CfDisconnectSyncRoot: HRESULT 0x{(uint)hr.Value:X8}");
            _connected = false;
        }
        if (_tableHandle.IsAllocated)
            _tableHandle.Free();
    }

    /// <summary>Logs a failing HRESULT; returns true when the operation succeeded.</summary>
    private static bool Ok(HRESULT hr, string op)
    {
        if (hr.Failed)
        {
            Log.Error($"{op} failed: HRESULT 0x{(uint)hr.Value:X8}");
            return false;
        }
        return true;
    }

    // --- Callbacks (platform threads) -------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Volume-relative absolute path + drive → full Win32 path of the folder being expanded.
        string baseDir = FullPathOf(info);
        string virtualPath = ToVirtualPath(baseDir);
        CF_CONNECTION_KEY key = info->ConnectionKey;
        long transferKey = info->TransferKey;

        Log.Info($"FETCH_PLACEHOLDERS: '{baseDir}' → virtual '{virtualPath}'");
        _ = Task.Run(() => PopulateAsync(key, transferKey, baseDir, virtualPath));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Resolve from the live path (NormalizedPath), not the FileIdentity blob — the latter goes
        // stale after a rename, while NormalizedPath always reflects the file's current location.
        string virtualPath = VirtualPathOf(info);
        CF_CONNECTION_KEY key = info->ConnectionKey;
        long transferKey = info->TransferKey;
        long offset = parameters->FetchData.RequiredFileOffset;
        long length = parameters->FetchData.RequiredLength;

        Log.Info($"FETCH_DATA: '{virtualPath}' offset={offset} length={length}");
        _ = Task.Run(() => HydrateAsync(key, transferKey, virtualPath, offset, length));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // The platform gates the local delete on our ACK. Use the live path (robust to prior renames).
        string virtualPath = VirtualPathOf(info);
        CF_CONNECTION_KEY key = info->ConnectionKey;
        long transferKey = info->TransferKey;

        Log.Info($"NOTIFY_DELETE: '{virtualPath}'");
        _ = Task.Run(() => DeleteAsync(key, transferKey, virtualPath));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Source is the live (pre-rename) path; target is volume-relative (+ drive) → virtual path.
        string fromVirtual = VirtualPathOf(info);
        string targetFull = ToFullPath(info->VolumeDosName.ToString(), parameters->Rename.TargetPath.ToString());
        string toVirtual = ToVirtualPath(targetFull);
        CF_CONNECTION_KEY key = info->ConnectionKey;
        long transferKey = info->TransferKey;

        Log.Info($"NOTIFY_RENAME: '{fromVirtual}' → '{toVirtual}' (target '{targetFull}')");
        _ = Task.Run(() => RenameAsync(key, transferKey, fromVirtual, toVirtual, targetFull));
    }

    // --- Async workers (off the platform thread) --------------------------------------------

    private static async Task DeleteAsync(CF_CONNECTION_KEY key, long transferKey, string virtualPath)
    {
        NTSTATUS status = StatusSuccess;
        try
        {
            await _fs.DeleteAsync(virtualPath);
            Log.Info($"  deleted '{virtualPath}' from Dataverse");
        }
        catch (FileNotFoundException)
        {
            // Idempotent: the desired end state (gone) already holds — a double-fire or prior removal.
            Log.Info($"  '{virtualPath}' already absent in Dataverse; delete is a no-op");
        }
        catch (Exception ex)
        {
            Log.Error($"NOTIFY_DELETE failed for '{virtualPath}'", ex);
            status = StatusUnsuccessful; // ACK failure blocks the local delete, keeping the two in step
        }
        AckDelete(key, transferKey, status);
    }

    private static async Task RenameAsync(CF_CONNECTION_KEY key, long transferKey, string fromVirtual, string toVirtual, string targetFull)
    {
        NTSTATUS status = StatusSuccess;
        try
        {
            // A rename or a drag-to-another-folder both surface as a full-path move within the root.
            // Targets outside the sync root aren't supported here (would map oddly) — skip cloud-side.
            if (targetFull.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
            {
                await _fs.MoveAsync(fromVirtual, toVirtual);
                Log.Info($"  moved '{fromVirtual}' → '{toVirtual}' in Dataverse");
            }
            else
            {
                Log.Warn($"  rename target '{targetFull}' is outside the sync root; cloud unchanged");
            }
        }
        catch (FileNotFoundException)
        {
            // Idempotent: source already moved/gone (e.g. a double-fire) — let the local op proceed.
            Log.Info($"  '{fromVirtual}' already absent in Dataverse; rename is a no-op");
        }
        catch (Exception ex)
        {
            Log.Error($"NOTIFY_RENAME failed '{fromVirtual}' → '{toVirtual}'", ex);
            status = StatusUnsuccessful;
        }
        AckRename(key, transferKey, status);
    }

    private static async Task PopulateAsync(CF_CONNECTION_KEY key, long transferKey, string baseDir, string virtualPath)
    {
        try
        {
            IReadOnlyList<FileItem> children = await _fs.ListFolderAsync(virtualPath);
            Log.Info($"  listed '{virtualPath}': {children.Count} item(s)");
            TransferPlaceholders(key, transferKey, children);
        }
        catch (Exception ex)
        {
            Log.Error($"FETCH_PLACEHOLDERS failed for '{virtualPath}'", ex);
            TransferPlaceholders(key, transferKey, []); // complete the request with an empty set
        }
    }

    private static async Task HydrateAsync(CF_CONNECTION_KEY key, long transferKey, string virtualPath, long offset, long length)
    {
        try
        {
            await using Stream stream = await _fs.OpenReadAsync(virtualPath);
            await SkipAsync(stream, offset); // SAS GET streams are forward-only

            byte[] buffer = new byte[ChunkSize];
            long pos = offset;
            long remaining = length;
            long transferred = 0;
            while (remaining > 0)
            {
                int want = (int)Math.Min(ChunkSize, remaining);
                int read = await ReadFullAsync(stream, buffer, want);
                if (read == 0) break;
                TransferData(key, transferKey, buffer, pos, read, StatusSuccess);
                pos += read;
                remaining -= read;
                transferred += read;
            }
            Log.Info($"  hydrated '{virtualPath}': {transferred} byte(s) from offset {offset}");
        }
        catch (Exception ex)
        {
            Log.Error($"FETCH_DATA failed for '{virtualPath}'", ex);
            TransferData(key, transferKey, null, offset, length, StatusUnsuccessful);
        }
    }

    // --- CfExecute helpers ------------------------------------------------------------------

    private static unsafe void TransferPlaceholders(CF_CONNECTION_KEY key, long transferKey, IReadOnlyList<FileItem> children)
    {
        int n = children.Count;
        var infos = new CF_PLACEHOLDER_CREATE_INFO[n];
        var pins = new List<GCHandle>(n * 2);
        try
        {
            for (int i = 0; i < n; i++)
            {
                FileItem c = children[i];

                // Pin the leaf name (PCWSTR; C# strings are null-terminated) and the identity blob.
                var nameHandle = GCHandle.Alloc(c.Name, GCHandleType.Pinned);
                pins.Add(nameHandle);
                byte[] identity = FileIdentity.Encode(c.Path);
                var idHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
                pins.Add(idHandle);

                long modified = (c.ModifiedOn ?? DateTimeOffset.UtcNow).ToFileTime();
                long created = (c.CreatedOn ?? c.ModifiedOn ?? DateTimeOffset.UtcNow).ToFileTime();

                infos[i].RelativeFileName = new PCWSTR((char*)nameHandle.AddrOfPinnedObject());
                infos[i].FsMetadata.FileSize = c.IsFolder ? 0 : (c.SizeBytes ?? 0);
                infos[i].FsMetadata.BasicInfo.FileAttributes = c.IsFolder ? FileAttributeDirectory : FileAttributeNormal;
                infos[i].FsMetadata.BasicInfo.CreationTime = created;
                infos[i].FsMetadata.BasicInfo.LastWriteTime = modified;
                infos[i].FsMetadata.BasicInfo.ChangeTime = modified;
                infos[i].FsMetadata.BasicInfo.LastAccessTime = modified;
                infos[i].FileIdentity = (void*)idHandle.AddrOfPinnedObject();
                infos[i].FileIdentityLength = (uint)identity.Length;
                // MARK_IN_SYNC = the placeholder is up to date. We do NOT set the *placeholder-level*
                // DISABLE_ON_DEMAND_POPULATION, so each child folder still fires its own
                // FETCH_PLACEHOLDERS when expanded (lazy population stays intact).
                infos[i].Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;
            }

            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = key,
                TransferKey = transferKey,
            };

            fixed (CF_PLACEHOLDER_CREATE_INFO* pInfos = infos)
            {
                var opParams = new CF_OPERATION_PARAMETERS
                {
                    ParamSize = (uint)((int)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous")
                        + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferPlaceholders_e__Struct)),
                };
                // *Operation-level* DISABLE_ON_DEMAND_POPULATION marks THIS directory as fully
                // populated so the platform stops re-firing FETCH_PLACEHOLDERS for it. Set it on the
                // final (here: only) batch — without it the directory re-enumerates in a tight loop.
                opParams.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;
                opParams.TransferPlaceholders.CompletionStatus = StatusSuccess;
                opParams.TransferPlaceholders.PlaceholderTotalCount = n;
                opParams.TransferPlaceholders.PlaceholderArray = pInfos;
                opParams.TransferPlaceholders.PlaceholderCount = (uint)n;
                opParams.TransferPlaceholders.EntriesProcessed = 0;

                HRESULT hr = PInvoke.CfExecute(opInfo, ref opParams);
                if (Ok(hr, $"CfExecute(TRANSFER_PLACEHOLDERS, {n} item(s))"))
                    Log.Info($"  transferred {opParams.TransferPlaceholders.EntriesProcessed}/{n} placeholder(s)");
            }
        }
        finally
        {
            foreach (GCHandle h in pins)
                h.Free();
        }
    }

    private static unsafe void TransferData(CF_CONNECTION_KEY key, long transferKey, byte[]? buffer, long offset, long length, NTSTATUS status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = key,
            TransferKey = transferKey,
        };

        fixed (byte* pBuf = buffer)
        {
            var opParams = new CF_OPERATION_PARAMETERS
            {
                ParamSize = (uint)((int)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous")
                    + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct)),
            };
            opParams.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            opParams.TransferData.CompletionStatus = status;
            opParams.TransferData.Buffer = pBuf;
            opParams.TransferData.Offset = offset;
            opParams.TransferData.Length = length;

            HRESULT hr = PInvoke.CfExecute(opInfo, ref opParams);
            Ok(hr, $"CfExecute(TRANSFER_DATA, offset={offset}, length={length})");
        }
    }

    private static unsafe void AckDelete(CF_CONNECTION_KEY key, long transferKey, NTSTATUS status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            ConnectionKey = key,
            TransferKey = transferKey,
        };
        var opParams = new CF_OPERATION_PARAMETERS
        {
            ParamSize = (uint)((int)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous")
                + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckDelete_e__Struct)),
        };
        opParams.AckDelete.Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE;
        opParams.AckDelete.CompletionStatus = status;

        Ok(PInvoke.CfExecute(opInfo, ref opParams), "CfExecute(ACK_DELETE)");
    }

    private static unsafe void AckRename(CF_CONNECTION_KEY key, long transferKey, NTSTATUS status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            ConnectionKey = key,
            TransferKey = transferKey,
        };
        var opParams = new CF_OPERATION_PARAMETERS
        {
            ParamSize = (uint)((int)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous")
                + sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckRename_e__Struct)),
        };
        opParams.AckRename.Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE;
        opParams.AckRename.CompletionStatus = status;

        Ok(PInvoke.CfExecute(opInfo, ref opParams), "CfExecute(ACK_RENAME)");
    }

    // --- Local write-back: watch the sync root for new files/folders ------------------------

    private static void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Dispatch(e.FullPath, ProcessLocalAsync);
        _watcher.Renamed += (_, e) => Dispatch(e.FullPath, ProcessLocalAsync);
        _watcher.Changed += (_, e) => Dispatch(e.FullPath, ProcessModifiedAsync);
        _watcher.Error += (_, e) => Log.Error("FileSystemWatcher error", e.GetException());
        _watcher.EnableRaisingEvents = true;
        Log.Info($"Watching '{_rootPath}' for local changes.");
    }

    /// <summary>
    /// Runs <paramref name="worker"/> for a local change off the watcher thread, with a per-path
    /// in-flight guard that dedupes the event bursts a single save produces. The same keyspace is
    /// shared across Created/Renamed/Changed, so a new-file upload in progress also suppresses the
    /// trailing Changed events from that same write.
    /// </summary>
    private static void Dispatch(string fullPath, Func<string, Task> worker)
    {
        if (!_inFlight.TryAdd(fullPath, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await worker(fullPath); }
            catch (Exception ex) { Log.Error($"write-back failed for '{fullPath}'", ex); }
            finally { _inFlight.TryRemove(fullPath, out _); }
        });
    }

    private static async Task ProcessLocalAsync(string fullPath)
    {
        FileAttributes attr;
        try { attr = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        // Our own placeholders are reparse points; population/hydration/convert all churn them.
        if (attr.HasFlag(FileAttributes.ReparsePoint)) return;

        string virtualPath = ToVirtualPath(fullPath);

        if (attr.HasFlag(FileAttributes.Directory))
        {
            // New local folder → mirror it into Dataverse. Left as a normal local dir; its children
            // are caught by this same watcher and uploaded with the correct virtual path.
            Log.Info($"NEW FOLDER (local): '{fullPath}' → '{virtualPath}'");
            await _fs.CreateFolderAsync(virtualPath);
            Log.Info($"  created folder '{virtualPath}' in Dataverse");
            return;
        }

        // New local file → wait for the writer to finish, upload, then convert to an in-sync placeholder.
        Log.Info($"NEW FILE (local): '{fullPath}' → '{virtualPath}'");
        if (!await WaitUntilReadableAsync(fullPath))
        {
            Log.Warn($"  '{fullPath}' stayed locked; skipping upload");
            return;
        }
        await _fs.UploadAsync(fullPath, virtualPath);
        Log.Info($"  uploaded '{virtualPath}' to Dataverse");
        ConvertFileToPlaceholder(fullPath, virtualPath);
    }

    /// <summary>
    /// Handles a <c>Changed</c> event: an in-place edit of an already-synced file. We only act on a
    /// placeholder the platform has marked <b>NOT_IN_SYNC</b> — our own hydration writes leave the
    /// placeholder IN_SYNC, so that one check is what keeps hydration from looping back as an upload.
    /// New (non-placeholder) files are the Created path's job and are ignored here.
    /// </summary>
    private static async Task ProcessModifiedAsync(string fullPath)
    {
        FileAttributes attr;
        try { attr = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        if (attr.HasFlag(FileAttributes.Directory)) return;      // folder metadata churn — nothing to upload
        if (!attr.HasFlag(FileAttributes.ReparsePoint)) return;  // not a placeholder yet → Created path owns it

        if (!IsDirtyPlaceholder(fullPath)) return;               // in sync (or unknowable) → our own write or no change

        string virtualPath = ToVirtualPath(fullPath);
        Log.Info($"MODIFIED (local): '{fullPath}' → '{virtualPath}'");
        if (!await WaitUntilReadableAsync(fullPath))
        {
            Log.Warn($"  '{fullPath}' stayed locked; skipping re-upload");
            return;
        }
        await _fs.UploadAsync(fullPath, virtualPath);
        Log.Info($"  re-uploaded '{virtualPath}' to Dataverse");
        MarkInSync(fullPath, virtualPath);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is a placeholder the platform flagged out of sync (a real
    /// user edit). Reads only metadata, so it never hydrates a cloud-only file. On any probe failure it
    /// returns false — the safe default, since a spurious upload loop is worse than a missed edit.
    /// </summary>
    private static unsafe bool IsDirtyPlaceholder(string fullPath)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[1024]; // fixed fields (~60B) + the inline file-identity blob
            HRESULT hr = PInvoke.CfGetPlaceholderInfo(
                handle, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD, buffer, out _);
            if (!Ok(hr, $"CfGetPlaceholderInfo('{fullPath}')")) return false;

            CF_IN_SYNC_STATE state;
            fixed (byte* p = buffer)
                state = ((CF_PLACEHOLDER_STANDARD_INFO*)p)->InSyncState;
            return state == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC;
        }
        catch (Exception ex)
        {
            Log.Warn($"in-sync probe failed for '{fullPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Clears the dirty flag after a successful re-upload so the file shows as synced again.</summary>
    private static void MarkInSync(string fullPath, string virtualPath)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            HRESULT hr = PInvoke.CfSetInSyncState(
                handle, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE);
            if (Ok(hr, $"CfSetInSyncState('{virtualPath}')"))
                Log.Info($"  marked '{virtualPath}' in sync");
        }
        catch (Exception ex)
        {
            Log.Error($"CfSetInSyncState failed for '{virtualPath}'", ex);
        }
    }

    /// <summary>Polls until the file opens exclusively (i.e. the writer has released it).</summary>
    private static async Task<bool> WaitUntilReadableAsync(string fullPath)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var s = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { await Task.Delay(200); }
        }
        return false;
    }

    private static void ConvertFileToPlaceholder(string fullPath, string virtualPath)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            byte[] identity = FileIdentity.Encode(virtualPath);
            HRESULT hr = PInvoke.CfConvertToPlaceholder(handle, identity, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC);
            if (Ok(hr, $"CfConvertToPlaceholder('{virtualPath}')"))
                Log.Info($"  converted '{virtualPath}' to an in-sync placeholder");
        }
        catch (Exception ex)
        {
            Log.Error($"CfConvertToPlaceholder failed for '{virtualPath}'", ex);
        }
    }

    // --- Plumbing ---------------------------------------------------------------------------

    private static string ToVirtualPath(string fullPath)
    {
        if (fullPath.Length <= _rootPath.Length)
            return "/";
        string rel = fullPath[_rootPath.Length..].Replace('\\', '/');
        return string.IsNullOrEmpty(rel) ? "/" : rel;
    }

    /// <summary>A callback path may be volume-relative (<c>\Users\…</c>) or already drive-qualified.</summary>
    private static string ToFullPath(string volumeDosName, string pathFromCallback) =>
        pathFromCallback.Length >= 2 && pathFromCallback[1] == ':'
            ? pathFromCallback
            : volumeDosName + pathFromCallback;

    /// <summary>The full Win32 path of the item a callback refers to (its current on-disk location).</summary>
    private static unsafe string FullPathOf(CF_CALLBACK_INFO* info) =>
        ToFullPath(info->VolumeDosName.ToString(), info->NormalizedPath.ToString());

    /// <summary>The virtual (Dataverse) path of the item a callback refers to.</summary>
    private static unsafe string VirtualPathOf(CF_CALLBACK_INFO* info) =>
        ToVirtualPath(FullPathOf(info));

    private static async Task SkipAsync(Stream stream, long count)
    {
        if (count <= 0) return;
        byte[] buffer = new byte[(int)Math.Min(count, 81920)];
        long left = count;
        while (left > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
            if (read == 0) break;
            left -= read;
        }
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int want)
    {
        int total = 0;
        while (total < want)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, want - total));
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
