# Spec — Dataverse MCP File Manager

## 1. Purpose

Demonstrate that **Microsoft Dataverse can act as a file server** through the Dataverse
MCP server's file-transfer capabilities.

The end goal is to hook this up to **Windows File Manager (Explorer)** so a user browses,
uploads, and downloads files that physically live in Dataverse.

The project is built as a reusable **C# / .NET 10 library** that

1. handles **authentication** against the Dataverse MCP server, and
2. exposes a clean, filesystem-shaped API for **file operations** (list/browse, upload,
   download, plus folder create + recursive delete),

backed by **dedicated Dataverse tables** purpose-built for file management — modelled so the
virtual paths project into Explorer without a redesign.

> **Status (current).** Phases 0–3 are **complete and validated end-to-end** against the live
> org: Dataverse table provisioned, library built (0 warnings), and a self-cleaning smoke test
> passing **10/10**. **Phase 4 — the Windows File Explorer integration — is the next phase**,
> fully planned in §6 below; its design rationale and technology evaluation live in the companion
> [`explorer-integration-research.md`](explorer-integration-research.md). This document is the
> canonical spec + plan; that one is the background research.

> **Usage model — MCP as a pure API.** The Dataverse MCP server is consumed as a plain
> **programmatic API / RPC transport**, *not* from an LLM or agent. The C# library is a
> deterministic **MCP client**: each tool is invoked directly with explicit, known arguments
> — no natural-language prompting, no model-driven tool selection, no agent in the loop. MCP
> here is simply the wire protocol to Dataverse's file/data operations.

---

## 2. How file transfer works in the Dataverse MCP server (verified)

Confirmed by inspecting the live `DataverseMCP` server tools and schemas.

### Storage model
Two options exist; we use **file columns** (modern, clean, per-record metadata):

- **File column** (type `FILE`) on a table — one file per column per record
  (reference example: `skillresource.filecontent`).
- *(Rejected for this project)* `annotation`/Notes attachments — weaker structure, no
  custom per-file metadata.

### Transfer is brokered, not proxied
The MCP tools only hand back **SAS URLs**; the actual bytes move over a **direct HTTPS
PUT/GET to Azure Blob storage**, outside the MCP channel.

**Upload (3 steps + metadata):**
1. `create_record` — create the row first (a record must exist before a file attaches).
2. `init_file_upload(tablename, recordId, fileAttributeName, fileName)` → returns
   **SAS URL + `continuationToken`**.
3. HTTP **PUT** the file bytes to the SAS URL.
4. `commit_file_upload(continuationToken, fileName)`.
5. `update_record` — write size/extension metadata.

**Download (1 step + fetch):**
1. `file_download(tablename, recordId, fileAttributeName)` → returns **SAS URL**.
2. HTTP **GET** the bytes.

### Supporting tools
- `create_table` / `update_table` — provision tables; supports a native `file` column type.
- `read_query` — restricted `SELECT` (equality `WHERE`, `ORDER BY`, `TOP`, `JOIN`;
  **no** subqueries/DISTINCT/OFFSET/CAST). Enough to enumerate a folder by `parentpath`.
- `create_record` / `update_record` / `delete_record`.
- `search_data` — full-text search **inside file contents** (future "find file mentioning X").

### Consequences for design
- A file column holds **exactly one file per record** → a folder of N files = N records.
- The library needs **both** an **MCP client** (broker calls) **and** an **HTTP client**
  (SAS blob transfer).
- Environment uses the **`cr19f_`** publisher prefix (existing: `cr19f_legoset`, `cr19f_pizza`…).

---

## 3. Data model — `cr19f_fileitem`

A **single table** where every row is a node — **file or folder**. Folders are real records
(not merely implied by path strings) so we get empty folders, folder metadata, and a trivial
"enumerate this directory" query — the exact primitive Explorer / the Cloud Filter API needs.

**Verified provisioned logical names** (Dataverse derived these from the display names —
note the **underscores**; captured via `describe` after creation):

| Logical name              | Type / size       | Meaning                                                  |
|---------------------------|-------------------|----------------------------------------------------------|
| `cr19f_fileitemid` *(PK)* | GUID              | Primary key                                              |
| `cr19f_name` *(primary)*  | NVARCHAR(850) NN  | Leaf name, e.g. `report.pdf` or `2026`                   |
| `cr19f_path`              | NVARCHAR(1000)    | Full virtual path, e.g. `/Documents/2026/report.pdf`     |
| `cr19f_parent_path`       | NVARCHAR(1000)    | Containing folder's path, e.g. `/Documents/2026`         |
| `cr19f_is_folder`         | BIT               | `true` = folder, `false` = file                          |
| `cr19f_file_content`      | FILE              | The bytes (files only)                                   |
| `cr19f_size_bytes`        | INT               | File size in bytes                                        |
| `cr19f_extension`         | NVARCHAR(1000)    | e.g. `.pdf` (files only)                                  |
| `cr19f_mime_type`         | NVARCHAR(1000)    | Our MIME type column (the file column stores its own,    |
|                           |                   | defaulting to `application/octet-stream`)                |
| system: `createdon`, `modifiedon`, `createdby`, `modifiedby` | — | Explorer "Date modified", etc. |

### Path conventions
- Root is `/`.
- A node's `path` has **no trailing slash** (except root). `parentpath` = the parent's `path`.
- Root's `parentpath` = `""` (empty) / null.
- **Enumerate a folder:** `WHERE cr19f_parent_path = '<folder path>'`
  `ORDER BY cr19f_is_folder DESC, cr19f_name`. *(Verified working.)*
- **Resolve one node:** `WHERE cr19f_path = '<path>'`.

### Known modelling constraints (tracked, see §7)
- String length **resolved**: columns defaulted to **NVARCHAR(1000)** — ample for paths.
- `create_table` doesn't expose **alternate keys** → `path` uniqueness enforced **in the
  library** (check-before-create), revisited later with a real alt-key for concurrency.

---

## 4. Library design (C# / .NET 10 LTS)

> **MCP client = official SDK.** Uses the official **ModelContextProtocol C# SDK**
> (`ModelContextProtocol` / `ModelContextProtocol.Core` NuGet) as the MCP client. Its
> `HttpClientTransport` speaks **remote Streamable HTTP**, and its built-in **`OAuth`**
> options (auth code + PKCE, with an `AuthorizationRedirectDelegate` doing the
> browser + loopback listener) cover our auth flow — so **no MSAL.NET needed** for the
> demo phase. The `Mcp/` layer wraps `McpClient.CallToolAsync(...)` in typed methods.


Solution `DataverseFileManager.slnx` (.NET 10). Layered so the eventual Explorer hook
consumes only the top layer (`IDataverseFileSystem`). **As built:**

```
DataverseFileManager.slnx
├─ src/DataverseFileManager/                  class library (net10.0)
│  ├─ Auth/DataverseMcpConnection.cs   HttpClientTransport + OAuth (PKCE loopback)
│  ├─ Mcp/DataverseMcpClient.cs        typed wrappers over McpClient.CallToolAsync (+ DataverseMcpException)
│  ├─ Blob/SasBlobClient.cs            HttpClient PUT (x-ms-blob-type)/GET to SAS URLs
│  ├─ Model/                           FileItem, DataverseFileManagerOptions, FileItemColumns
│  ├─ Paths/VirtualPath.cs             normalize, parent/leaf, ancestors, SQL-escape
│  ├─ IDataverseFileSystem.cs          ← public surface
│  └─ DataverseFileSystem.cs           orchestrates Mcp + Blob + Paths
└─ samples/DataverseFileManager.ConsoleDemo/  manual harness (smoke test / list / tree)
```

### Public API (`IDataverseFileSystem`)
```csharp
Task<IReadOnlyList<FileItem>> ListFolderAsync(string path, CancellationToken ct = default);
Task<FileItem?>               GetItemAsync(string path, CancellationToken ct = default);
Task<FileItem>                CreateFolderAsync(string path, CancellationToken ct = default);
Task<FileItem>                UploadAsync(string localPath, string remotePath, CancellationToken ct = default);
Task                          DownloadAsync(string remotePath, string localPath, CancellationToken ct = default);
Task<Stream>                  OpenReadAsync(string remotePath, CancellationToken ct = default); // on-demand hydration

Task DeleteAsync(string path, ...);   // file, or folder + descendants (recursive)

// Anticipated for Explorer phase (stubbed now, not implemented this phase):
// Task MoveAsync(string from, string to, ...);
// Task RenameAsync(string path, string newName, ...);
```

`FileItem`: `Path, Name, ParentPath, IsFolder, SizeBytes, Extension, MimeType, CreatedOn,
ModifiedOn, RecordId(Guid)`.

### Operation → MCP/HTTP mapping
| API call            | Steps |
|---------------------|-------|
| `ListFolderAsync`   | `read_query` `WHERE cr19f_parent_path = …` |
| `GetItemAsync`      | `read_query` `WHERE cr19f_path = …` |
| `CreateFolderAsync` | `create_record` (`cr19f_is_folder=true`); auto-create missing ancestors |
| `UploadAsync`       | `create_record` → `init_file_upload`(`cr19f_file_content`) → **PUT** SAS (header `x-ms-blob-type: BlockBlob`) → `commit_file_upload` → `update_record` (size/ext); auto-create parent folders |
| `DownloadAsync`     | `GetItemAsync` → `file_download` → **GET** SAS → write local |
| `OpenReadAsync`     | `file_download` → **GET** SAS → return stream |

> **Verified transfer details (Phase 0 round-trip):** the SAS URL is an Azure **blob** SAS
> (`sr=b`, `sp=rw`); the upload PUT **must** send header `x-ms-blob-type: BlockBlob`.
> Download is a plain GET on a read SAS (`sp=r`). `file_download` also returns `FileName`,
> `FileSizeInBytes`, and `MimeType` alongside the `SasUrl`.

---

## 5. Authentication

The library authenticates **through the MCP server**, consumed as a pure API (§1).

### Connection facts
Per-deployment values (org URL, `client_id`) live in `appsettings.json` / env vars, not in source —
see the README. Placeholders below; substitute your own.

| Setting              | Value |
|----------------------|-------|
| MCP endpoint         | `https://YOUR-ORG.crm.dynamics.com/api/mcp` |
| Transport            | Remote **Streamable HTTP** MCP server |
| Authorization server | **Microsoft Entra ID** (Dataverse org tenant) |
| OAuth flow           | **Authorization Code + PKCE**, **public client** (no client secret) |
| Dynamic Client Reg.  | **Not** supported → `client_id` is supplied explicitly |
| `client_id`          | `YOUR-ENTRA-APP-CLIENT-ID` |
| Redirect URI         | `http://localhost/callback` (loopback, ephemeral port) |
| Target resource/scope| Dataverse org `https://YOUR-ORG.crm.dynamics.com/.default` (resolves to **Dynamics CRM → `mcp.tools`**) |

### App registration requirements
See [Register a custom Microsoft Entra app](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp-other-clients#register-a-custom-microsoft-entra-app).
- API permission **Dynamics CRM → `mcp.tools`** (the remote `/api/mcp` endpoint requires it; consented).
- Platform **"Mobile and desktop applications"** with redirect URI `http://localhost/callback`, and
  **"Allow public client flows" = Yes** (this client uses the interactive PKCE loopback flow).
- **Allow the client ID** in the env's **Power Platform admin center → … → Dataverse Model Context
  Protocol → Advanced Settings** (Is Enabled = Yes) — without it the endpoint rejects the app.

### Flow (handled by the SDK's `HttpClientTransport.OAuth`)
1. `HttpClientTransport` is configured with `Endpoint`, `OAuth.ClientId`,
   `OAuth.RedirectUri` (`http://localhost:<fixedPort>/callback`), and an
   `AuthorizationRedirectDelegate`.
2. On first call the SDK runs **auth code + PKCE**: opens the system browser to Entra and a
   **loopback `HttpListener`** catches the code at `/callback` (Entra ignores the loopback
   port, so registered `http://localhost/callback` matches the fixed-port runtime URI).
3. SDK exchanges code → access/refresh token, caches, and refreshes; sends
   `Authorization: Bearer …` on every Streamable-HTTP MCP call.

> Because DCR is unsupported, set `OAuth.ClientId` (no `DynamicClientRegistration`), and
> leave the client secret empty (public client → PKCE). `OAuth.Scopes` may need the
> Dataverse scope set explicitly if not auto-discovered from the server's resource metadata.
> MSAL.NET is **not** required for this phase; revisit only if richer token caching is needed.

### Explorer phase (later)
Swap interactive acquisition for an **unattended** credential (service principal /
client-credentials or on-behalf-of) behind the `Auth/` layer — a shell extension can't
prompt a browser per call. The public API surface is auth-agnostic, so nothing above
`Auth/` changes.

---

## 6. Implementation plan (phased)

### Phase 0 — Discover & provision *(do first; table creation needs sign-off)*
- [x] Determine the MCP server endpoint + auth scheme — **done, see §5**.
- [x] Register redirect URI `http://localhost/callback` + public-client settings on the app reg (user).
- [x] `create_table` **`fileitem`** with the **simple columns only** (no file column yet).
- [x] `update_table` to add **`cr19f_file_content` (file)** as a **separate step** (after MCP reconnect).
- [x] `describe` the table → captured real logical names (see §3).
- [x] Seeded `root` + `/Documents`; created a file record, **uploaded** `hello.txt`, **listed**
      `/Documents`, and **downloaded** it back — **byte-identical round-trip confirmed**.
- **Phase 0 complete.** ✅

> **Gotcha (observed) — file columns + per-connection metadata cache.** The MCP server
> caches entity metadata **per connection** and does **not** refresh it mid-session after a
> metadata change. Adding a `file` column (which builds a relationship and needs the entity
> already in cache) therefore fails right after the entity is created:
> - `create_table` *with* a file column → rolls back entirely
>   (*"EntityId … not found in the MetadataCache"*).
> - `create_table` simple columns only → commits fine.
> - `update_table` to add the file column **in the same session** → fails
>   (*"Unable to retrieve referenced entity to create relationship for a file type attribute"*).
>
> **Working recipe:** (1) `create_table` with simple columns only → (2) **reconnect MCP**
> (`/mcp`) so the new entity enters the cache → (3) `update_table` to add the `file` column →
> (4) reconnect again if the next step can't see the new column. Budget a reconnect after
> every metadata mutation that a later call depends on.

### Phase 1 — Library skeleton — **complete** ✅
- [x] .NET 10 solution (`DataverseFileManager.slnx`) + class library (net10.0) + console demo;
      added `ModelContextProtocol` 1.4.0; `Model/` (FileItem, Options, FileItemColumns), `Paths/`.
- [x] `Auth/DataverseMcpConnection` — `HttpClientTransport` + `ClientOAuthOptions`
      (`ModelContextProtocol.Authentication`) with ClientId + loopback redirect delegate.
- [x] `Mcp/DataverseMcpClient` typed wrappers over `McpClient.CallToolAsync`;
      `Blob/SasBlobClient` SAS PUT/GET. **Builds clean: 0 errors, 0 warnings.**

### Phase 2 — File operations — **code written; pending live validation**
- [x] `ListFolderAsync`, `GetItemAsync` (read_query by `parent_path` / `path`).
- [x] `CreateFolderAsync` (+ ancestor auto-creation, idempotent).
- [x] `UploadAsync` (create_record → init → PUT `BlockBlob` → commit → size update).
- [x] `DownloadAsync` / `OpenReadAsync`.
- [ ] Exercise each against the live org (blocked on first interactive OAuth — Phase 3).

### Phase 3 — Validation
- [x] **Auth flow verified manually** by the user (interactive PKCE loopback).
- [x] Built a self-cleaning **smoke-test harness** in the console demo: create-nested-folders
      (+ ancestors), idempotent create, upload+metadata, GetItem (hit/miss), list files,
      list subfolders, download (byte-identical), OpenRead (content match), recursive delete.
      Modes: `(default)` smoke test, `list <path>`, `tree <path>`.
- [x] **Ran** the harness against the live org → **10 passed, 0 failed**. ✅
      **Phases 1–3 complete; library validated end-to-end.**
- [ ] (Optional) promote the harness to xUnit integration tests.

> `DeleteAsync` was **brought forward** from the Explorer phase to make the harness
> self-cleaning (low-risk; file = delete record, folder = recursive). `Move`/`Rename` remain
> stubbed. The SDK uses an in-memory token cache, so each process run re-runs interactive OAuth.

> **Build notes:** .NET 10 `dotnet new sln` emits the XML **`.slnx`** format. OAuth types
> (`ClientOAuthOptions`, `AuthorizationRedirectDelegate`) live in the
> **`ModelContextProtocol.Authentication`** namespace (not `.Client`).

### Phase 4 — Windows File Explorer integration *(next phase)*

> **Canonical actionable plan below.** Full technology comparison (cfapi vs ProjFS vs NSE vs
> WebDAV), API/callback walk-through, .NET-interop options, packaging realities, and risks are
> in **[`explorer-integration-research.md`](explorer-integration-research.md)**.

**Approach (decided):** **Cloud Filter API (cfapi)** sync root — files appear as OneDrive-style
**placeholders** in Explorer's nav pane and are **hydrated on demand**. Implemented from .NET via
**`Microsoft.Windows.CsWin32`** (native cfapi P/Invoke) + **`Windows.Storage.Provider`** WinRT
(sync-root registration). A **new host project** `src/DataverseFileManager.Explorer/`
(`net10.0-windows10.0.19041.0`) consumes **only** the existing `IDataverseFileSystem` top layer.
Structural references: **CloudMirror** (C++ sample) and **styletronix/cfapiSync** (C#).

**Two prerequisites dominate the effort — resolve before/early:**
1. **Package identity** — cfapi + `StorageProviderSyncRootManager.Register` require the process to
   run with package identity. Choose **MSIX** vs **sparse package** (sparse adds identity to the
   existing desktop exe — lighter weight).
2. **Unattended authentication** — hydration callbacks fire from background Explorer activity and
   **cannot open a browser**. The current interactive PKCE in `Auth/` must be swapped for a silent
   credential. Recommended: **MSAL.NET with a persistent token cache** (one interactive sign-in at
   setup, silent refresh thereafter — keeps per-user delegated identity). Alternative: service
   principal / client-credentials (app-only). **Only the `Auth/` layer changes** — the public API
   is auth-agnostic.

**Decisions (captured 2026-06-26):**
- [x] Identity packaging: **sparse package** — adds identity to the existing .NET desktop exe.
      Confirmed sufficient for the branded experience: the OneDrive-style **nav-pane node icon**
      comes from `StorageProviderSyncRootInfo.IconResource` at registration (not the manifest), and
      per-file **cloud-status badges** are rendered natively once registered as a cloud provider —
      both work identically under sparse. Only *custom* file-**state** overlays (a 4D polish item)
      would benefit from full MSIX.
- [x] Unattended auth: **MSAL.NET refresh-token persistent cache** (per-user delegated, keeps
      `user_impersonation`) — one interactive sign-in at setup, silent refresh thereafter.
- [x] First milestone scope: **read-only drive first** (4A).

**Milestone 4A — Read-only spike** — ✅ **COMPLETE & LIVE-VALIDATED** *(branded Dataverse node renders
in Explorer; lazy per-folder population + on-demand hydration confirmed against the live org)*
- [x] Create `src/DataverseFileManager.Explorer/` host (`net10.0-windows10.0.19041.0`, references only
      `IDataverseFileSystem`); added `Microsoft.Windows.CsWin32` 0.3.298; authored `NativeMethods.txt`
      (cfapi funcs/structs). **Builds clean (0 warnings); publishes win-x64.**
- [x] Icon: `dvicon.png` → multi-res `dvicon.ico` (256/48/32/16) in `Assets/`, wired as the nav-pane
      `StorageProviderSyncRootInfo.IconResource`.
- [x] Sync-root folder: code targets NTFS `%USERPROFILE%\Dataverse` (created on register).
- [x] **Sparse package authored** (`Package/AppxManifest.xml` + MSIX logos + `setup-sparse-package.ps1`:
      self-signed dev cert → publish → MakeAppx pack → SignTool → `Add-AppxPackage -ExternalLocation`,
      with `-Unregister` / `-Full` modes). **Installed once (elevated); identity confirmed working.**
- [x] Register the sync root (`StorageProviderSyncRootManager.Register`) → `SyncRootRegistrar.cs`
      (register/unregister; per-user sync-root id). **Branded node + green icon appear in Explorer.**
- [x] `CfConnectSyncRoot` with a callback table (`CloudProvider.cs`); two `[UnmanagedCallersOnly]`
      `Stdcall` callbacks; the `CF_CALLBACK_REGISTRATION[]` table is **pinned** (static `GCHandle`)
      for process lifetime. cfapi types via CsWin32 (`allowMarshaling:false` → function pointers).
- [x] Lazy placeholders: on `FETCH_PLACEHOLDERS` → `ListFolderAsync(virtualPath)` →
      `CfExecute(TRANSFER_PLACEHOLDERS)` (the virtual path is stashed in the `FileIdentity` blob via
      `FileIdentity.cs`). Folders keep on-demand population (no `DISABLE_ON_DEMAND_POPULATION`).
- [x] Hydration: on `CF_CALLBACK_TYPE_FETCH_DATA` → decode identity → `OpenReadAsync` → stream via
      `CfExecute(TRANSFER_DATA)` in 1 MB chunks; the callback hops to `Task.Run` so the platform
      thread never blocks. `ParamSize` computed as `offsetof(Anonymous)+sizeof(member)`.
- [x] Teardown: `CfDisconnectSyncRoot` + frees the pinned table; `unregister` verb calls `Unregister`.
- **First-run gotcha (fixed):** must set the **operation-level**
  `CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION` on the (final) TRANSFER batch to
  mark a directory fully populated — without it the platform re-fires `FETCH_PLACEHOLDERS` in a tight
  loop. Distinct from the *placeholder-level* flag of the same name (left off so child folders stay lazy).
- ✅ **EXIT MET:** browsed the Dataverse tree in Explorer and opened a file (hydrate on demand), read-only,
  against the live org. Diagnostics: `Log.cs` → `%LOCALAPPDATA%\DataverseFileManager\explorer.log`.

**Milestone 4B — Unattended auth** — *planned; see [`phase4b-auth-plan.md`](phase4b-auth-plan.md)*
- [ ] Replace interactive PKCE in `Auth/` with **MSAL.NET + DPAPI persistent cache** (per-user
      delegated). **Mechanism resolved:** bypass `ClientOAuthOptions` (`OAuth = null`) and feed the
      MCP `HttpClientTransport` a custom `HttpClient` whose `DelegatingHandler` stamps a silent
      MSAL bearer token per request (MSAL auto-refreshes). Add `Microsoft.Identity.Client` +
      `…Extensions.Msal`; additive `AuthMode`/`AllowInteractiveSignIn` options keep the public API
      and console-demo behavior unchanged; new `login` verb does the one-time interactive sign-in.
- [ ] **First step (de-risk):** spike-validate the MCP endpoint accepts an MSAL `.default`/
      `user_impersonation` token (compare `aud` to a working SDK-flow token) before refactoring.
- **Exit:** 4A works fully unattended — callbacks fetch bytes with no browser prompt.

**Milestone 4C — Write-back** — **code-complete; builds clean (0 warnings); pending live validation**
- [x] **`MoveAsync` + `RenameAsync` implemented** in the library (`DataverseFileSystem`): a shared
      `RelocateAsync` rewrites `name`/`path`/`parent_path`; for folders it recursively relocates the whole
      subtree (gathered via the `parent_path` primitive, since `read_query` has no prefix match). Guards
      self-nesting + destination collisions. Smoke test extended with file-rename, file-move, and
      folder-rename-subtree checks (run the console demo to validate against the org).
- [x] `FileSystemWatcher` on the sync root (`CloudProvider`): new local **file** → wait-until-stable →
      `UploadAsync` → `CfConvertToPlaceholder(MARK_IN_SYNC)`; new **folder** → `CreateFolderAsync`.
      Feedback loops avoided by skipping reparse-point (placeholder) items + an in-flight dedupe set.
- [x] `NOTIFY_DELETE` → `DeleteAsync` → `CfExecute(ACK_DELETE)`; `NOTIFY_RENAME` → `MoveAsync(from,to)`
      → `CfExecute(ACK_RENAME)` (ACK failure on error keeps local+cloud in step).
- [x] **In-place edits** (`Changed` event): a save dirties the placeholder, so the watcher probes
      `CfGetPlaceholderInfo` and re-uploads only when `InSyncState == NOT_IN_SYNC`, then
      `CfSetInSyncState(IN_SYNC)`. The in-sync check is the discriminator that keeps our own hydration
      writes (which leave the file IN_SYNC) from looping back as uploads. `UploadAsync` was made an
      **upsert** (reuse the existing record at the path; folder-at-path is a conflict) so a re-upload
      replaces content instead of creating a duplicate row. Smoke test covers the upsert path.
- **Exit (pending live run):** create / edit / rename / delete in Explorer round-trips back to Dataverse.
- *Known v1 gaps:* new local folders aren't converted to placeholders (left as plain dirs; children still
  upload); atomic-save editors (temp-file + rename-over) round-trip via delete+create rather than an
  in-place edit, so the file gets a fresh record id; no cloud→local refresh (direct Dataverse edits don't
  appear in Explorer — see 4E below).

**Milestone 4D — Polish**
- [ ] Custom state icons; thumbnails (use `cr19f_mime_type`); context-menu verbs; pinned /
      always-offline; hydration-policy tuning.
- [ ] Eviction / "Free up space" (idempotent dehydrate/rehydrate).
- [ ] **Large-file chunking** for SAS PUT/GET (ties to §7).
- [ ] Conflict handling; path-uniqueness under concurrency (consider a Dataverse **alternate key**
      on `cr19f_path`).

**Milestone 4E — Cloud→local sync** *(new; surfaced during 4C)* — make changes made **directly in
Dataverse** appear in Explorer. Today there is **no cloud→local push**: once a folder is marked fully
populated (4A loop fix), Windows caches its enumeration and won't re-fetch, so server-side additions stay
invisible until the local placeholder is cleared. A real engine needs a background **poller** over a
`modifiedon` watermark that reconciles deltas via `CfCreatePlaceholders` (new) / `CfUpdatePlaceholder`
(changed) / `CfDeletePlaceholders` (removed). Distinct from 4C (which is local→cloud). Not yet scoped.

**Library gaps for Phase 4:** ~~`MoveAsync` + `RenameAsync`~~ **now implemented** (4C). `DeleteAsync`
implemented. Still consider an alternate key on `cr19f_path` for concurrency.

---

## 7. Open questions / risks

**Resolved**
- ~~String column length~~ → columns provisioned as **NVARCHAR(1000)**; ample for paths.
- ~~`read_query` literal handling~~ → `VirtualPath.EscapeSqlLiteral` doubles single quotes;
  exercised by the smoke test.
- ~~Interactive-vs-validation~~ → library validated 10/10 against the live org.

**Open (carried into Phase 4)**
- **Unattended auth** — interactive PKCE today; Phase 4 needs silent/refresh-token or service
  principal (Phase 4 §4B; research §5). Highest-priority Phase 4 prerequisite.
- **Package identity** — MSIX vs sparse; required for cfapi (Phase 4 §4; research §4).
- **Path uniqueness / concurrency** — enforced in-library (check-before-create) until a Dataverse
  **alternate key** on `cr19f_path` exists; Explorer multiplies concurrent ops.
- **Large files** — Dataverse file-column size cap (default ~32 MB, configurable) + chunked SAS
  PUT/GET surface as real user actions in Explorer (Phase 4 §4D).
- **`Move`/`Rename` not yet built** — needed for Phase 4 write-back (§4C).
- **NTFS-only** sync root, callback threading/marshalling, eviction idempotency — Explorer-specific
  risks enumerated in research §8.
