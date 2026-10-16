# KalTerm plan

**Status: APPROVED by the owner (2026-10-16).** Source of truth. Milestones done: M0. M1 spikes done: S-3, S-1a, S-1b. M2 may start; M3 waits for S-1d (server on order).

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
| **M2 Core** | Fixed-point types, fees, Ed25519 signer, REST (GET-only) and WebSocket clients, rate limiter; verified against Python fixtures | parity tests green; may start once S-1a and S-1b have passed (M2 never touches SQL) |
| **M3 Vertical slice** | Ingest to SQL Server, minimal API, CLI market view | demo feed ingests and the CLI reads it; **may start only after S-1d has passed on the target server** |
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
| **S-1d** | target server (refurbished OptiPlex 7080 MFF, on order) | 3 hard power cuts (cord pulled) under load; after each, `DBCC CHECKDB`, every acknowledged commit present, every row's checksum valid | The authoritative gate, on the target volume |

There is no S-1c: real cuts on the workstation are skipped in favour of the server box.

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
| 2.2 | Fees (taker fee, round-up) | |
| 2.3 | Token-bucket rate limiter on `TimeProvider`, then the coverage gate | |
| 2.4 | Config and environments (demo default, URLs only here) | |
| 2.5 | Ed25519 signer (NSec) against the 21 S-3 vectors | |
| 2.6 | Clock-skew estimator | |
| 2.7 | REST read client and models (GET-only, retries, pagination) | |
| 2.8 | WebSocket client (reconnect, resubscribe, sequence-gap recovery) | |

## 7. Risks

- **Durability is unproven.** The premise of SQL Server is a hypothesis; M1 S-1 tests it first.
  The workstation's drive (WD Blue SN580, consumer NVMe) reports no power-loss protection and its
  write cache on. S-1d runs on the target server's drive, which may differ. **S-1a found** that
  with the device cache on, this drive acknowledges write-through writes (SQL's commit path) from
  volatile cache, about 10 times faster than the flash can commit (BUILD-LOG §2.2). The server needs
  either a power-loss-protected drive or its device cache disabled and verified; decide after S-1d.
- **Rewrite cost.** KalshiTerm2 phases 0 to 6 are complete; this repeats them. Keep milestones tight.
- **Ed25519** is not in the .NET 10 base library (confirmed by S-3). Decision: NSec.Cryptography
  (libsodium; ships one native `libsodium.dll`). BouncyCastle is the managed fallback. Remaining
  check, in S-4: `libsodium.dll` loads on a clean machine.
- **Server 2025 on the OptiPlex 7080:** a consumer-class box; confirm drivers (NIC, storage) install
  before relying on it. Check at the start of S-1d.
- **Workstation impact** in single-box mode: cap SQL Server memory, measure.
- **Arbitrary machines:** existing SQL Server, no domain, non-English locale, restricted execution policy.

## 8. Reference

KalshiTerm2: `docs/PLAN.md`, `docs/BUILD-LOG.md` (measurements in §10.2), the `kalshi-core` fixtures,
and the API contract tests.

## 9. Parking lot

- Python or R research sidecar and out-of-process analyzers.
- Math.NET Numerics / ML.NET for modeling, if analytics deepen.
- Importing KalshiTerm2 history (28 GB at 2026-10-15).
- Replication / Always On; Windows containers.
- Polly (or `Microsoft.Extensions.Http.Resilience`) for retry, circuit breaking and per-client
  policies. Revisit if M3 ingest or M6 trading needs more than the one hand-written read policy.
  The trading client must not retry writes blindly (orders are not idempotent without a client id).
