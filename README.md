<p align="center">
  <img src="dvicon.png" alt="Dataverse File Manager" width="96" height="96" />
</p>

<h1 align="center">Dataverse File Manager</h1>

<p align="center">
  Use <b>Microsoft Dataverse as a file server</b> over its MCP server, and surface it as a
  branded drive in <b>Windows File Explorer</b> via the Cloud Filter API.
</p>

---

## What it is

A C# / .NET 10 solution in two layers:

- **`DataverseFileManager`** — a cross-platform class library that exposes a familiar file-system API
  (`List`/`GetItem`/`CreateFolder`/`Upload`/`Download`/`OpenRead`/`Delete`/`Move`/`Rename`) backed by a
  single Dataverse table. It talks to the **Dataverse MCP server as a pure API** (no agent / LLM in the
  loop) using the official [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
  SDK, and moves file bytes directly over the SAS URLs that the MCP server brokers.
- **`DataverseFileManager.Explorer`** — a **Windows-only** host that registers a sync root with the
  shell and implements the [Cloud Filter API](https://learn.microsoft.com/windows/win32/cfapi/cloud-filter-api-portal)
  (cfapi) callbacks, so the Dataverse-backed files appear under a branded **"Dataverse"** node in
  Explorer with on-demand hydration and local write-back.

> **Status:** the library and read/write Explorer integration are functional against a live org. See
> [`spec.md`](spec.md) for the detailed, phased design record and current milestone status.

## How it works

```
Windows Explorer ──cfapi──> DataverseFileManager.Explorer ──> DataverseFileManager (library)
                                                                   │
                                            MCP (control plane) ────┤────> Dataverse MCP server
                                            SAS PUT/GET (bytes) ────┘       (brokers SAS URLs)
```

- **Control plane:** record CRUD and SAS-URL brokering go through the MCP server (`read_query`,
  `create_record`, `init_file_upload`, `commit_file_upload`, …).
- **Data plane:** file bytes are streamed **directly** to/from Azure Blob storage via the brokered SAS
  URL (the MCP server does not proxy the bytes).
- **Explorer:** files are placeholders, hydrated on first read; local create / edit / rename / delete are
  written back to Dataverse.

## Repository layout

| Path | Description |
|------|-------------|
| `src/DataverseFileManager/` | The core library (net10.0). |
| `src/DataverseFileManager.Explorer/` | Windows cfapi host + sparse-package build. |
| `samples/DataverseFileManager.ConsoleDemo/` | A self-cleaning end-to-end smoke test / manual harness. |
| `spec.md` | Canonical spec and phased plan. |
| `explorer-integration-research.md` | Cloud Filter API design rationale. |
| `phase4b-auth-plan.md` | Plan for unattended (MSAL persistent-cache) auth. |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A **Microsoft Dataverse** environment with the **MCP server** enabled.
- For the Explorer host only: **Windows 10 2004 (19041)+**, x64 or ARM64.
- An **Entra ID app registration** (public client) — see [Setup](#setup).

## Setup

### 1. Provision the Dataverse table

Create a single table (every row is a node — file *or* folder). The library's column logical names are
defined in [`FileItemColumns.cs`](src/DataverseFileManager/Model/FileItemColumns.cs) and default to the
`cr19f` publisher prefix:

| Logical name | Type | Meaning |
|--------------|------|---------|
| `cr19f_fileitemid` *(PK)* | GUID | Primary key |
| `cr19f_name` *(primary)* | Text (850) | Leaf name, e.g. `report.pdf` |
| `cr19f_path` | Text (1000) | Full virtual path, e.g. `/Documents/2026/report.pdf` |
| `cr19f_parent_path` | Text (1000) | Containing folder's path |
| `cr19f_is_folder` | Yes/No | `true` = folder, `false` = file |
| `cr19f_file_content` | File | The bytes (files only) |
| `cr19f_size_bytes` | Whole number | File size in bytes |
| `cr19f_extension` | Text (1000) | e.g. `.pdf` (files only) |
| `cr19f_mime_type` | Text (1000) | MIME type |

> **Publisher prefix:** if your publisher prefix isn't `cr19f`, either provision the table with that
> prefix or update the constants in `FileItemColumns.cs` to match. The table *name* alone is configurable
> (`TableName` setting), but the column logical names are compile-time constants in v1.
>
> **Provisioning order matters:** the Dataverse MCP `create_table` rolls back if a `file` column is
> included up front. Create the simple columns first, **reconnect the MCP session**, then `update_table`
> to add `cr19f_file_content`. See `spec.md` §Phase 0 for the verified recipe.

### 2. Register the Entra ID app

This project connects to the Dataverse MCP server's **remote endpoint** (`/api/mcp`), which requires a
custom Entra app. Follow Microsoft's guide —
[Register a custom Microsoft Entra app](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp-other-clients#register-a-custom-microsoft-entra-app)
— summarized here:

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/): **Identity → Applications →
   App registrations → New registration**. Name it (e.g. *Dataverse MCP Client*), choose your supported
   account types, and **Register**. Note the **Application (client) ID**.
2. **API permissions → Add a permission → Microsoft APIs → Dynamics CRM** → select the **`mcp.tools`**
   permission → **Add permissions** (grant admin consent if your tenant requires it).
3. This client uses the interactive **PKCE loopback** flow, so also configure **Authentication → Add a
   platform → Mobile and desktop applications** with redirect URI `http://localhost/callback`, and set
   **Allow public client flows = Yes**.
4. **Allow the client in your environment:** [Power Platform admin center](https://admin.powerplatform.microsoft.com/)
   → **Manage → Environments →** *(your environment)* **→ Settings → Product → Features → Dataverse Model
   Context Protocol → Advanced Settings**. Add a client entry with your app's **Application (client) ID**
   and set **Is Enabled = Yes**. (Without this step the app can't connect to the endpoint.)

Use the **Application (client) ID** in [step 3](#3-configure-the-connection).

### 3. Configure the connection

Connection settings are **not** committed to source. Each host app reads them from environment variables
or a local `appsettings.json` (env vars win). Copy the template and fill it in:

```bash
# from the repo root, for whichever host you want to run:
cp src/DataverseFileManager.Explorer/appsettings.example.json src/DataverseFileManager.Explorer/appsettings.json
cp samples/DataverseFileManager.ConsoleDemo/appsettings.example.json samples/DataverseFileManager.ConsoleDemo/appsettings.json
```

```jsonc
// appsettings.json (git-ignored)
{
  "Dataverse": {
    "OrgUrl": "https://YOUR-ORG.crm.dynamics.com",
    "ClientId": "YOUR-ENTRA-APP-CLIENT-ID",
    "RedirectUri": "http://localhost:1179/callback",
    "TableName": "cr19f_fileitem"
  }
}
```

Or via environment variables (useful for CI / containers — these override the file):

```bash
export DATAVERSE_ORG_URL="https://YOUR-ORG.crm.dynamics.com"
export DATAVERSE_CLIENT_ID="YOUR-ENTRA-APP-CLIENT-ID"
# optional:
export DATAVERSE_REDIRECT_URI="http://localhost:1179/callback"
export DATAVERSE_TABLE_NAME="cr19f_fileitem"
```

`OrgUrl` and `ClientId` are required; the rest have defaults.

## Build

```bash
dotnet build src/DataverseFileManager/DataverseFileManager.csproj
dotnet build samples/DataverseFileManager.ConsoleDemo/DataverseFileManager.ConsoleDemo.csproj
# The Explorer host is x64/ARM64 only (compile check — the run flow below publishes via the setup script):
dotnet build src/DataverseFileManager.Explorer/DataverseFileManager.Explorer.csproj -p:Platform=x64
```

## Run the console demo

A self-cleaning end-to-end smoke test against your live org (the **first** connect opens a browser for
interactive sign-in):

```bash
dotnet run --project samples/DataverseFileManager.ConsoleDemo
# or inspect the tree:
dotnet run --project samples/DataverseFileManager.ConsoleDemo -- tree /
```

## Run the Explorer integration (Windows)

The Explorer host needs **package identity** (cfapi and sync-root registration are blocked for
unpackaged processes). A sparse package supplies that. The setup script does everything in one shot —
it runs `dotnet publish` (to `bin\publish`), then packs, signs, and registers the sparse package against
that published exe — so there's **no separate `dotnet publish` step**. From an **elevated** PowerShell
(first run only — it trusts a self-signed dev cert):

```powershell
# build, sign, and register the sparse package + the host
src\DataverseFileManager.Explorer\Package\setup-sparse-package.ps1

# tear down:        ...\setup-sparse-package.ps1 -Unregister
# full reset:       ...\setup-sparse-package.ps1 -Full
```

Then run the host:

```powershell
src\DataverseFileManager.Explorer\bin\publish\DataverseFileManager.Explorer.exe run
```

A **"Dataverse"** node appears in Explorer's navigation pane. Browsing a folder populates it on demand;
opening a file hydrates it; creating / editing / renaming / deleting locally is written back to Dataverse.
Diagnostics are written to `%LOCALAPPDATA%\DataverseFileManager\explorer.log`. Press Enter in the host
window to disconnect.

## Configuration reference

| Setting | `appsettings.json` (`Dataverse` section) | Environment variable | Default |
|---------|------------------------------------------|----------------------|---------|
| Org URL **(required)** | `OrgUrl` | `DATAVERSE_ORG_URL` | — |
| Client ID **(required)** | `ClientId` | `DATAVERSE_CLIENT_ID` | — |
| Redirect URI | `RedirectUri` | `DATAVERSE_REDIRECT_URI` | `http://localhost:1179/callback` |
| Table name | `TableName` | `DATAVERSE_TABLE_NAME` | `cr19f_fileitem` |

## Known limitations

- **Interactive auth only** — tokens live in-memory, so a process restart re-prompts. Persistent
  (unattended) auth is planned; see `phase4b-auth-plan.md`.
- **No cloud→local push** — edits made *directly* in Dataverse don't yet refresh in Explorer until the
  local placeholder is cleared (a `modifiedon` poller is the planned fix).
- **Atomic-save editors** (those that write a temp file then rename over the original) round-trip as a
  delete + create, so the file gets a fresh record id.
- **Column names are compile-time constants** (publisher prefix `cr19f`) — see Setup step 1.
- The signing cert produced by the setup script is **self-signed (dev only)**.
- The bundled icons (`dvicon.*`, `Assets/*Logo.png`) are **neutral placeholders** — swap in your own
  branding. (The Microsoft Dataverse logo is intentionally not redistributed here.)

## License

[MIT](LICENSE) © 2026 Andreas Adner
