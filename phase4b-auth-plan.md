# Plan — Milestone 4B: Unattended Authentication (MSAL)

> Companion to [`spec.md`](spec.md) §6 (Milestone 4B) and §5 (Authentication), and
> [`explorer-integration-research.md`](explorer-integration-research.md) §5. This is the
> actionable design for replacing the interactive PKCE in the `Auth/` layer with a silent,
> refresh-token-backed MSAL credential — so cfapi hydration callbacks (which fire on background
> threads and cannot open a browser) obtain bearer tokens with no prompt.
>
> **Decision (fixed):** per-user **delegated** identity (keeps `user_impersonation`) via
> **MSAL.NET + persistent token cache** — one interactive sign-in at setup, silent refresh after.
> Service-principal/app-only was rejected.

---

## 0. The hard question, resolved (MCP SDK API findings)

Investigated `ModelContextProtocol` 1.4.0 (`ModelContextProtocol.Authentication` + `.Client`):

- `HttpClientTransport` has a **public constructor that accepts a caller-supplied `HttpClient`**
  (already used: `new HttpClientTransport(options, _httpClient, _loggerFactory)`). No custom
  `HttpMessageHandler` option — but a pre-built `HttpClient` over a `DelegatingHandler` is enough.
- `HttpClientTransportOptions.OAuth` is **optional**; when `null`, the transport runs no interactive
  flow and sends whatever headers the underlying `HttpClient`/handler stamps. **This is the clean bypass.**
- `ClientOAuthOptions` exposes **no** custom-token-provider / credential-delegate / handler hook
  (members: `RedirectUri`, `ClientId`, `ClientSecret`, `ClientMetadataDocumentUri`, `Scopes`,
  `AuthorizationRedirectDelegate`, `AuthServerSelector`, `DynamicClientRegistration`,
  `AdditionalAuthorizationParameters`, `TokenCache`). `TokenCache` only persists tokens the SDK's
  *own* provider acquired — it can't inject an externally-acquired MSAL token cleanly.
- `HttpClientTransportOptions.AdditionalHeaders` is sent on every request but fixed at construction —
  can't refresh an expiring token without rebuilding the transport.

**Conclusion (~0.85 confidence):** bypass `ClientOAuthOptions` (leave `OAuth = null`) and feed the
transport a **custom `HttpClient` whose `DelegatingHandler` calls MSAL `AcquireTokenSilent` and stamps
`Authorization: Bearer …` per request.** MSAL transparently refreshes from the cached refresh token, so
this is robust to expiry. Residual uncertainty is **not** the bridge but whether the MCP endpoint
accepts a token whose `aud` is the org resource via plain MSAL scopes (see Risk #1).

| Approach | Mechanism | Refresh | Coupling |
|---|---|---|---|
| **Primary** | Custom `HttpClient` + MSAL `DelegatingHandler`; `OAuth = null` | per-request `AcquireTokenSilent` (auto-refresh) | lowest |
| Fallback A | `AdditionalHeaders["Authorization"]` set to a fresh token | rebuild transport on expiry | low/brittle |
| Fallback B | Seed `ClientOAuthOptions.TokenCache` from MSAL | SDK provider still runs discovery/interactive on miss | highest |

---

## 1. Token bridge

`MsalAuthenticationHandler : DelegatingHandler` in `Auth/`:
- Holds an `IPublicClientApplication` + target scopes.
- `SendAsync`: resolve the cached account → `AcquireTokenSilent(scopes, account)` →
  `request.Headers.Authorization = new("Bearer", result.AccessToken)` → forward.
- On `MsalUiRequiredException` (unattended): do **not** prompt — let the request go out tokenless
  (server 401 → MCP error), and signal "re-auth required" (log/event). Interactive recovery only via
  the explicit `login` path (§4).
- MSAL is thread-safe and serves from its in-memory cache (network only near expiry), so per-request
  calls from concurrent cfapi callback threads are cheap.

Wire-up in `DataverseMcpConnection.ConnectAsync` (MSAL mode): build
`_httpClient = new HttpClient(new MsalAuthenticationHandler(pca, scopes){ InnerHandler = new HttpClientHandler() })`,
construct the transport with `OAuth = null`, pass this `HttpClient` via the existing 3-arg ctor.
Nothing below `Mcp/` changes (`DataverseMcpClient` only calls `McpClient.CallToolAsync`).

## 2. NuGet packages (add to the **library** csproj)

- **`Microsoft.Identity.Client`** (MSAL.NET, 4.66+) — `PublicClientApplicationBuilder`,
  `AcquireTokenInteractive`/`AcquireTokenSilent`, account/cache management. Same public-client `ClientId`.
- **`Microsoft.Identity.Client.Extensions.Msal`** (matching 4.x) — `MsalCacheHelper` for the
  cross-session persistent cache with OS-native protection (DPAPI on Windows).

Both pure-managed, `net10.0`-compatible; flow transitively into the Explorer host.

## 3. Persistent cache

- `StorageCreationPropertiesBuilder(file, dir)` → `MsalCacheHelper.CreateAsync(...)` →
  `helper.RegisterCache(pca.UserTokenCache)`.
- **Location:** fixed `%LOCALAPPDATA%\DataverseFileManager\msal.cache` (explicit; don't rely on
  packaged-vs-unpackaged heuristics). Per-user.
- **Protection:** DPAPI (per-user) by default — **no plaintext fallback**. Refresh token never cleartext.
- **Sparse-identity interplay:** sparse packages add identity but do **not** redirect `%LOCALAPPDATA%`,
  so a fixed path is stable across the unpackaged console demo and the packaged Explorer host — one
  sign-in is shared by both. System-browser interactive (not WAM broker) → no `ms-appx-web://` redirect needed.

## 4. One-time sign-in + silent refresh + re-consent

- **Setup (interactive, once):** new **`login` verb** → `AcquireTokenInteractive(scopes)`
  `.WithUseEmbeddedWebView(false)` (system browser + loopback, matches `http://localhost/callback`).
  Persists access + refresh tokens to the DPAPI cache.
- **Thereafter (silent):** the handler's `AcquireTokenSilent` returns cached or silently-refreshed
  tokens — no browser. Exactly what background cfapi callbacks need.
- **Refresh expired/revoked / CAE / MFA:** `AcquireTokenSilent` throws `MsalUiRequiredException`;
  callbacks can't prompt → fail that op gracefully (cfapi returns an error status; Explorer shows the
  file temporarily unavailable) + signal "sign-in required" (log now; tray/toast is a 4D item).
  Recovery = user re-runs `Explorer.exe login`. Frequency depends on Entra refresh-token lifetime / CA.

## 5. `Auth/` refactor (public API unchanged)

Keep `IDataverseFileSystem`, `DataverseFileSystem.CreateAsync(...)`, `FromConnectedClient` unchanged.
All change inside `Auth/` + additive options:
1. Seam: `internal interface IMcpAccessTokenProvider { Task<string> GetTokenAsync(CancellationToken ct); }`
   with `MsalAccessTokenProvider` (silent + gated interactive).
2. `MsalAuthenticationHandler` (§1) consumes it.
3. `DataverseMcpConnection` branches on `options.AuthMode == MsalSilent` (MSAL handler + `OAuth = null`),
   else today's `ClientOAuthOptions` path. Existing disposal already disposes `_httpClient`.
4. Additive `DataverseFileManagerOptions` (init-only; `CreateAsync` signature untouched):
   `AuthMode` (`InteractiveSdkOAuth` default | `MsalSilent`), `AllowInteractiveSignIn` (default false),
   `TokenCachePath` (optional). Default behavior identical to today.

**Host selection:** console demo unchanged (`InteractiveSdkOAuth`). Explorer host: `AuthMode = MsalSilent`;
`run` uses `AllowInteractiveSignIn = false` and detects "needs login" in the warm-up; new `login` verb
uses `AllowInteractiveSignIn = true` and calls `AcquireTokenInteractive` once.

## 6. Scopes / permissions / MFA / CAE

- Delegated, public client, same `ClientId` (from config); scope `…/.default` (or explicit
  `…/user_impersonation`). App reg unchanged (Mobile & desktop, `http://localhost/callback`, public
  client flows = Yes, delegated `Dynamics CRM → user_impersonation` consented).
- MFA satisfied at the one-time interactive sign-in. **CAE:** server may 401 with a claims challenge
  mid-session → handler treats like `MsalUiRequired` (fail op + signal re-auth); main reason silent
  can intermittently require `login`. WAM/broker deliberately deferred to 4D.

## 7. Risks

1. **Audience/resource acceptance (top risk).** Confirm the MCP endpoint accepts an MSAL token via
   `.default`/`user_impersonation`. Decode a working SDK-flow token's `aud` and compare; if the server
   enforces RFC 8707 resource indicators, may need explicit `user_impersonation`/resource tweak.
   Validate **before** removing the SDK-OAuth path; keep both behind `AuthMode`.
2. **DelegatingHandler vs. transport session/SSE** (`mcp-session-id`, reconnection) — should coexist
   (handler only adds `Authorization`); Fallback A if not.
3. **Cache path under sparse identity** — pin the absolute path; verify same file packaged/unpackaged.
4. **Concurrency** — many parallel `AcquireTokenSilent`; MSAL thread-safe; ensure interactive never
   fires from a callback thread (`AllowInteractiveSignIn = false`).
5. **First-run ordering** — `login`/warm-up must populate the cache **before** `CfConnectSyncRoot`.

## Task breakdown (suggested order)

1. **Spike — validate the token assumption.** MSAL-acquire a token with the existing `ClientId` + `.default`,
   hand-set `Authorization: Bearer` on one MCP `read_query`. Confirm 200. *(De-risks #1 first.)*
2. Add the two MSAL packages to the library csproj.
3. Build the cache helper (DPAPI, fixed `%LOCALAPPDATA%` path) + `MsalAccessTokenProvider`.
4. Implement `MsalAuthenticationHandler` + `MsalUiRequired`/CAE handling.
5. Add additive options; branch `DataverseMcpConnection.ConnectAsync` (leave SDK-OAuth path intact).
6. Add `login` verb; `run` uses `MsalSilent` + `AllowInteractiveSignIn = false`; warm-up "needs login".
7. Dry-run: `login` once → close → `run` → `ListFolderAsync("/")` + a hydration succeed with **no browser**.
8. Negative tests: delete/corrupt cache → graceful failure + guidance; simulate expiry → silent refresh.
9. Live cfapi run under the installed sparse package against the org.

## Exit criterion

After a single `login`, launching `run` and browsing/opening files in Explorer triggers enumeration +
on-demand hydration where callbacks obtain bearer tokens **silently — no browser at any point in normal
operation** — and token expiry is recovered by MSAL's silent refresh without user interaction.

## Critical files
- `src/DataverseFileManager/Auth/DataverseMcpConnection.cs` — auth seam (MSAL handler + `OAuth = null` branch).
- `src/DataverseFileManager/Model/DataverseFileManagerOptions.cs` — additive `AuthMode` / `AllowInteractiveSignIn` / `TokenCachePath`.
- `src/DataverseFileManager/DataverseFileSystem.cs` — pass-through wiring (signature unchanged).
- `src/DataverseFileManager/DataverseFileManager.csproj` — add MSAL packages.
- `src/DataverseFileManager.Explorer/Program.cs` — `login` verb; `run` silent mode; warm-up detection.

## Sources
- [ClientOAuthOptions — MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Authentication.ClientOAuthOptions.html)
- [HttpClientTransportOptions — MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Client.HttpClientTransportOptions.html)
- [HttpClientTransport ctors — MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Client.HttpClientTransport.html)
- [OAuth in the MCP C# SDK — Den Delimarsky](https://den.dev/blog/mcp-csharp-sdk-authorization/)
