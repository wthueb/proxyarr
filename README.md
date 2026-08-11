# proxyarr

An HTTP proxy that sits between Radarr/Sonarr and your download clients. It exposes only
the API endpoints the \*arr download client integrations actually use and passes them through,
untouched, to a configured qBittorrent or SABnzbd instance. Optionally, it can
[deduplicate downloads](#cross-instance-deduplication) so several \*arr instances sharing one
client download each release only once.

**Supported upstream versions**

| Client      | Version | API                        |
| ----------- | ------- | -------------------------- |
| qBittorrent | 5.2.3   | Web API v2 (2.15.x)        |
| SABnzbd     | 5.0.4   | `/api` with `mode=` params |

## How it works

Every client instance in the config is served under `/<name>`. In Radarr, point the download
client at this proxy's host/port and set the client's **URL Base** to `/<name>`:

```text
Radarr ──▶ http://proxy:8484/qbittorrent/api/v2/torrents/info
                             └────┬────┘└──────────┬─────────┘
                            instance name      forwarded to
                                          http://qbit-host:8080/api/v2/torrents/info
```

Each client type has an *adapter* that declares an explicit allow-list of endpoints
(`src/Proxyarr/Clients/*/`). Anything not declared returns 404 (and logs a warning naming
the request), unknown SABnzbd `mode`s return 400, and destructive/administrative endpoints
(`app/setPreferences`, `mode=shutdown`, ...) are never forwarded. Requests, headers, cookies
(qBittorrent's `SID` session), bodies (including `.torrent`/`.nzb` uploads), and responses stream
through unmodified via [YARP](https://microsoft.github.io/reverse-proxy/)'s forwarder.

### Proxied endpoints

qBittorrent (mirrors Radarr's `QBittorrentProxyV2`):

| Method | Endpoint                          |
| ------ | --------------------------------- |
| POST   | `/api/v2/auth/login`              |
| GET    | `/api/v2/app/webapiVersion`       |
| GET    | `/api/v2/app/version`             |
| GET    | `/api/v2/app/preferences`         |
| GET    | `/api/v2/torrents/info`           |
| GET    | `/api/v2/torrents/properties`     |
| GET    | `/api/v2/torrents/files`          |
| GET    | `/api/v2/torrents/categories`     |
| POST   | `/api/v2/torrents/add`            |
| POST   | `/api/v2/torrents/delete`         |
| POST   | `/api/v2/torrents/setCategory`    |
| POST   | `/api/v2/torrents/createCategory` |
| POST   | `/api/v2/torrents/setShareLimits` |
| POST   | `/api/v2/torrents/topPrio`        |
| POST   | `/api/v2/torrents/setForceStart`  |

SABnzbd — `GET|POST /api` with these `mode` values (mirrors Radarr's `SabnzbdProxy`):
`addfile`, `version`, `get_config`, `fullstatus`, `queue`, `history`, `retry`.

## Configuration

One YAML file, resolved from `--config <path>`, the `PROXYARR_CONFIG` environment
variable, or `./config.yml` — see [config/config.example.yml](config/config.example.yml):

```yaml
server:
  host: 0.0.0.0
  port: 8484

logging:
  level: information # trace | debug | information | warning | error | critical | none
  format: logfmt # logfmt (default) | json
  include_scopes: false
  overrides: {} # per-category levels, e.g. "Microsoft.AspNetCore: information"

clients:
  - name: qbittorrent # served at /qbittorrent, use as Radarr's URL Base
    type: qbittorrent
    upstream: http://localhost:8080

  - name: sabnzbd
    type: sabnzbd
    upstream: http://localhost:8085/sabnzbd # include SABnzbd's URL base
```

Unknown keys, bad levels, duplicate names, and malformed upstreams fail at startup with a
message naming the problem. Multiple instances of the same type are fine (`qbit-movies`,
`qbit-4k`, ...).

## Cross-instance deduplication

Several Radarr/Sonarr instances often share one qBittorrent and one SABnzbd. When two of them
grab the same release, qBittorrent's one-category-per-torrent model means whichever adds first
"owns" the torrent and the other's download vanishes, and SABnzbd downloads the same NZB twice.

Turn on `dedupe` per instance and proxyarr makes those instances **share one download**. It is
opt-in: without a `dedupe` block an instance is a byte-identical pass-through exactly as before.

```yaml
database: /config/proxyarr.db # optional; defaults to proxyarr.db next to the config

clients:
  - name: radarr-qbit # point several *arrs at the SAME qBittorrent, one prefix each
    type: qbittorrent
    upstream: http://qbit:8080
    dedupe:
      enabled: true
      category: proxyarr # real category set upstream; omit to add with no category
      # group: main       # optional override; only for one client on two hostnames
  - name: sonarr-qbit
    type: qbittorrent
    upstream: http://qbit:8080
    dedupe:
      enabled: true
      category: proxyarr # every member of a group must agree on this

  - name: radarr-sab
    type: sabnzbd
    upstream: http://sab:8080
    dedupe:
      enabled: true
      category: proxyarr
      announce_categories: [movies, tv] # sabnzbd only (see below)
```

**Grouping.** Dedupe-enabled instances of the same type that share a normalized upstream URL form
a *group*, derived automatically — nothing about the *arr instances is configured here. Ownership
of a shared download is tracked by the **proxyarr instance name** (`radarr-qbit`, `sonarr-qbit`,
...). The optional `dedupe.group` key is only needed for the exotic case of one client reachable
via two hostnames.

**qBittorrent — categories become tags.** A torrent can hold many tags but only one category, so
each instance's ownership is a tag named after it. The real category assigned upstream is
`dedupe.category` (or none if unset); the category each *arr sends is echoed back to it but never
forwarded. Requests are translated on the fly: `torrents/info?category=X` filters by the
instance's tag and reports `X` back; `add` tags the torrent instead of fighting over its category;
`delete` removes only that instance's tag. qBittorrent itself is the state store — no database is
used for the qBittorrent side.

**qBittorrent — files are never deleted early.** A shared torrent's files survive until it (1) has
passed one of its seed limits and (2) carries no instance tags. While any tag remains, its
share-limit action is pinned to *stop*. When the last tag is removed, proxyarr checks the current
ratio/seeding-time against the effective limits: already past → a real delete with files; not yet →
the torrent's per-torrent share-limit action is set to *remove torrent and its files* so
qBittorrent deletes it natively when a limit is finally hit. The request's `deleteFiles` flag is
ignored under dedupe, and torrents whose merged limits are unlimited are left seeding indefinitely.
Share limits merge across instances by the maximum (`-1` unlimited beats all; `-2` global loses to
any explicit value).

**SABnzbd — claims in SQLite.** SABnzbd has no tag concept, so ownership is tracked in a small
SQLite database (`database:`, WAL-mode, migrated on startup). The same release from different
indexers dedupes because the content key is a hash of the NZB's segment message-IDs. A duplicate
`addfile` adds a claim and returns the existing `nzo_id`; a `delete` removes that instance's claim
and only forwards the real delete (honoring `del_files`) once the **last** claim is gone.

> **First-run category check (SABnzbd).** On a fresh SABnzbd, Radarr's "does my category exist?"
> check runs before anything has been added. List the categories your *arrs use under
> `announce_categories` so proxyarr reports them as existing; categories are otherwise learned from
> the claims that already exist.

### Known edges (accepted)

- `torrents/setCategory` swaps the instance tag for a category tag; if a sibling then deletes,
  qBittorrent's remove-when-limits-met can take over the renamed instance's torrent. (Radarr's
  post-import categories are unused in this setup.)
- Radarr's `deleteFiles` flag is ignored for qBittorrent under dedupe — cleanup always waits for a
  seed limit, then removes with files. Torrents with unlimited merged limits are never auto-deleted.
- A crash between the SABnzbd upstream add and the database insert makes the next instance re-add
  once (it self-heals).
- Third-party callers parsing qBittorrent's JSON add response would see a synthetic `Ok.` on a
  duplicate add. `hashes=all` / `value=all` are forwarded unchanged with a warning (Radarr never
  sends them). Pre-existing user tags identical to an instance name would count as managed.

### Logging

Both formats emit one event per line with identical snake_case fields; `logfmt` is the default:

```text
ts=2026-07-09T06:03:48.824Z level=info msg="Proxied qbittorrent GET /qbittorrent/api/v2/torrents/info -> 200 in 3.1ms" logger=Proxyarr.Forwarding.UpstreamForwarder instance=qbittorrent method=GET path=/qbittorrent/api/v2/torrents/info query="" status_code=200 elapsed_ms=3.1
```

Every proxied request logs its outcome and duration at `information`; upstream failures log at
`error` with the exception; rejected and unmatched requests log at `warning` (useful for spotting
when Radarr starts calling an endpoint the adapter doesn't declare yet). Query strings are
redacted before logging (`apikey=REDACTED`, ...), and YARP's own logging — which would print
un-redacted upstream URLs — is disabled by default (re-enable with an override on `Yarp` if you
accept that).

## Running

```sh
dotnet run --project src/Proxyarr -- --config config/config.example.yml
```

### Docker

```sh
docker build -t proxyarr .
docker run -v ./config/config.yml:/config/config.yml:ro -p 8484:8484 proxyarr
```

`compose.yaml` starts the proxy alongside real qBittorrent 5.2.3 and SABnzbd 5.0.4 containers as
a development harness: `docker compose up --build`.

## Testing

Tests are a core component; run everything with `dotnet test`.

- **`tests/Proxyarr.Tests`** — no real services needed. Boots the proxy in-process
  (`WebApplicationFactory`) against a WireMock fake upstream and verifies pass-through behavior
  (paths, queries, cookies, multipart bodies, error propagation), the endpoint allow-lists,
  config validation, and log output/redaction.
- **`tests/Proxyarr.IntegrationTests`** — full integration tests. Testcontainers starts
  the real `linuxserver/qbittorrent:5.2.3` / `linuxserver/sabnzbd:5.0.4` images and drives every
  proxied endpoint through the proxy the way Radarr does (login, upload a torrent, add an NZB,
  query, remove, ...). Skipped automatically when Docker isn't available, or explicitly with
  `PROXYARR_SKIP_INTEGRATION=1`.

`docker build --target test .` runs the mock-based suite inside the image build.

## Extending

- **New endpoint for an existing client**: add one `ProxyRoute` line to
  `Clients/QBittorrent/QBittorrentAdapter.cs` (or a `mode` to `SabnzbdAdapter.AllowedModes`).
  The "every declared endpoint is forwarded" test picks it up automatically; unmatched-request
  warnings in the logs tell you exactly which endpoint a new Radarr version wants.
- **New download client type**: implement `IDownloadClientAdapter` (a `Type` string plus the
  route allow-list), register it in `DownloadClientEndpoints.AddDownloadClients`, and add
  pass-through tests plus a Testcontainers fixture mirroring the existing pairs.
- **API version bumps**: adapters pin to one upstream API generation (currently qBittorrent Web
  API 2.15.x / SABnzbd 5.x). When a client renames endpoints (as qBittorrent 5.0 did with
  `pause` → `stop`), update the adapter's routes and the container image tags in the integration
  fixtures together.

## Project layout

```text
src/Proxyarr/
├── Program.cs                  # config load, logging setup, endpoint mapping
├── Configuration/              # YAML config model, loader, validation
├── Clients/                    # one adapter per download client type
│   ├── QBittorrent/            # pass-through + dedup (tags, bencode/magnet hashing, share limits)
│   └── Sabnzbd/                # pass-through + dedup (claims, NZB content key)
├── Dedupe/                     # shared dedup infra: groups, keyed lock, SQLite claim store (EF Core)
├── Forwarding/                 # YARP pass-through (prefix strip, request logging)
└── Logging/                    # logfmt/JSON formatters, query redaction
tests/
├── Proxyarr.Tests/            # mock-upstream suite (WireMock, no Docker)
└── Proxyarr.IntegrationTests/ # real clients via Testcontainers
```
