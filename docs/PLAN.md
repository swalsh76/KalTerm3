# KalTerm plan

**Status: APPROVED by the owner (2026-10-16).** Source of truth. Milestones done: M0.

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
| **M1 Spikes** (throwaway, measured) | **S-1 durability:** `diskspd` write-through benchmark on the target volume, then SQL Server commit latency, then 3 hard power cuts under load with `DBCC CHECKDB` after each. **S-2 time-series layout:** daily partitioned columnstore vs hot rowstore plus cold columnstore; insert rate, storage per million rows, query latency. **S-3 Ed25519:** signing library choice, verified against the Python signing vectors. **S-4 install:** unattended instance install on a clean VM | go/no-go; S-1 failing ends the SQL Server path |
| **M2 Core** | Fixed-point types, fees, Ed25519 signer, REST (GET-only) and WebSocket clients, rate limiter; verified against Python fixtures | parity tests green |
| **M3 Vertical slice** | Ingest to SQL Server, minimal API, CLI market view | demo feed ingests and the CLI reads it |
| **M4 Server** | Storage governor, retention, candles, status, auth, TLS and pinning, backup and restore | API contract tests green |
| **M5 Analytics** | Runner, built-in metrics, alerts, push | per-user alerts fire |
| **M6 Trading** | Risk layer, trading client, kill switch; production off by default | verified under tiny limits, with the owner's go-ahead |
| **M7 UI** | WPF terminal | decided later |
| **M8 Host layer and verification** | Installer, services, reboot and power-cut tests, 48 h soak | all pass |

## 7. Risks

- **Durability is unproven.** The premise of SQL Server is a hypothesis; M1 S-1 tests it first.
- **Rewrite cost.** KalshiTerm2 phases 0 to 6 are complete; this repeats them. Keep milestones tight.
- **Ed25519** is not in the .NET base library as far as known; S-3 picks a library.
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
