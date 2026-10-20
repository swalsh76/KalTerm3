# KalTerm plan

**Status: APPROVED by the owner (2026-10-16).** Source of truth. Milestones done: M0, M2. M1 spikes done: all (S-3, S-1a, S-1b, S-1d, S-2, S-4). **M3 may start**: S-1d passed on the target server (BUILD-LOG 2.7).

## 1. Goal

A Windows-native Kalshi trading and analytics terminal: a server that ingests and stores market data
and a client that browses it and trades, directly against Kalshi. Same end goal as KalshiTerm2, on a
Windows-only stack. KalshiTerm2 (Python, Postgres) is the reference implementation.

## 2. Decisions

| # | Decision |
|---|---|
| 1 | Windows only. .NET 10 (LTS), C#, SQL Server 2025. No Docker, WSL or Linux in the product. |
| 2 | Greenfield repo. KalshiTerm2 provides specs, fixtures and test oracles, not code. |
| 3 | Manual trading first; the trading client is separate from the read client. |
| 4 | Multi-user server; per-user tokens and data. |
| 5 | Analytics start as standard screener metrics in SQL and C# (§5). |
| 6 | MIT license, public repository. |
| 7 | SQL Server 2025 **Enterprise** on the server; licensing is the owner's responsibility and is settled (2026-10-16). Spikes may run on any edition, but a result that depends on an Enterprise-only feature must say so. |
| 8 | The server host runs **Windows Server 2025** (topology B and the S-1d target). The workstation runs Windows 11 Pro. |
| 9 | Coverage gate (CI, line coverage per production assembly): **90%**, and **95% for `KalTerm.Core` and `KalTerm.Data`** (owner, 2026-10-18), where a defect costs most (money and fees; durability and retry). Set in `tools/ci/check-coverage.ps1`. |

## 3. Topologies

- **A. Single box**: server and client on one Windows 11 x64 machine. API binds loopback; the
  installer creates a client profile and pins the certificate.
- **B. Remote host**: server and SQL Server on Windows Server 2025; clients elsewhere on the LAN.
  The API is the only exposed port.

One installer and one code base; a `deployment.mode` setting differs.

## 4. Stack

| Layer | Choice |
|---|---|
| Runtime | .NET 10, C#, nullable on, warnings as errors |
| Database | SQL Server 2025, named instance, shared memory only, Windows authentication |
| Data access | `Microsoft.Data.SqlClient` (async, `SqlBulkCopy`), Dapper on hot paths, DbUp or SSDT migrations |
| Server | ASP.NET Core (Kestrel, self-signed TLS, TOFU pinning); one Windows service per role: ingest, api, analyze, backup |
| Supervision | `UseWindowsService`, `sc failure` recovery, virtual service accounts, ACLs |
| Client | CLI (`System.CommandLine`, `Spectre.Console`) first; WPF UI later (decision pending: WPF vs WinUI 3) |
| Secrets | DPAPI and ACL'd files for the server; Credential Manager for the trading key |
| Install | PowerShell: `check`, `install`, `verify`, `uninstall`, `-WhatIf` |
| Tests | xUnit; LocalDB or a Developer instance; golden fixtures exported from the Python line |
| CI | GitHub Actions, `windows-latest`, pinned action versions |

## 5. Analytics

Start with standard screener and trading-station metrics: last, change, volume, open interest,
spread, VWAP, candles, simple moving averages and volatility, and per-user alerts. These are SQL
aggregates plus integer arithmetic in C#.

Extension point: an analyzer takes a window and a market universe and returns typed rows; a runner
owns scheduling, locking and change signaling; results land in result tables. Nothing about an
analyzer depends on its language or process, so heavier analytics can be added later without a
rewrite. Data stays in ordinary SQL tables and documented views, and a read-only `research` schema
and login allow external tools (for example Python notebooks) to read it. Do not build a plugin
host or embed a runtime before it is needed.

## 6. Build order

Each milestone ends with tests green and the BUILD-LOG updated.

| Milestone | Deliverable | Gate |
|---|---|---|
| **M0 Scaffold** | Solution, CI, analyzers, central package management | CI green on an empty solution |
| **M1 Spikes** (throwaway, measured) | **S-1 durability:** `diskspd` write-through benchmark on the target volume, then SQL Server commit latency, then 3 hard power cuts under load with `DBCC CHECKDB` after each. **S-2 time-series layout:** daily partitioned columnstore vs hot rowstore plus cold columnstore; insert rate, storage per million rows, query latency. **S-3 Ed25519:** signing library choice, verified against the Python signing vectors. **S-4 install:** unattended instance install on a clean VM | go/no-go; S-1 failing ends the SQL Server path. S-1 runs in stages (§6.1) |
| **M2 Core** | Fixed-point types, fees, Ed25519 signer, REST (GET-only) and WebSocket clients, rate limiter; verified against Python fixtures | parity tests green; may start once S-1a and S-1b have passed (M2 never touches SQL). **Met 2026-10-17** (BUILD-LOG §4) |
| **M3 Vertical slice** | Ingest to SQL Server, minimal API, CLI market view | demo feed ingests and the CLI reads it; **may start only after S-1d has passed on the target server** (**met 2026-10-18**, BUILD-LOG 2.7). **Done 2026-10-19** (BUILD-LOG 5.9: production feed ingesting on `kaldb`, CLI reading ticks, trades, gaps and order books) |
| **M4 Server** | Storage governor, retention, candles, status, auth, TLS and pinning, backup and restore | API contract tests green |
| **M5 Analytics** | Runner, built-in metrics, alerts, push | per-user alerts fire |
| **M6 Trading** | Risk layer, trading client, kill switch; production off by default | verified under tiny limits, with the owner's go-ahead |
| **M7 UI** | WPF terminal | decided later |
| **M8 Host layer and verification** | Installer, services, reboot and power-cut tests, 48 h soak | all pass |

### 6.1 S-1 stages

Only a real power cut tests a drive's write cache, and the development workstation (one NVMe, one
volume, the daily driver) must not be hard-cut. So S-1 is staged, safest first. Spike code lives on
`spike/s1-durability`, outside `KalTerm.sln`, and is not merged; results go in BUILD-LOG.

| Stage | Where | What | Proves |
|---|---|---|---|
| **S-1a** | workstation | `diskspd` write-through latency, then SQL Server commit latency, compared with each other and the drive's physical flush time | Early warning. Commits far faster than the drive can physically commit is the KalshiTerm2 signature: stop and discuss |
| **S-1b** | workstation | Hard-kill `sqlservr.exe` under write load, then `DBCC CHECKDB` and a row-level verify | The engine's own recovery. Does **not** prove the drive flushes |
| **S-1d** | target server (Dell OptiPlex 7060, i7-8700, 32 GB, 1 TB NVMe, hostname `kaldb`; delivered 2026-10-18) | 3 hard power cuts (cord pulled) under load; after each, `DBCC CHECKDB`, every acknowledged commit present, every row's checksum valid | The authoritative gate, on the target volume |

There is no S-1c: real cuts on the workstation are skipped in favour of the server box.

**S-1d result (2026-10-18): passed, 3 of 3 cord pulls** with the device cache on and no power-loss protection: 42.8 M acknowledged rows
verified, none missing, no bad checksums, `CHECKDB` clean each time (BUILD-LOG 2.6 and 2.7). Limits are recorded there.

Load and verify protocol: the writer inserts rows whose content is a checksum of the row id, in a mix
of single-row and batched small transactions, with `DELAYED_DURABILITY = DISABLED` and
`PAGE_VERIFY = CHECKSUM`. Each acknowledged commit id is sent to a ledger on a **different machine**
(the workstation, once the server is the database host), so a cut cannot erase the record of what was
promised. Pass: no lost acknowledged commit, `CHECKDB` clean, no zeroed or mismatched rows, and the
instance recovers without manual repair across all 3 cuts. Watch recovery with a timed script
(host up, service up, data fresh) and compare row counts with a baseline taken beforehand.

### 6.2 M2 Core: decisions and slices

Decisions (2026-10-16): two libraries, **`KalTerm.Core`** (pure logic, no I/O: fixed-point money, fees,
rate limiter, clock-skew maths) and **`KalTerm.Kalshi`** (I/O: config, signer, REST, WebSocket,
models). Money is typed: `Dollars` (10^-6) and `Contracts` (10^-2), each a `readonly record struct`
over a `long`, with no `double` or `float` anywhere in the public surface. Retry and backoff are
written by hand (a small internal policy driven by `TimeProvider`); Polly is parked (§9). Out of M2:
portfolio reads (M6), the order book builder (M3), trading (M6). A line-coverage gate for
`KalTerm.Core` is added after slice 2.3.

Parity method: golden vectors are generated by calling the Python reference as the oracle
(`tools/vectors/`), committed under `tests/<project>/Vectors/`, and read by the C# tests. CI never
runs Python. Where the port is deliberately stricter or different, the vector says so.

| Slice | Content | Status |
|---|---|---|
| 2.1 | Fixed-point (`Dollars`, `Contracts`, exact parse/refuse) | done |
| 2.2 | Fees (taker fee, round-up) | done |
| 2.3 | Token-bucket rate limiter on `TimeProvider`, then the coverage gate (90% lines, `tools/ci/check-coverage.ps1`) | done |
| 2.4 | Config and environments (demo default, URLs only here); creates `KalTerm.Kalshi` | done |
| 2.5 | Ed25519 signer (NSec) against the 21 S-3 vectors | done |
| 2.6 | Clock-skew estimator | done |
| 2.7a | REST models: exact fixed-point JSON converters, strict date-times, `Level`; pydantic-oracle vectors | done |
| 2.7b | REST client core: GET-only guard, rate limiting, signing, retry/backoff, pagination, production banner; exchange status and markets | done |
| 2.7c | Remaining read endpoints (trades, events, multivariate events, market/event/series, order book), clock-skew sampling, opt-in live smoke test (production, read-only key) | done; live run passed 2026-10-16 (the owner runs it; each run needs the owner's go-ahead) |
| 2.7d | One-off live check that the real server accepts our signature: an unsigned account read must be refused, the signed one accepted (added at the owner's request; account values are never printed) | done; passed 2026-10-16 (401 unsigned, 200 signed) |
| 2.8a | WebSocket models: envelope plus the public channels (ticker, trade, market and event lifecycle, order book snapshot and delta); pydantic-oracle vectors | done |
| 2.8b | WebSocket client core: small socket interface, scriptable in-memory fake on a fake `TimeProvider`, a few real-socket tests against in-process Kestrel; connect, sign, subscribe, command ids, control markers for sequence tracking | done |
| 2.8c | Reconnect with backoff, resubscribe, `reconnected` message (sequence-gap detection is the M3 order book builder's job) | done |
| 2.8d | Overflow handling (reconnect after the consumer drains), stats, opt-in live tests | done |

Slice 2.8 decisions (2026-10-16): split into 2.8a-d, each its own PR; models cover public channels only (private `fill`,
`user_order`, `market_position` wait for M6; the order book builder and gap recovery stay in M3); channel names pass through
unchecked, as in the reference.

### 6.3 Inputs from the spikes S-2 and S-4 (approved by the owner 2026-10-18)

Measurements and reasoning are in BUILD-LOG 2.4 and 2.5. Approved as M3 and M8 inputs on 2026-10-18; see section 6.4.

**M3 ticker storage (S-2).** Layout: one table, daily-partitioned, clustered columnstore ordered by `(market_id, ts)`. Rules that
apply to any layout: partition by day and drop a day with `TRUNCATE ... WITH (PARTITIONS (n))`, never `DELETE`; `LOCK_ESCALATION = AUTO`
on partitioned tables; the ingest writer retries on SQL errors 1205 and 4891; count rows with `COUNT_BIG(*)`, not `dm_db_partition_stats`;
size the ingest buffer for a 30 s stall until the sealing method is measured. The hot-rowstore-plus-cold-columnstore layout (B) was
measured and is not used: 11 s full scans while a day is hot, 6x the bytes per row, 10x the log, and a rollover job.

**M8 installer (S-4).** Install the Visual C++ 2015-2022 x64 runtime, or ship `vcruntime140.dll` and `vcruntime140_1.dll` beside `libsodium.dll`;
publish apps self-contained; preflight (read-only, exit 1 with "REFUSING") before any change; SQL Server from a generated configuration file
(no key, no password) with TCP and named pipes disabled, Windows authentication only, virtual service accounts, every wait bounded; verification
that asserts "no network exposure" (not "no TCP socket": the loopback admin connection always exists) and waits, bounded, for the
delayed-start service after a reboot; disable the telemetry service and usage metrics (to be tested).

### 6.4 M3 Vertical slice: decisions and slices

Decisions (approved by the owner 2026-10-18; D-numbers are those of the M3 proposal):

| # | Decision |
|---|---|
| D1 | Ticker storage is the S-2 layout "A": one table, daily-partitioned, clustered columnstore ordered by `(market_id, ts)`. The eight S-2 findings (section 6.3) are rules for the schema and the writers. |
| D1b | The S-4 installer requirements (section 6.3) are accepted as M8 requirements. |
| D2 | Data source: **Kalshi production with the read-only key** (public market data; GET and subscribe only). Demo stays the default in code and config; production is selected explicitly. A long production ingest soak needs the owner's fresh go-ahead before it starts. |
| D3 | Schema migrations with **DbUp** (plain SQL scripts, journal table, forward only). |
| D4 | CI database: the runner's **LocalDB (SQL Server 2025 17.0.4025.3, Express)**. The slice 3.0 probe showed it supports daily partitioning, an ordered clustered columnstore on the partition scheme, `LOCK_ESCALATION = AUTO`, `DATETRUNC`, `TRUNCATE ... WITH (PARTITIONS)` and `REORGANIZE ... COMPRESS_ALL_ROW_GROUPS`. No SQL install step in CI. Express limits (10 GB, small buffer pool) are fine for tests; scale and durability tests run on `kaldb`. |
| D5 | The API listens on **loopback only** in M3; the CLI runs on `kaldb` through remoting until M4 adds TLS and tokens. |
| D6 | An in-memory order book builder with sequence-gap detection and a `book` CLI view for a configured watch list. Deltas are not persisted. |
| D7 | M3 logs gaps (`ingest_gap`); M4 backfills trades. |
| D8 | Market scope: open, non-combo markets only. Combo tickers (prefix `KXMVE`) are skipped and counted. |
| D9 | Catalog refresh (owner, 2026-10-18): every 5 minutes on the clock, except every 30 seconds in the last 5 and first 5 minutes of each UTC hour (`RefreshSchedule`). The 5-minute ticks run a **full** pass; the other 30-second ticks run an **incremental** pass (`min_updated_ts` + `mve_filter=exclude`, 2-minute overlap), because a full pass takes about one tick. Markets that drop out of the open list are marked `unlisted` after a complete, non-empty full listing; a failed or empty listing unlists nothing; an incremental pass records true statuses of known markets and never unlists. |

Projects: `KalTerm.Data` (schema, migrations, writers, readers), `KalTerm.Ingest` (discovery, stream ingest, host),
`KalTerm.Api`, `KalTerm.Contracts` (models crossing the API), `KalTerm.Cli`. Schema v1 tables: `market`, `ticker`, `trade`,
`ingest_gap`, `ingest_state`; money columns are `bigint` fixed-point (`_e6` dollars, `_e2` counts); no foreign keys on time-series
tables. Database: `DELAYED_DURABILITY = DISABLED`, `PAGE_VERIFY = CHECKSUM`, a hard `MAXSIZE` set at provisioning. Writers use
`SqlBulkCopy` (flush at 1 s or 5,000 rows; buffer sized for a 30 s stall) and retry on 1205, 4891 and transient errors. Reads use
Dapper. The CLI uses System.CommandLine and Spectre.Console.

Out of M3: TLS and tokens (M4), trade backfill (M4), the Windows service installer (M8), order persistence and trading (M6),
analytics (M5 onward), combo markets.

| Slice | Content | Status |
|---|---|---|
| 3.0 | Prerequisites: CI probe of the runner's SQL Server (D4), this plan | done |
| 3.1 | `KalTerm.Data`: schema v1, DbUp migrations, provisioning, integration tests on LocalDB | done (BUILD-LOG 5.1) |
| 3.2 | Write path: bulk writers, retry policy, buffer and stall behavior | done (BUILD-LOG 5.2) |
| 3.3 | Market discovery (REST, open non-combo markets, `market` upserts) and the refresh schedule | done (BUILD-LOG 5.3) |
| 3.4 | Stream ingest (WebSocket to writers, gap log) and the host | done (BUILD-LOG 5.4 pipeline, 5.5 host) |
| 3.5 | `KalTerm.Api` and `KalTerm.Contracts` (loopback, read endpoints) | done (BUILD-LOG 5.6) |
| 3.6 | `KalTerm.Cli` (market view, status including downtime and gaps) | done (BUILD-LOG 5.7) |
| 3.7 | In-memory order book builder, sequence-gap detection, `book` view | done (BUILD-LOG 5.8) |
| 3.8 | End to end on `kaldb`: the M3 gate (feed ingests and the CLI reads it) | done (BUILD-LOG 5.9) |

**Dev tooling trial: local model for bulk digestion (owner, 2026-10-18): ended the same day.** A local model (Ollama, `qwen2.5-coder:14b`) was tried for digesting large test and log output during slice 3.4a and discontinued by the owner; it, the helper script and the model files were removed from the workstation. On structured output (tests, builds, coverage) `grep`, `tail` and `check-coverage.ps1` are exact, instant and as cheap, and the trial rules still required reading failures from the raw output, so it saved nothing (BUILD-LOG 5.4). Not to be revisited unless the owner raises it.

### 6.5 M4 Server: decisions and slices

Order (owner, 2026-10-19): 4.1 storage governor and retention, 4.2 trade backfill, 4.3 backup and restore, 4.4 candles, 4.5 auth, TLS and pinning.
Each slice gets its own plan for approval before code.

**Candles (slice 4.4), approved 2026-10-19.** Measured on `kaldb` (last hour): 2.27 M ticker rows over 45 k markets collapse to about 465 k market-minutes
with ticks and 30 k with trades, and the raw ticker table grows about 2.2 GB a day, so 1-minute candles for every market are not stored.

- A candle: yes-price open, high, low, close from trades; volume (sum of counts) and trade count; yes bid, yes ask and open interest at the close from the last
  tick of the bucket. A bucket with ticks but no trades has a null price and is not filled forward. Buckets are UTC, aligned to the epoch; money is fixed-point.
- **Stored intervals are configuration** (`Candles:Stored`, each an interval and a scope). Defaults: 1 d and 1 h for all markets, 1 min for the watch list.
  Adding one backfills it from the raw data still retained; removing one stops new rows and leaves the old ones to retention.
- **Requested intervals are a request parameter**: any whole number of minutes from 1 to 1440, and 1 week. It is rolled up exactly (open first, close last, high max, low min,
  volume sum) from the coarsest stored interval that divides it; if none does, it is computed from raw ticks for retained days only, with a row cap. Anything else is a 422.
- Candles are the long-term record: the storage governor drops raw ticker days under the budget and keeps the candles.
- **Gaps (option C):** a candle that overlaps a gap carries `partial: true`, `gap_kind` and the seconds covered. Trade-derived fields lose the flag when trade backfill (4.2)
  closes the gap; ticker-derived fields (bid, ask, open interest) stay partial for good. The CLI shows partial candles in yellow; alerts (M5) can ignore them on purpose.
- Surfaces: `GET /v1/markets/{ticker}/candles?interval=&start=&end=&limit=` (paged like ticks) and `kterm candles TICKER --interval 15m --since 1d`.
  The candle job runs in the ingest host about once a minute for closed buckets.
  Built on `m4/4.4-candles` (BUILD-LOG 5.14); the client time-zone option is still to do.

**Backup and restore (slice 4.3), approved 2026-10-19.** The target is a network share (a mapped drive on kaldb, `\\host\share`). kaldb is in a workgroup and SQL Server runs as a virtual service
account that cannot authenticate to it, so SQL Server backs up to a local **staging** folder and the ingest host verifies and copies the file to the share with a credential kept on the server (DPAPI, machine
scope, SYSTEM and administrators only), then deletes the staging file. Weekly full plus daily differential (the database uses the simple recovery model); retention is grandfather-father-son (7 days of
differentials, 4 weekly fulls, 12 monthly fulls, configurable). The target needs a marker file; a restore is proven only by restoring into a NEW database and comparing it with the manifest (row counts per day
from before and after the backup); restore never overwrites. Backups hold market data only; the key files and the service wrapper settings are **not** in them (documented in BUILD-LOG 5.12). A new
`kalterm-admin` console (on the server) carries the commands; `kterm` stays read-only.

**Auth, TLS and pinning (slice 4.5), approved 2026-10-19.** Named users with per-user tokens (random 256 bits, shown once, only the SHA-256 stored, revocable, optional expiry, roles read/admin). The API
serves the LAN over HTTPS only (self-signed ECDSA certificate made on the server; clients pin its fingerprint on first use and a changed certificate is a hard error), every request there needs a token,
and the process refuses to serve the network without both. A loopback plain-HTTP listener keeps `/healthz`, `/readyz` and `/v1/status` open for checks on the server itself. The Windows Firewall allows
the API port from the local subnet only. `kalterm-admin` manages users, tokens and the certificate; `kterm trust`, `login` and `logout` are the client side. Per-user watchlists, alerts and layouts (M5/M7)
will hang off the user table; mDNS discovery and WebSocket push are not part of 4.5.

**Time zones (standing requirement for every client).** The database and the API are UTC. Client terminals show times in a **user-selectable time zone**: the default is the
system's local zone, but the user can always choose any other zone (or UTC) and the choice is stored per user (with layouts, once M4 auth exists; until then a CLI option and
an environment variable). The system zone is only a default, never a fixed pass-through. The CLI today prints UTC with a "UTC" suffix; the zone option (`--tz`, an IANA or
Windows zone id, and `--utc`) arrives in the first slice that touches CLI output, and every view that prints a time must use it.
Rules (checked against System.CommandLine and PowerShell 2026-10-19: no clashes): `--utc` with `--tz` is a usage error; an unknown zone id is an error; `--json` stays UTC; precedence is `--utc`, then `--tz`, then `KALTERM_TZ`, then the stored per-user setting, then the system zone; no short aliases.

## 7. Risks

- **Durability is unproven.** The premise of SQL Server is a hypothesis; M1 S-1 tests it first.
  The workstation's drive (WD Blue SN580, consumer NVMe) reports no power-loss protection and its
  write cache on. S-1d runs on the target server's drive, which may differ. **S-1a found** that
  with the device cache on, this drive acknowledges write-through writes (SQL's commit path) from
  volatile cache, about 10 times faster than the flash can commit (BUILD-LOG §2.2). The server needs
  either a power-loss-protected drive or its device cache disabled and verified; decide after S-1d.
  **Update 2026-10-18:** the server's drive (an unbranded NVMe, also without power-loss protection) does not show that signature, and
  SQL Server survived three real cord pulls with the cache on (BUILD-LOG 2.6, 2.7). The risk stays open for any other drive: the
  installer's `verify` reports `IsPowerProtected` and the cache state and warns.
- **Rewrite cost.** KalshiTerm2 phases 0 to 6 are complete; this repeats them. Keep milestones tight.
- **Ed25519** is not in the .NET 10 base library (confirmed by S-3). Decision: NSec.Cryptography
  (libsodium; ships one native `libsodium.dll`). BouncyCastle is the managed fallback. Remaining
  check, in S-4: `libsodium.dll` loads on a clean machine. **Resolved in S-4:** it does, once the Visual C++ 2015-2022 x64 runtime is
  present (a clean Windows Server has none); see section 6.3 and BUILD-LOG 2.5.
- **Server 2025 on the OptiPlex:** a consumer-class box. Checked at the start of S-1d: the NIC (Intel I219-LM, driver 12.19.2.57) and the NVMe drive were usable
  after a plain install (the delivered model is an OptiPlex 7060, not the 7080 originally ordered). Its one NIC first negotiated 10 Mbps (cable).
- **Workstation impact** in single-box mode: cap SQL Server memory, measure.
- **Arbitrary machines:** existing SQL Server, no domain, non-English locale, restricted execution policy.

## 8. Reference

KalshiTerm2: `docs/PLAN.md`, `docs/BUILD-LOG.md` (measurements in §10.2), the `kalshi-core` fixtures,
and the API contract tests.

## 9. Parking lot

- Settled-market slimming and tombstones (reference decision 15): our catalog holds only open markets and stays small; revisit if it grows.
- Merging empty old partition boundaries after retention: the 15,000 partition limit is decades away at one boundary per day.

- Python or R research sidecar and out-of-process analyzers.
- Math.NET Numerics / ML.NET for modeling, if analytics deepen.
- Importing KalshiTerm2 history (28 GB at 2026-10-15).
- Replication / Always On; Windows containers.
- Multiple identical servers (OptiPlex 7060 class; 6 cores, one NVMe each). Ideas, none designed or measured, ordered by fit:
  - Redundant ingest: a second box with its own data-only key and WebSocket fills ticker and order book gaps in the first
    (merge or dedupe by sequence number). Targets "downtime is lost data".
  - Restore-verifying standby: a second box restores the latest backup into a new database on a schedule and compares it with
    the row-count manifest. Doubles as a manual failover.
  - Ingest/query split: one box writes, another serves the API and analysis from a restored copy, so heavy queries cannot starve
    the writer or the storage governor.
  - Power-cut test rig: a spare box for repeated S-1d-style cuts while the primary keeps ingesting.
  - Independent verification: run the Python KalshiTerm2 reference against the same feed for ongoing differential testing.
  - Weaker: cold-partition scale-out beyond the 500 GB budget (needs a federation layer); sharding backtests across boxes
    (only if CPU-bound, SQL and I/O more likely limit); clustering or distributed order-book processing (not worth the cost).
- Polly (or `Microsoft.Extensions.Http.Resilience`) for retry, circuit breaking and per-client
  policies. Revisit if M3 ingest or M6 trading needs more than the one hand-written read policy.
  The trading client must not retry writes blindly (orders are not idempotent without a client id).
- S-2 follow-ups: measure the staging-and-switch re-seal of a sealed day (so ingest never waits on a rebuild); repeat the key ingest and
  log-rate runs on the target server (S-1d is done, the server is available); measure order book delta and trade layouts.
- S-4 follow-ups: an install pass on a clean Windows 11 machine (topology A); disabling SQL Server telemetry from the configuration file;
  a pass with a network attached (Windows Update, download paths).
