# Research — Integrating the Dataverse File Manager into Windows File Explorer

> **Status:** Design rationale / background for **Phase 4** of [`spec.md`](spec.md). The
> **canonical, actionable Phase 4 checklist lives in `spec.md` §6** (milestones 4A–4D); this
> document is the *why* behind it — technology comparison, the recommended approach, the .NET
> interop realities, and the gotchas. No code here is final.
>
> **Project state:** Phases 0–3 are **complete and validated** — the Dataverse table is
> provisioned and the **`DataverseFileManager` .NET 10 library is built and passing 10/10
> smoke tests** against the live org. It exposes a filesystem-shaped API
> (`IDataverseFileSystem`): `ListFolderAsync`, `GetItemAsync`, `CreateFolderAsync`,
> `UploadAsync`, `DownloadAsync`, `OpenReadAsync`, and `DeleteAsync` (recursive). `MoveAsync` /
> `RenameAsync` are stubbed. The Explorer phase surfaces those operations *inside Explorer* so a
> user browses `/Documents/2026/report.pdf` as if it were a local folder, with bytes fetched on
> demand from Dataverse → Azure Blob (SAS).

---

## 1. The candidate technologies

Windows offers four distinct ways to make a custom backend appear in File Explorer. They sit
at different layers of the stack and have very different cost/capability profiles.

| Approach | Layer | What Explorer sees | On-demand bytes | Effort | Fit for us |
|----------|-------|--------------------|-----------------|--------|------------|
| **Cloud Filter API (cfapi)** | FS minifilter (`cldflt.sys`) + WinRT | A real NTFS folder of **placeholder** files in the nav pane, OneDrive-style | **Yes** — native hydration callbacks | High | **Best fit** ✅ |
| **Projected File System (ProjFS)** | FS virtualization driver | A virtualized folder (used by VFS for Git) | Yes — projection callbacks | High | Viable, but aimed at "enumerate huge trees" not cloud sync |
| **Shell Namespace Extension (NSE)** | Shell / COM (`IShellFolder`) | A fully virtual node; you draw the whole UX | You implement everything | Very high | Overkill; legacy, brittle COM |
| **WebDAV → mapped network drive** | Network redirector | A mapped drive `Z:\` | Partial (whole-file cache) | Low | Quick demo only; poor UX, perf limits |

The spec already calls the target "the Cloud Filter API sync root" — this research confirms
that is the right primitive and documents *why*, plus the practical realities of doing it
from .NET.

---

## 2. Recommended approach — Cloud Filter API (cfapi)

### 2.1 Why cfapi

The Cloud Filter API (introduced Windows 10 1709, "Fall Creators Update") is the modern,
**purpose-built** API for exactly our scenario: a sync engine that presents remote files in
Explorer and downloads their contents on demand. It is what OneDrive, Dropbox, Google Drive,
etc. use today. Key wins:

- **Placeholders**: a file appears in Explorer consuming ~1 KB (just the FS header); the real
  bytes are pulled only when something opens it. This maps perfectly to our model where bytes
  live in Dataverse/Blob and metadata lives in the `cr19f_fileitem` table.
- **Three file states** Explorer renders natively: *placeholder* (cloud-only), *full*
  (hydrated, may be evicted), *pinned full* (always-offline). Standard cloud-status icons,
  no legacy icon-overlay shell extension needed.
- **Native nav-pane node**: registering a sync root creates a branded entry (custom name +
  icon) in Explorer's navigation pane — no namespace-extension COM code required.
- **Free Shell integration**: hydration progress UI, "Free up space"/"Always keep on this
  device" context-menu verbs, hydration toasts, thumbnails/metadata — all provided by the
  platform once you register.
- **App-compatibility**: placeholders are implemented with reparse points that the platform
  *hides* from normal apps, so `notepad`, `cmd`, the Photos app, etc. all hydrate
  transparently without code changes.

### 2.2 How it works end-to-end

There are two cooperating APIs:

1. **`Windows.Storage.Provider`** (WinRT) — configuration/registration: register the sync
   root with the OS so it shows up in Explorer.
2. **Cloud Filter API** (`cfapi.h`, native Win32) — the runtime: create placeholders, and
   respond to the platform's callbacks (most importantly "give me the bytes for this file").

**Lifecycle (in order of use):**

1. **Pick/create a local sync-root folder** on an **NTFS** volume (cfapi requires NTFS;
   `cldflt.sys` depends on NTFS-only features). e.g. `%USERPROFILE%\Dataverse`.
2. **Register the sync root** — `StorageProviderSyncRootManager.Register(...)` with a
   `StorageProviderSyncRootInfo` (display name, icon, sync-root ID, hydration policy,
   the folder path). This writes the registry keys and adds the nav-pane node.
   *(The equivalent native call is `CfRegisterSyncRoot`.)*
3. **Connect** — `CfConnectSyncRoot(path, callbackTable, ...)` opens the bidirectional
   channel and hands the platform your **callback table** (an array of
   `{callback type, function pointer}`). Returns a connection key.
4. **Populate placeholders** — `CfCreatePlaceholders(...)` creates the file/dir entries under
   the root from our Dataverse listing. Each placeholder carries a `FileIdentity` blob (we
   stash the Dataverse `RecordId`/path) + size + timestamps. The path **must** be inside the
   registered sync root or you get `STATUS_CLOUD_FILE_NOT_UNDER_SYNC_ROOT`.
   - Populate **lazily**: respond to the `FETCH_PLACEHOLDERS` callback when the user expands a
     folder, calling `ListFolderAsync(parentPath)` and creating placeholders for that level
     only. (Mirrors our `WHERE cr19f_parent_path = …` primitive.)
5. **Hydration** — when something opens a placeholder, the platform fires
   **`CF_CALLBACK_TYPE_FETCH_DATA`** (`OnFetchData`). We:
   - read the `FileIdentity` to recover the Dataverse path/RecordId,
   - call **`OpenReadAsync(remotePath)`** (→ `file_download` → SAS GET),
   - stream bytes back via **`CfExecute`** with
     `CF_OPERATION_TYPE_TRANSFER_DATA` (in chunks, with progress).
6. **Other callbacks** to handle for a complete provider: `VALIDATE_DATA`,
   `CANCEL_FETCH_DATA`, `NOTIFY_FILE_OPEN_COMPLETION`/`CLOSE`,
   `NOTIFY_DELETE`/`NOTIFY_RENAME` (to push deletes/renames back to Dataverse — these map to the
   library's `DeleteAsync` (**implemented**, recursive) and the still-stubbed
   `RenameAsync`/`MoveAsync`).
7. **Uploads** (local → cloud): a `DirectoryWatcher`/`ReadDirectoryChangesW` on the sync root
   detects new/modified files; convert them to placeholders with `CfConvertToPlaceholder`,
   then `UploadAsync` the bytes to Dataverse and mark them "in sync"
   (`CfSetInSyncState`/`CfUpdatePlaceholder`).
8. **Teardown** — `CfDisconnectSyncRoot` + `StorageProviderSyncRootManager.Unregister`.

**Key functions/structures cheat-sheet:**
`CfRegisterSyncRoot` / `StorageProviderSyncRootManager.Register` → `CfConnectSyncRoot`
(+ `CF_CALLBACK_REGISTRATION` table) → `CfCreatePlaceholders` (+ `CF_PLACEHOLDER_CREATE_INFO`)
→ `OnFetchData` (`CF_CALLBACK_INFO` / `CF_CALLBACK_PARAMETERS`) → `CfExecute`
(`CF_OPERATION_INFO` / `CF_OPERATION_PARAMETERS`, type `TRANSFER_DATA`) →
`CfUpdatePlaceholder` / `CfSetInSyncState` / `CfConvertToPlaceholder` → `CfDisconnectSyncRoot`.

### 2.3 The reference sample: **Cloud Mirror**

Microsoft's canonical sample is **CloudMirror** (Windows-classic-samples, C++). It "mirrors" a
local server folder into a sync-root client folder, implementing registration, placeholder
creation, fetch-data hydration with chunked progress, a context-menu verb, a thumbnail
provider, and a directory watcher. It is explicitly *not production code* (no robust error
handling) but is the best structural reference. Notable classes to mirror in our design:
`CloudProviderRegistrar`, `Placeholders`, `ShellServices`, `CloudProviderSyncRootWatcher`,
`FileCopierWithProgress` → our analogues would call into the existing `DataverseFileSystem`
(via the `IDataverseFileSystem` interface).

---

## 3. Calling cfapi from .NET (this is the real work)

cfapi is a **native Win32 API with function-pointer callbacks** — there is no first-party
managed wrapper. Three options, best first:

### 3.1 Hand-rolled P/Invoke via **CsWin32** (recommended)
- **`Microsoft.Windows.CsWin32`** is a source generator that emits accurate P/Invoke
  signatures + structs for any Win32 API (including `cfapi`) for your target arch — no runtime
  dependency. You list the functions/structs you want in a `NativeMethods.txt`; it generates
  `CfConnectSyncRoot`, `CfExecute`, the `CF_*` structs, etc.
- **`Windows.Storage.Provider`** WinRT registration is reachable from .NET via the
  **`Microsoft.Windows.SDK.NET`** projection (or CsWinRT). Target e.g.
  `net10.0-windows10.0.19041.0`.
- **Callback marshalling is the hard part**: the callback table holds unmanaged function
  pointers. Use `[UnmanagedCallersOnly]` static methods (works cleanly with our existing
  `static`/DI-light style) and keep them alive for the process lifetime. Inside the callback,
  hop to async land carefully (callbacks are on platform threads; don't block them — kick the
  `OpenReadAsync` + `CfExecute` streaming onto a worker and pump chunks).

### 3.2 Community C# engine: **styletronix/cfapiSync**
- MIT-licensed C# Cloud Files API sync engine — OneDrive-style folder, bidirectional sync,
  placeholder conversion. Crucially it already abstracts the backend behind an
  **`IServerProvider` interface** (local/UNC today; WebDAV/S3 planned). We could implement
  `IServerProvider` over our `IDataverseFileSystem` and get the cfapi plumbing for free.
- **Caveat:** author labels it "very early alpha" / their first C# project; known issues
  (esp. Office files). Good as a **structural reference and accelerator**, not a dependency to
  ship on blindly. Worth reading its callback/marshalling code even if we re-implement.

### 3.3 Other wrappers (lower value)
- **`CloudFilter.NET`** (JDanielSmith) — C++/CLI wrapper, MIT, but minimal (≈12 commits, no
  releases, 2 stars). Not production-ready; mostly a reference.
- **Commercial**: IT Hit **User File System** (userfilesystem.com) is a polished commercial
  .NET SDK that wraps cfapi (Windows) with a clean managed virtual-FS interface and samples.
  Fastest path to a robust product if budget allows; licensing cost is the trade-off.

**Recommendation:** prototype with **CsWin32 + WinRT registration**, using **CloudMirror** (C++)
and **cfapiSync** (C#) as references. Keep all cfapi code in a new `Explorer/` host project; it
consumes only the existing `IDataverseFileSystem` top layer (as the spec intends).

---

## 4. Packaging & identity — the non-obvious blocker

The Cloud Filter API and `StorageProviderSyncRootManager.Register` effectively require the
provider process to run with **package identity** (Desktop Bridge / MSIX). The docs state sync
engines are "designed to use the Desktop Bridge as an implementation requirement," and several
shell-integration hooks (custom states, thumbnails, context menus via
`IStorageProviderCopyHook` / `IExplorerCommand`) are registered through the package manifest.

Practical implications:
- Plan to ship the Explorer host as an **MSIX package** (or a **sparse package** that grants
  identity to an otherwise-unpackaged desktop exe — the lighter-weight option for adding
  identity to our existing .NET exe).
- The package manifest declares the sync-root/shell extensions. Budget time for MSIX signing
  (a code-signing cert or local dev cert) and the manifest authoring.
- This is the single biggest "surprise" cost beyond the callback marshalling. Decide
  MSIX-vs-sparse early.

---

## 5. Authentication — the architectural pivot (ties to spec §5)

The spec already flags this and it is the **most important cross-cutting change** for Phase 4:

- Today the library does **interactive OAuth** (auth code + PKCE, opens a browser, loopback
  listener). **A shell/sync-engine context cannot prompt a browser per file fetch** — hydration
  callbacks fire from background Explorer/Photos/etc. activity, unattended.
- Phase 4 must swap the `Auth/` layer for an **unattended credential**:
  - **Service principal / client credentials** (app-only) — simplest, but app-only Dataverse
    access has different permissioning than delegated `user_impersonation`.
  - **Refresh-token / silent token cache** (MSAL.NET with persistent cache) — keeps per-user
    delegated identity; one interactive sign-in at setup, silent refresh thereafter. This is
    likely the right balance for a per-user OneDrive-style drive.
- The spec's layering pays off here: the public API is auth-agnostic, so **only `Auth/`
  changes**. Revisiting MSAL.NET (deferred in the demo phase) becomes necessary here for
  durable token caching.

---

## 6. Mapping our existing API to cfapi callbacks

The library is already shaped for this; the mapping is nearly 1:1:

| Explorer / cfapi event | Library call | Notes |
|------------------------|--------------|-------|
| Nav-pane expand / `FETCH_PLACEHOLDERS` | `ListFolderAsync(parentPath)` | Lazy per-level population; `WHERE cr19f_parent_path = …` |
| Resolve a node's metadata | `GetItemAsync(path)` | For placeholder size/timestamps |
| Open a cloud-only file / `FETCH_DATA` (hydration) | `OpenReadAsync(remotePath)` | Stream SAS GET → `CfExecute(TRANSFER_DATA)` in chunks |
| New local file in sync root (watcher) | `UploadAsync(localPath, remotePath)` | Then `CfConvertToPlaceholder` + `CfSetInSyncState` |
| New folder in sync root | `CreateFolderAsync(path)` | + ancestor creation already handled |
| Delete / rename (callbacks) | `DeleteAsync` (done) / `RenameAsync` / `MoveAsync` | `DeleteAsync` implemented (recursive); `Rename`/`Move` still stubbed — Phase 4 (4C) implements |

**Gap to close before Phase 4 write-back:** `MoveAsync` / `RenameAsync` are still stubbed
(`DeleteAsync` is implemented, recursive) — these are exactly the write-back callbacks a real
sync engine receives. List/browse + on-demand read (`OpenReadAsync`) — the read path — are
already enough for a **read-only** Explorer drive (**milestone 4A**), the sensible first step.

---

## 7. Suggested phasing for the Explorer hook

> These four steps are the canonical **milestones 4A–4D in `spec.md` §6** — the spec holds the
> tracked checklist; this section is the narrative behind each.

1. **Spike (read-only) → 4A:** MSIX/sparse-package a host exe; register a sync root; lazily create
   placeholders from `ListFolderAsync`; implement only `OnFetchData` → `OpenReadAsync`.
   Goal: browse the Dataverse tree in Explorer and open a file (hydrate on demand).
   *This proves the whole concept and exercises every hard part except write-back.*
2. **Unattended auth → 4B:** replace interactive PKCE in `Auth/` with silent/refresh-token (MSAL)
   or service principal. Required for callbacks to work without a browser.
3. **Write-back → 4C:** directory watcher + upload on create/modify; wire delete/rename/move
   callbacks — `DeleteAsync` is implemented, so un-stub `RenameAsync`/`MoveAsync` in the library.
4. **Polish → 4D:** custom state icons, thumbnails (we have `cr19f_mime_type`), context-menu verbs,
   pinned/always-offline, hydration policy tuning, conflict handling, large-file chunking
   (ties to spec §7 large-file risk + SAS PUT chunking).

---

## 8. Risks / open questions specific to Explorer integration

- **NTFS-only**: the local sync root must be on an NTFS volume (cfapi limitation).
- **Package identity required**: MSIX or sparse package — decide early (see §4).
- **Callback threading/marshalling**: `[UnmanagedCallersOnly]` callbacks must not block; async
  bridging to our `Task`-based API needs care. Keep delegates rooted for process lifetime.
- **Unattended auth** (see §5) — gating dependency.
- **Concurrency / path uniqueness**: still enforced in-library (spec §3) until Dataverse
  alternate keys exist; Explorer multiplies concurrent operations, raising the stakes.
- **Eviction / "free up space"**: handle dehydration and re-hydration idempotently.
- **Large files**: Dataverse file-column size cap (~default, configurable) + chunked SAS
  transfer both surface here as real user actions, not test scripts.
- **cfapi minifilter CVEs**: keep Windows patched (a 2025 `cldflt.sys` privilege-escalation
  TOCTOU was reported and fixed) — not our bug, but relevant to the platform we depend on.

---

## 9. Decision summary

- **Use the Cloud Filter API.** It is purpose-built for "remote files, hydrate on demand in
  Explorer," gives us the nav-pane node + status UI + context menus for free, and matches the
  library's existing placeholder-shaped design.
- **Implement it from .NET with CsWin32** for the native surface + WinRT for registration;
  lean on **CloudMirror** (C++) and **styletronix/cfapiSync** (C#) as references; evaluate
  **IT Hit User File System** if a commercial, supported wrapper is preferred.
- **Two prerequisites dominate the effort:** (1) **package identity** (MSIX/sparse) and
  (2) **unattended authentication** in the `Auth/` layer. Neither requires changes above the
  library's public API.
- **Start read-only** (`OnFetchData` → `OpenReadAsync`) to de-risk, then add write-back.

---

## Sources

- [Build a Cloud Sync Engine that Supports Placeholder Files (cfapi) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)
- [Cloud Sync Engines portal — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/cfapi/cloud-files-api-portal)
- [Cloud Filter API reference (`cfapi.h`) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/_cloudapi/)
- [`CfRegisterSyncRoot` — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot)
- [`CfCreatePlaceholders` — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfcreateplaceholders)
- [`CfConnectSyncRoot` — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot)
- [Integrate a Cloud Storage Provider (sync root registration + nav pane) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage)
- [`StorageProviderSyncRootManager` — Microsoft Learn](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager)
- [`Windows.Storage.Provider` namespace — Microsoft Learn](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider)
- [CloudMirror sample (Windows-classic-samples)](https://github.com/Microsoft/Windows-classic-samples/tree/master/Samples/CloudMirror)
- [`Microsoft.Windows.CsWin32` — NuGet](https://www.nuget.org/packages/Microsoft.Windows.CsWin32)
- [styletronix/cfapiSync — C# Cloud Files API sync engine (GitHub)](https://github.com/styletronix/cfapiSync)
- [JDanielSmith/CloudFilter.NET (GitHub)](https://github.com/JDanielSmith/CloudFilter.NET)
- [IT Hit User File System — commercial .NET virtual FS SDK](https://www.userfilesystem.com/)
- [Microsoft.Windows.ProjFS (Projected File System managed API) — NuGet](https://www.nuget.org/packages/Microsoft.Windows.ProjFS)
- [ProjFS-Managed-API (GitHub)](https://github.com/microsoft/ProjFS-Managed-API)
