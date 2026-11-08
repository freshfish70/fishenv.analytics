# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Privacy-friendly web analytics backend in C# / .NET 10 with PostgreSQL. No cookies, no stored IPs, no raw user agents. See README.md for the public API, tracker usage and the full privacy model.

## Commands

```bash
dotnet build Analytics.sln
dotnet run --project tests/Analytics.Tests          # all tests; exit code 0 = pass

docker compose up -d postgres                       # local DB (runs db/setup.sql on first init)
dotnet run --project src/Analytics.Api              # 127.0.0.1:8081, applies migrations on start
dotnet run --project src/Analytics.Ingest           # 0.0.0.0:8080
dotnet run --project src/Analytics.Api -- create-key "name" admin   # prints a new API key once

docker build --target ingest .                     # or --target api; CI (.github/workflows/docker.yml) pushes both to ghcr.io
dotnet tool install -g dotnet-ef                                     # once
dotnet ef migrations add <Name> --project src/Analytics.Postgres    # after changing AnalyticsDbContext
```

There is no xUnit/NUnit and no linter. `tests/Analytics.Tests/Program.cs` is a self-written runner: each `await Section("…", …)` block is a group of `t.Eq`/`t.Check` assertions. To run a single section, comment out the others temporarily; there is no filter flag. Tests cover only `Analytics.Core` (no database needed). Nothing tests the Postgres or HTTP layers.

Shared build settings (net10.0, nullable, implicit usings, `InvariantGlobalization`) live in `Directory.Build.props`.

## Architecture

There are four projects. Each service has its own database role:

- **Analytics.Core** has no database dependency. It holds the domain logic and the storage interfaces in `Abstractions.cs` (`IEventStore`, `ISiteStore`/`ISiteLoader`, `ISaltStore`, `IApiKeyStore`, `IStatsQuery`). It references the `Microsoft.AspNetCore.App` framework (not NuGet packages) for logging, HTTP client and query helpers.
- **Analytics.Postgres** contains the EF Core implementations (`AnalyticsDbContext`, Npgsql provider), registered through `AddPostgresStorage(connectionString)`. The stores are singletons and take an `IDbContextFactory<AnalyticsDbContext>` (pooled), creating a short-lived context per operation. The entity classes stay in this project; the rest of the code base sees only the Core records.
- **Analytics.Ingest** is the public service: `POST /api/event`, `GET /script.js` (the embedded `tracker.js`) and `/healthz`. It connects as `analytics_ingest`, which may only INSERT events, SELECT sites, and read, insert and delete `daily_salts` (granted by the `IngestRoleGrants` migration; `db/grants.sql` is the manual equivalent). Ingest code must not need any other DB access. If you add a query to the ingest path, add a migration with the new GRANT and update `db/grants.sql` too.
- **Analytics.Api** is the private query/admin service under `/api/v1` (Bearer API key, `read`/`admin` scopes in `ApiKeyAuth.cs`). It connects as `analytics_admin`, owns the schema and runs migrations at startup (`Database:AutoMigrate`). JSON uses snake_case. `OrphanEventCleanup` deletes, daily, events whose site was deleted.

### Ingest pipeline

`EventEndpoint.HandleAsync` runs these steps in order:
1. `EventValidator` sanitizes the input.
2. `SiteDirectory` looks up the site in an in-memory cache, refreshed every `SiteRefreshSeconds`.
3. The Origin header is checked.
4. `UserAgentParser` reduces the user agent to browser, OS and device class. Bots get `202` and are not recorded.
5. `VisitorHasher` builds the visitor id: an HMAC over a daily salt stored in `daily_salts`; earlier days' salts are pruned with the first event of a new UTC day. Exception: on sites with `identify_users` an event carrying a user id gets `UserIdHasher`'s stable `u_` + HMAC(`Ingest:IdentitySecret`, site | user id) instead. The `identified` dimension relies on that `u_` prefix (daily hashes are lowercase hex).
6. `GeoIpUpdater.Lookup` finds the country from RIR delegation files that `GeoIpHostedService` downloads into `data/geo/` (each registry about once per `GeoRefreshHours`).
7. `EventQueue` buffers the event in a bounded in-memory queue. A full queue returns `503`. A hosted service writes batches (retrying with backoff until the database accepts them) through `IEventStore.WriteBatchAsync`, which uses Npgsql binary COPY on the shared `NpgsqlDataSource` and bypasses EF Core on purpose (speed). Do not move it to `SaveChanges`. If a batch is rejected because of a row's content (SQLSTATE class 22/23/54), the store rewrites it row by row behind savepoints and skips only the bad rows.

The IP address must never be persisted or logged.

### Stats queries

- `StatsFilterParser` and `DateRangeResolver` (Core) turn query parameters into a `StatsRequest`.
- `StatsSqlBuilder` (Core) produces parameterized SQL with `$n` placeholders.
- `PgStatsQuery` only executes that SQL, through EF Core's `Database.SqlQueryRaw<T>`. EF names the parameters itself, so it rewrites `$n` to `{n-1}`. The SQL must therefore contain no literal braces, and result columns must be aliased to match the row classes' `[Column]` names.
- Column names come from the `StatsDimensions` whitelist in `Stats.cs`. Custom properties (`prop.<key>`) are read with `props ->> $n`. Never put request values into SQL text. To add a filter or breakdown dimension, extend that whitelist (and the `events` table if needed).
- Sessions/visits are computed at query time in SQL (30-minute gap) by `SessionCtes`; there are no rollup tables. Event-level filters apply before sessionization; visit-level dimensions (`entry_page`, `exit_page`, in `StatsDimensions.SessionColumns`) filter the sessions afterwards, and event queries then read the `src` CTE (events of the matching visits).
- Filter operators (`path[contains]=…`) are parsed in `StatsFilterParser`; LIKE values go through `EscapeLike`. An `event` filter on a breakdown (not `by=event`) and on `/events` is a conversion goal: it is kept out of the base WHERE so the denominator is all visitors.
- `?compare=` is resolved by `DateRangeResolver.TryResolveComparison`; the API runs the same query a second time for the comparison range (in parallel) and aligns rows with `StatsComparison.Align`.

### Migrations

These are EF Core migrations in `src/Analytics.Postgres/Migrations/`, tracked in `__EFMigrationsHistory`. The API applies them at startup through `MigrateDatabaseAsync()` (`Database:AutoMigrate`). The ingest service never migrates. To change the schema, edit `AnalyticsDbContext` and run `dotnet ef migrations add <Name> --project src/Analytics.Postgres` (`DesignTimeDbContextFactory` builds the context; generating a migration needs no database). Do not edit applied migrations. Table and column names are snake_case: `OnModelCreating` derives column names from property names, so the raw SQL (COPY, stats) relies on them. A new table the ingest role uses needs a GRANT in a migration (see `IngestRoleGrants`) and in `db/grants.sql`. `events.site_id` has no foreign key on purpose (cheap inserts). The API deletes a site's events explicitly.

## Configuration

Settings live in `appsettings.json` per service, and any of them can be overridden by environment variables (`ConnectionStrings__Ingest`, `Ingest__TrustedProxies__0`, …). Ingest options are in `IngestOptions.cs`. Loopback proxies are always trusted; any other reverse proxy must be listed in `TrustedProxies` (IPs or CIDR networks), or every visitor gets the proxy's IP.
