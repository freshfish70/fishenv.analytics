# Analytics

A small, privacy-friendly web analytics backend in C# on .NET 10 with PostgreSQL.
No cookies, no stored IP addresses, no raw user agents. Two services share one codebase:

```
 visitors' browsers                         you / your dashboard
        │  POST /api/event                          │  GET /api/v1/...  (Bearer API key)
        ▼                                           ▼
┌──────────────────┐                      ┌──────────────────────┐
│ Analytics.Ingest │  public, tiny        │ Analytics.Api        │  private
│  validate, drop  │                      │  sites, keys, stats  │
│  bots, geo, hash │                      │  runs DB migrations  │
│  queue ─► batch  │                      └──────────┬───────────┘
└────────┬─────────┘                                 │
         │ INSERT only (role analytics_ingest)       │ full access (role analytics_admin)
         └──────────────────►  PostgreSQL  ◄─────────┘
```

| Project | Purpose |
|---|---|
| `Analytics.Core` | Shared logic with no database dependency: event validation, user-agent parsing, RIR-based IP→country lookup, anonymous visitor hashing, query building |
| `Analytics.Postgres` | EF Core (Npgsql provider) implementations of the storage interfaces, EF Core migrations |
| `Analytics.Ingest` | Public service: `POST /api/event`, `GET /script.js` |
| `Analytics.Api` | Query/admin service: sites, API keys, summary / breakdown / timeseries |
| `tests/Analytics.Tests` | Dependency-free test runner for Core (`dotnet run --project tests/Analytics.Tests`) |

## Quick start

```bash
docker compose up -d postgres                    # or use any Postgres 14+; see db/setup.sql
dotnet run --project src/Analytics.Api           # applies migrations (incl. ingest-role grants), 127.0.0.1:8081
dotnet run --project src/Analytics.Ingest        # listens on 0.0.0.0:8080
dotnet run --project src/Analytics.Api -- create-key "my dashboard" admin   # prints an API key once
```

Change the passwords in `db/setup.sql` and the connection strings in each `appsettings.json` (or use environment
variables such as `ConnectionStrings__Ingest`). The first start of the ingest service downloads the RIR files
(~25 MB) into `data/geo/` (relative to the working directory; the resolved path is logged at startup, so use an
absolute `Ingest:DataDir` under systemd). Each registry is refreshed about once a day. Until the first download
events are stored without a country. `db/grants.sql` is only needed for a custom ingest role name or on PostgreSQL 14.

### Docker images

`.github/workflows/docker.yml` publishes multi-arch (amd64/arm64) images to GitHub Container Registry:
`ghcr.io/freshfish70/fishenv.analytics-ingest` and `ghcr.io/freshfish70/fishenv.analytics-api`. Pushes to `main`
publish `:main` and `:sha-<commit>`; a tag `v1.2.3` publishes `:1.2.3`, `:1.2` and `:latest`. Build locally with
`docker build --target ingest .` or `--target api`.

```bash
docker run -d -p 127.0.0.1:8081:8081 \
  -e ConnectionStrings__Api="Host=db;Database=analytics;Username=analytics_admin;Password=..." \
  ghcr.io/freshfish70/fishenv.analytics-api:latest
docker run -d -p 8080:8080 -v analytics-geo:/app/data \
  -e ConnectionStrings__Ingest="Host=db;Database=analytics;Username=analytics_ingest;Password=..." \
  -e Ingest__TrustedProxies__0=172.18.0.0/16 \
  -e Ingest__SaltKey="..." \
  ghcr.io/freshfish70/fishenv.analytics-ingest:latest
docker run --rm -e ConnectionStrings__Api="..." ghcr.io/freshfish70/fishenv.analytics-api:latest create-key "my dashboard" admin
```

The containers run as a non-root user and listen on all interfaces (ingest 8080, API 8081); keep the API port
private. Mount `/app/data` on ingest so the GeoIP files survive restarts. Generate `Ingest:SaltKey` once with
`openssl rand -base64 48`, use the same value on every ingest instance, and keep it out of the database and its
backups (see Privacy model). Inside Docker, a reverse proxy is
not loopback, so its network must be in `Ingest:TrustedProxies` (see Operations notes).

Register a site, then add the tracker to its pages:

```bash
curl -X POST http://127.0.0.1:8081/api/v1/sites -H "Authorization: Bearer $KEY" \
     -H "Content-Type: application/json" -d '{"domain":"example.com","name":"Example"}'
```

```html
<script defer data-site="example.com" src="https://analytics.example.com/script.js"></script>
```

The ingest service picks up new sites within 30 seconds (`Ingest:SiteRefreshSeconds`).

## Tracking

- Pageviews are automatic, including single-page-app navigations (`history.pushState`).
- Custom event from code: `analytics('signup', { plan: 'pro' })`. If the page already defines a global `analytics`
  (e.g. Segment), it is left alone; pick another name with `data-global="myAnalytics"` on the script tag.
- Prerendered pages (speculation rules) are counted only when the visitor actually opens them.
- Custom event from markup (one event per click): `<button data-track="signup_click" data-track-plan="pro">`
- Event names: letters, digits, `_ - . :` and spaces, max 64 chars. Props: at most 10 flat keys with
  string / number / boolean values; anything else is dropped.
- Localhost and automated browsers are skipped; add `data-allow-local` to the script tag to test locally.
- Outbound links and file downloads (pdf, zip, csv, …) are recorded automatically as `outbound_link` /
  `file_download` with the prop `url` (host + path, no query string). Turn this off with `data-outbound="off"`.
- Name feature events `area:action` (`checkout:buy_click`, `settings:modal_open`); then `event[prefix]=checkout:`
  selects one area of the product.

### Identified users (internal sites)

For sites behind a login you can count real users instead of daily anonymous visitors:

1. Set `Ingest:IdentitySecret` (at least 32 random characters, kept out of the database and never changed).
2. Enable it per site: `PATCH /api/v1/sites/{id}` with `{"identify_users": true}` (or pass it when creating the site).
3. Pass the logged-in user's id: `<script ... data-user="42">`, or `analytics.identify('42')` after login and
   `analytics.identify(null)` after logout.

The visitor id is then `u_` + HMAC-SHA256(secret, site | user id): the same every day and on every device, so
visitor counts over several days are unique users and visits are not split at midnight. The raw user id is not
stored. Events without a user id (logged out) still get the anonymous daily id. Sites without `identify_users`
ignore user ids completely. `by=identified` / `identified=yes` separates the two kinds of visitors.

## Query API (`/api/v1`, `Authorization: Bearer <key>`)

| Endpoint | Scope | Description |
|---|---|---|
| `GET /sites` | read | List sites |
| `GET /sites/{id}` | read | One site |
| `POST /sites` `{domain, name, identify_users?}` | admin | Register a site |
| `PATCH /sites/{id}` `{name?, identify_users?}` | admin | Change a site's settings |
| `DELETE /sites/{id}` | admin | Delete a site **and all its events** |
| `GET /keys` | admin | List API keys (name, scope, created, last used) |
| `DELETE /keys/{id}` | admin | Revoke an API key |
| `GET /sites/{id}/summary` | read | visitors, pageviews, custom events, visits, bounce rate, avg visit duration |
| `GET /sites/{id}/breakdown?by=…&limit=10` | read | Top values with visitors / pageviews / events |
| `GET /sites/{id}/timeseries?interval=day` | read | Per hour / day / week / month, empty buckets zero-filled |
| `GET /sites/{id}/events?limit=100` | read | Feature usage per custom event: visitors, events, visits, conversion rate, events per visitor |
| `GET /sites/{id}/events/props?event=…` | read | The property keys an event carries |

Common parameters:

- **Range:** `period=today|7d|30d|…|month|12mo|year` (default `30d`), or `from`/`to` as `yyyy-MM-dd` (inclusive days) or
  ISO timestamps (`to` exclusive). Ranges are limited to ~3 years.
- **Time zone:** `tz=Europe/Oslo` (IANA name) for calendar boundaries and timeseries buckets. Default UTC.
- **Filters:** `path` (stored decoded, e.g. `/blåbær`), `referrer`, `country`, `browser`, `os`, `device`, `utm_source`,
  `utm_medium`, `utm_campaign`, `event`, `identified` (`yes`/`no`), `entry_page`, `exit_page`, and custom properties as
  `prop.<key>`. The value `(none)` matches events where the field is unset.
- **Operators** go in brackets after the name: `path=/a` (is), `country[not]=NO`, `path[contains]=blog`
  (case-insensitive), `path[not_contains]=admin`, `path[prefix]=/blog/`. Repeating a positive filter means "any of"
  (`country=NO&country=SE`); negative ones must all hold. Different dimensions are combined with AND.
- **Visit filters:** `entry_page` / `exit_page` select whole visits (first / last pageview), and every endpoint then
  counts the events of those visits. The other filters select events *before* visits are built, so `path=/a` with
  `by=entry_page` only sees visits through `/a` as starting there.
- **Breakdown dimension:** `by=` any filter name, or `prop.<key>` to group by a custom property. `by=entry_page` /
  `exit_page` returns visitors, visits, pageviews, bounce rate and average duration per page.
- **Conversion:** a breakdown with an `event` filter (and `by` not `event`) counts the matching events only and adds
  `conversion_rate`: the share of each row's visitors who triggered the event. `GET /events` reports conversion
  against all visitors matching the other filters; an `event` filter there only selects which events are listed.
- **Comparison:** `compare=previous_period` (the month before for `period=month`, the 7 days before for `7d`, the same
  length before a custom range), `compare=year_over_year`, or `compare=custom&compare_from=…&compare_to=…`. Summary,
  breakdown and events add `comparison.results` (breakdown/events rows in the same order as `results`, zero when the
  value did not occur) and summary also `comparison.change` (percent; bounce rate in percentage points; null when the
  previous value is 0). For a period that is still running, totals are compared up to the same point in time; the
  timeseries comparison returns the whole previous period, aligned by position.

Examples:

- Signups per plan from Norway this month: `GET /sites/1/breakdown?by=prop.plan&event=signup&country=NO&period=month`
- Which pages lead to buy clicks: `GET /sites/1/breakdown?by=path&event=checkout:buy_click`
- Adoption of all checkout features on mobile vs. last month:
  `GET /sites/1/events?event[prefix]=checkout:&device=Mobile&period=month&compare=previous_period`
- Where visits that start on the blog go: `GET /sites/1/breakdown?by=exit_page&entry_page[prefix]=/blog/`

## Privacy model

- The IP address is used in memory for the country lookup and visitor hash, then discarded. The user agent is reduced
  to browser / OS / device class. Paths are stored without query strings (except `utm_*`), referrers as host only.
- Visitor id = `HMAC-SHA256(daily salt, site | ip | user agent)`, truncated. The salt is random, changes at 00:00 UTC and
  the previous one is deleted with the first event of the new day, after which ids cannot be linked across days or
  reversed by anyone. IPv6 uses the /64 prefix.
- The salt is stored only encrypted (AES-256-GCM) with `Ingest:SaltKey`, which lives in the ingest configuration and
  never in the database. A database dump or backup alone therefore cannot be brute-forced back to visitors' IP
  addresses; someone who also has the key can, for the current day. Every ingest instance must use the same key, and
  restarts keep the day's visitor ids. Changing the key, or upgrading from a version with plaintext salts, starts a new
  salt and counts that day's earlier visitors again.
- Without `Ingest:SaltKey` the salt is kept only in the ingest process's memory: equally safe against dumps, but a
  restart starts a new salt (that day's returning visitors count twice) and several instances would each have their
  own. Fine for development and a single small instance; ingest logs a warning at startup.
- Exception: identified users on sites with `identify_users` (see above) get a stable id instead.
- Consequence: anonymous "visitors" are unique per UTC day. Over a multi-day range (and in week/month timeseries buckets) the
  number is the sum of daily unique visitors: someone who comes back on three days counts three times. With a
  non-UTC `tz`, a local day spans two salts, and a visit that crosses midnight UTC counts as two.
- Sessions ("visits") are computed at query time: events from one visitor with gaps of at most 30 minutes.

## Operations notes

- **Reverse proxy:** a proxy on the same host (loopback) is trusted automatically. For any other proxy, set
  `Ingest:TrustedProxies` to its IP(s) or network(s) in CIDR form (e.g. a Docker network `172.18.0.0/16`) so
  `X-Forwarded-For` is honored; otherwise every visitor gets the proxy's address. The service logs a warning when it
  sees `X-Forwarded-For` from an untrusted address. Never trust forwarded headers from the open internet.
  The "no stored IPs" promise also depends on the proxy: disable or anonymize its access logs.
- **Back-pressure and outages:** events are queued in memory (`QueueCapacity`, default 10 000, i.e. days of traffic
  at a few thousand events/day) and written in batches. If the database is unavailable, the current batch is retried
  with backoff (up to every 30 s) until it succeeds while new events keep queueing; only a full queue answers `503`
  with `Retry-After`. A row the database rejects (bad data) is skipped without affecting the rest of its batch.
  Stopping the process loses what is still queued if the database is down at that moment.
- **Health:** `GET /healthz` on ingest returns queue depth, last successful write, site and GeoIP range counts, and
  answers `503` while events cannot be written.
- **Rate limits:** per IP, 120/min on ingest and 300/min on the API (configurable).
- **API keys** are stored as SHA-256 hashes. `GET /api/v1/keys` lists them and `DELETE /api/v1/keys/{id}` revokes one
  immediately (a key deleted directly in the database keeps working for up to 60 seconds because of the cache).
- **Retention:** nothing is deleted automatically. Deleting a site deletes its events; events that arrive in the
  seconds before the ingest service notices the deletion are removed by a daily cleanup in the API.

## Performance (measured)

On a 2-core test VM with PostgreSQL 16 defaults and 450 000 events (about 30 days at 15 000 events/day):

| Query over the full 30 days | Time |
|---|---|
| summary (sessionization) | ~1.0 s |
| breakdown by path | ~0.5–0.7 s |
| timeseries (day or hour) | ~0.4–0.5 s |
| any of the above for 7 days | roughly a quarter of that |

That is fine for a dashboard at this scale but it is **not** instant; it grows linearly with the range. Pre-aggregated
daily rollup tables are the next step when it matters. The ingest path is not a concern at this volume.

## Not implemented yet

Realtime view, funnels, per-site API keys, automatic retention, rollup tables, table partitioning
(declare `events` partitioned by month if you plan to drop old data), `LISTEN/NOTIFY` for instant site refresh.
