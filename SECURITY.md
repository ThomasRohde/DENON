# Security policy

See also: [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/plugin-development.md](docs/plugin-development.md).

## Supported versions

Only the latest release gets security fixes:

- the latest app release (tag `vX.Y.Z`), and
- the latest release of each external plugin (tags `appletv-vX.Y.Z`, `homepod-vX.Y.Z`, `chromecast-vX.Y.Z`).

A fix comes in a new release. Update the app with the in-app update. Download a plugin release from
[Releases](https://github.com/ThomasRohde/DENON/releases).

## Report a vulnerability

Report a vulnerability privately. Do not open a public issue for it.

1. Go to [github.com/ThomasRohde/DENON](https://github.com/ThomasRohde/DENON).
2. Open the **Security** tab.
3. Select **Report a vulnerability**.
4. Tell what you found, the app or plugin version, and the steps to see the problem.

## Network surface

The app opens these servers on your PC.

| Server | Address and port | Who can connect | Access check |
|---|---|---|---|
| MCP server (`DenonMcpServer`, Kestrel) | `/mcp` and `/health`, TCP 18926 by default | Only this PC (127.0.0.1). All network interfaces when **Allow LAN connections** is on (off by default) | Bearer token (on by default), Host and Origin checks, permission tiers |
| Audio relay and Phone Remote (`LocalAudioRelayServer`) | TCP 18925 by default (Settings → Relay Port) | All network interfaces, so that the receiver can read the streams | Random token in each stream address; pairing tokens for the Phone Remote |
| Insights page and automation editor (`InsightsServer`, `HttpListener`) | 127.0.0.1, a free port at each start | Only this PC | A new key at each start, kept in a cookie |

### MCP server

- It listens on 127.0.0.1. With **Allow LAN connections** on, it listens on all interfaces.
- Each request must name this PC in its `Host` header: `localhost`, a loopback address, or (with LAN on) a LAN
  address or the PC's name. Other names get 403. This blocks DNS rebinding.
- A request to `/mcp` with an `Origin` header must come from a page on this PC. Other origins get 403.
- A request to `/mcp` needs `Authorization: Bearer <token>` when **Require Authorization: Bearer token on all /mcp
  requests** is on (the default). The token is random (`ddn_` and 32 hex characters). The server compares it in
  fixed time.
- `/health` needs no token.
- Permission tiers limit the tools: `Observer` (read only), `SafeControl` and `FullOrchestration`. Receiver power
  off and plugin actions marked destructive need `FullOrchestration`.
- Each MCP volume change stays at or below the safe ceiling (65 on the receiver's 0–98 scale by default).

### Audio relay and Phone Remote

- The relay tries the set port first, then 18925–18940, then a port that Windows picks.
- It serves YouTube, PC Audio Cast and jukebox streams to the receiver. Each stream address has a random token.
- It serves the Phone Remote page (`/`) and its API (`/api/...`). The page and `/health` need no token. The API
  needs a pairing token when pairing is on.
- The Phone Remote is on by default and needs pairing by default. A phone pairs with the owner token or the guest
  token from the QR code. The token goes in the `X-Denon-Token` header, an `Authorization: Bearer` header, or the
  `token` or `pair` query value. The app compares tokens in fixed time.
- A guest can only search and queue YouTube tracks. All other controls need the owner token.
- Automation webhooks (`POST /api/hooks/{name}`) need the owner token.
- A `POST` from another site's page gets 403.
- **Revoke & Rotate** on the Cast & Remote page makes new owner and guest tokens.
- At most 64 connections are open at one time.

### Insights page and automation editor

- `HttpListener` listens on `http://127.0.0.1:<port>/` only. It answers only loopback clients that use the host
  `127.0.0.1`.
- Each app start makes a new random key. The app opens the page with `?key=...`. The page keeps the key in an
  `HttpOnly`, `SameSite=Strict` cookie.
- Only the automation editor's API accepts `PUT`, `POST` and `DELETE`. All other paths are read only.
- Responses have a strict `Content-Security-Policy`, `X-Content-Type-Options: nosniff` and no caching.

### Outgoing connections

The app connects to devices on the LAN: the receiver (Telnet TCP 23, HEOS TCP 1255), SSDP (UDP 1900) and mDNS
(UDP 5353) discovery, and the devices of the plugins. The ports table on the app's Help page lists all ports.

Plugins get an `HttpClient` from the host. It accepts a self-signed certificate only from a LAN address or a
`.local` name when the plugin asks for it.

### Secrets in settings

The MCP token and the Phone Remote tokens are in `%LocalAppData%\DenonDesktop\settings.json`. A settings export
replaces them with a placeholder.

## Update verification

The in-app update (`UpdateService`) does these checks:

1. It reads releases from the GitHub API of `ThomasRohde/DENON`. It takes only `vX.Y.Z` tags, and no drafts or
   pre-releases.
2. It accepts only `https://github.com/ThomasRohde/DENON/...` addresses for the installer and the checksum file.
   It ignores other addresses.
3. It downloads `SHA256SUMS.txt` and reads the line for `DenonDesktop-Setup-win-x64.exe`.
4. It downloads the installer to a new folder and computes its SHA-256. When the hash does not match, it deletes
   the file and stops.
5. Before it starts the installer, it computes the hash again and keeps the file open. When the file changed, it
   deletes the file and stops.

The release workflows make `SHA256SUMS.txt` for each release. The installer and the exe are not code-signed.
For a plugin update, the app opens the release page. It does not install plugins itself.
