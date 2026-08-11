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

Every client instance in the config is served under `/<type>/<name>`. In Radarr, point the download
client at this proxy's host/port and set the client's **URL Base** to that path:

```text
Radarr ──▶ http://proxy:8484/qbittorrent/radarr/api/v2/torrents/info
                             └────┬────┘└──┬──┘└──────────┬─────────┘
                             client type  name         forwarded to
                                               http://qbit-host:8080/api/v2/torrents/info
```

Each client type has an *adapter* that declares an explicit allow-list of endpoints
(`src/Proxyarr/Clients/*/`). Anything not declared returns 404 (and logs a warning naming
the request), unknown SABnzbd `mode`s return 400, and destructive/administrative endpoints
(`app/setPreferences`, `mode=shutdown`, ...) are never forwarded. Requests, headers, cookies
(qBittorrent's `SID` session), bodies (including `.torrent`/`.nzb` uploads), and responses stream
through via [YARP](https://microsoft.github.io/reverse-proxy/)'s forwarder. Responses are unchanged
unless deduplication or an upstream `path_mappings` entry explicitly transforms them.

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
  overrides: {} # per-category levels, e.g. "Microsoft.AspNetCore: information"

clients:
  qbittorrent:
    upstreams:
      - name: main
        url: http://localhost:8080
        path_mappings: # optional: real client paths -> synthetic reported paths
          - from: /downloads
            to: /proxyarr/qbit-main
    instances:
      - name: radarr # served at /qbittorrent/radarr
        upstream: main

  sabnzbd:
    upstreams:
      - name: main
        url: http://localhost:8085/sabnzbd # include SABnzbd's URL base
    instances:
      - name: radarr # served at /sabnzbd/radarr
        upstream: main
```

Each client type has three named sections:

- `upstreams` defines reusable connections to real download clients.
- `groups` defines dedupe boundaries and the real upstream category for each boundary.
- `instances` defines the URL Base names and references an upstream plus, optionally, a group.

Referencing a group enables dedupe; omitting `group` makes an instance a pass-through. Names are
unique within their section and client type, so `radarr` can be used under both `qbittorrent` and
`sabnzbd`. Unknown keys, invalid references, duplicate names, and malformed URLs fail at startup.

### Reported path mappings

Radarr and Sonarr select remote path mappings using only the download client's **Host**. They do
not include its port, URL Base, type, or Proxyarr instance name. Consequently, two real clients
behind one Proxyarr host are ambiguous when both report `/downloads/Movie` but the files live at
different locations.

Configure `path_mappings` on each named upstream to give its returned paths a unique synthetic
namespace:

```yaml
clients:
  qbittorrent:
    upstreams:
      - name: seedbox-a
        url: http://qbit-a:8080
        path_mappings:
          - from: /downloads
            to: /proxyarr/qbit-a
    instances:
      - name: radarr-a
        upstream: seedbox-a

  sabnzbd:
    upstreams:
      - name: seedbox-b
        url: http://sab-b:8080
        path_mappings:
          - from: /downloads
            to: /proxyarr/sab-b
    instances:
      - name: radarr-b
        upstream: seedbox-b
```

Then add non-conflicting remote path mappings in each *arr:

| Host | Remote Path | Local Path |
| --- | --- | --- |
| `proxy` | `/proxyarr/qbit-a` | `/mnt/seedbox-a/qbit` |
| `proxy` | `/proxyarr/sab-b` | `/mnt/seedbox-b/sab` |

`from` and `to` must be absolute paths. Rewrites match complete path segments, preserve the suffix,
and use the longest matching `from` prefix, so a specific mapping may override a broader one.
Windows drive/UNC paths and Unix paths are supported, including separator conversion when `from`
and `to` use different styles. Every Proxyarr instance referencing the upstream inherits its
mappings, and rewriting works with or without deduplication.

Proxyarr rewrites the qBittorrent paths used by *arr in preferences, torrent info/properties, and
categories. For SABnzbd it rewrites the complete directory from config/full status, absolute
category directories, and queue/history `storage` paths. This only changes API responses; it does
not mount or copy files, so each *arr must still be able to access every configured local path.

## Cross-instance deduplication

Several Radarr/Sonarr instances often share one qBittorrent and one SABnzbd. When two of them
grab the same release, qBittorrent's one-category-per-torrent model means whichever adds first
"owns" the torrent and the other's download vanishes, and SABnzbd downloads the same NZB twice.

Assign instances to a named group and proxyarr makes those instances **share one download**.
Deduplication is opt-in: an instance without `group` is a byte-identical pass-through unless its
upstream explicitly configures `path_mappings`.

```yaml
database: /config/proxyarr.db # optional; defaults to proxyarr.db next to the config

clients:
  qbittorrent:
    upstreams:
      - name: main
        url: http://qbit:8080
    groups:
      - name: radarr
        category: radarr
      - name: sonarr
        category: sonarr
    instances:
      - name: radarr
        upstream: main
        group: radarr
      - name: radarr4k
        upstream: main
        group: radarr
      - name: sonarr
        upstream: main
        group: sonarr
      - name: sonarr4k
        upstream: main
        group: sonarr

  sabnzbd:
    upstreams:
      - name: main
        url: http://sab:8080
    groups:
      - name: radarr
        category: radarr
        announce_categories: [movies, movies-4k]
    instances:
      - name: radarr
        upstream: main
        group: radarr
      - name: radarr4k
        upstream: main
        group: radarr
```

**Grouping.** Instances that reference the same named group share downloads. Different named
groups are separate dedupe boundaries, even when they reference the same upstream. Ownership of a
shared download is tracked by the **proxyarr instance name** (`radarr`, `radarr4k`, ...). The
group's `category` is the real category assigned upstream; omit it to add without a category.

**qBittorrent — categories become tags.** A torrent can hold many tags but only one category, so
each instance's ownership is a tag named after it. The real category assigned upstream is the
group's `category` (or none if unset); the category each *arr sends is echoed back to it but never
forwarded. Every `torrents/info` listing is filtered by the instance's tag, even when the *arr does
not configure a category; when it does send `category=X`, Proxyarr also reports `X` back. `add`
tags the torrent instead of fighting over its category; `delete` removes only that instance's tag.
qBittorrent itself is the state store — no database is used for the qBittorrent side.

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
> the group's `announce_categories` so proxyarr reports them as existing; categories are otherwise
> learned from the claims that already exist.

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
ts=2026-07-09T06:03:48.824Z level=info msg="Request proxied" logger=Proxyarr.Forwarding.UpstreamForwarder instance=radarr method=GET path=/qbittorrent/radarr/api/v2/torrents/info query="" status_code=200 elapsed_ms=3.1
```

Every proxied request logs its outcome and duration at `information`; upstream failures log at
`error` with the exception; rejected and unmatched requests log at `warning` (useful for spotting
when Radarr starts calling an endpoint the adapter doesn't declare yet). Every log emitted while a
request is active inherits its instance, method, path, and query fields. Query strings are redacted
before entering that scope (`apikey=REDACTED`, ...), and YARP's own logging — which would print
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
