# BUILD-LOG

How each slice was built and verified. Section numbers follow PLAN.md milestones.

## 0. Setup (2026-10-15)

- Repo created. .NET SDK 10.0.402 installed with winget; global.json pins it.

## 1. M0 Scaffold (2026-10-16)

- Added `KalTerm.sln`, `Directory.Build.props` (net10.0, nullable, warnings as errors, latest-recommended
  analyzers), `Directory.Packages.props` (central package management), `.gitattributes` (CRLF).
- Projects: `src/KalTerm.Core` (one type, `BuildInfo`) and `tests/KalTerm.Core.Tests` (xUnit v3).
- Test stack: `xunit.v3.mtp-v2` 4.0.2, `coverlet.MTP` 10.1.0, `Microsoft.Testing.Extensions.TrxReport`
  2.5.1. `global.json` selects the Microsoft Testing Platform runner for `dotnet test`.
- Findings while verifying locally:
  - The plain `xunit.v3` package cannot run under `dotnet test` on the .NET 10 SDK in VSTest mode; MTP
    mode is required, and coverlet.MTP only registers with the `xunit.v3.mtp-v2` flavour.
  - `--report-trx` needs the TrxReport package, otherwise the test host fails its handshake.
  - CA1707 forbids underscores in test names: tests use plain PascalCase (`VersionIsNotEmpty`).
- Local verification: `dotnet format --verify-no-changes`, Release build (0 warnings), 2 of 2 tests
  passing, TRX and Cobertura coverage files produced.
- CI (`.github/workflows/ci.yml`): windows-latest, actions pinned to commit SHAs, same steps as above.
  Verified on GitHub: PR #1, `build-test` passed in 1m12s, merged to `main` (7056292). M0 gate met.

## 2. M1 Spikes

### 2.1 S-3 Ed25519 (2026-10-16): done

Question: which .NET library signs Ed25519 byte-identically to the Python reference and loads its
key files? Spike code is on branch `spike/s3-ed25519` (813c829), deliberately not merged.

- The reference has no fixed signing vectors (`test_auth.py` uses random keys). Ed25519 is
  deterministic (RFC 8032), so vectors were generated with Python `cryptography` 50.0.2 from fixed
  seeds: 3 RFC 8032 section 7.1 vectors (the generator asserts the library reproduces them) and 18
  Kalshi-shaped messages (`timestamp + METHOD + path`, query stripped; GET, POST, DELETE, empty
  path, non-ASCII; 3 seeds). Each vector carries the PKCS#8 PEM. Plus 6 negative key files: RSA, EC,
  encrypted Ed25519, garbage, empty, public-key-only.
- .NET 10.0.13 has no Ed25519 types in the base library.

| | NSec.Cryptography 26.4.0 | BouncyCastle.Cryptography 2.7.0 |
|---|---|---|
| Positive vectors identical to Python | 21 of 21 | 21 of 21 |
| Bad key files rejected | 6 of 6 | 6 of 6 |
| Sign latency (20,000 signs, Release) | 20.3 us | 26.5 us |
| License | MIT | MIT |
| Footprint, win-x64 publish | 108 KB + native `libsodium.dll` 452 KB | 5 MB managed DLL, no native code |

- Decision: **NSec.Cryptography**. Thin wrapper over libsodium; keys default to a non-exportable
  policy (documented, not tested here). BouncyCastle is the fallback.
- Both libraries reject bad keys with different exception types; the M2 loader must wrap them in
  one auth exception, as the Python loader does.
- Not verified: key-memory protection (M2/M6), and whether `libsodium.dll` loads on a clean
  machine without extra runtimes (S-4).
- The 21 vectors are reusable for the M2 parity tests; they live only on the spike branch until M2
  asks for them.

### 2.2 S-1a flush path, workstation (2026-10-16): done, red flag found

Question: when SQL Server acknowledges a commit, is the data on the flash? Method, scripts and raw
results: branch `spike/s1-durability` (2b51c0e, not merged). Hardware: MSI MS-7D78, WD Blue SN580
2 TB NVMe (consumer, DRAM-less), one volume (C:), Windows 11 Pro 26300. Windows reports the drive
`IsPowerProtected = False`, device write cache enabled. SQL Server 2025 17.0.1000.7 Enterprise
Developer, default instance, max server memory capped at 16 GB for the tests. Scratch files in
`C:\KalTermSpikes\s1`; test database `S1Test` (`PAGE_VERIFY CHECKSUM`, `DELAYED_DURABILITY
DISABLED`, simple recovery). `diskspd` 2.3, Microsoft-signed.

A-B-A: device write cache on (A1), off via Device Manager (B), on again (A2). One thread, queue
depth 1. The A1 and A2 baselines agree.

| Measure | A1 cache on | B cache off | A2 cache on |
|---|---|---|---|
| 4K write-through write (`diskspd -Sw`) | 23 us | 215 us | 23 us |
| 4K no-buffer + write-through (`-Sh`) | 19 us | 209 us | 19 us |
| 8K random write-through | 26 us | 233 us | 26 us |
| 4K write + `FlushFileBuffers` | 228 us | 488 us | 221 us |
| SQL commit, mean (20,000 autocommits) | 48 us | 234 us | 62 us |
| SQL log write stall per write | 28 us | 203 us | 36 us |
| SQL commits per second | 20,731 | 4,272 | 16,115 |

- **Finding:** with the device cache on, write-through writes (what SQL Server uses for its log:
  one write per commit, no separate flush) are acknowledged in about 20 to 30 us, while the flash
  needs about 200 us to commit a write (the cache-off figure). So the acknowledged data was in the
  drive's volatile cache, not on the flash. A power cut would lose acknowledged commits. This is
  the KalshiTerm2 failure mode, reproduced on Windows before any power was pulled.
- An explicit flush (about 220 us, cache on) does reach the flash.
- With the cache off, SQL still commits about 4,300 per second single-threaded, well above what the
  batched ingest needs. Candidate fixes (decision after S-1d, in PLAN.md): a drive with power-loss
  protection, or the device cache disabled with the installer's `verify` checking it, or both. A UPS
  alone does not close the gap (a crash or reset still loses the cache).
- Limits: one drive, one run per state, a busy workstation. This is a measurement of acknowledgement
  latency, not a power-loss test; S-1d on the target server is the gate.
- Housekeeping: the cache was restored to on. Max server memory was capped at 16 GB for the tests
  and restored to the default when S-1b ended.

### 2.3 S-1b crash consistency, workstation (2026-10-16): done, passed

Question: do acknowledged commits survive a hard crash of `sqlservr.exe`, and is the database left
consistent? Code: branch `spike/s1-durability` (0106a4c, not merged), `S1.CrashTest`. Same machine
and database settings as 2.2 (device cache on). A process kill never loses the drive's cache, so
this tests SQL Server's recovery, not the drive; it does not contradict 2.2.

- Table `dbo.S1Rows (Id bigint PK, Payload binary(32))`, payload = SHA-256 of the big-endian id.
- Writer: 8 threads, each with its own id range; every operation is a single-row autocommit or a
  100-row transaction. An id counts as acknowledged only when SQL Server returns success.
- Loop: load for 3 to 8 s at a random moment, `Process.Kill()` on `sqlservr.exe`, wait for the
  writers to fail, restart the service (bounded 180 s wait), then verify.
- Verifier: `DBCC CHECKDB (all errors, EXTENDED_LOGICAL_CHECKS)` must report nothing; for each
  writer every id from its first to its last acknowledged id is present; no row fails
  `HASHBYTES('SHA2_256', CONVERT(binary(8), Id))`; the total never falls between cycles.
- Self-test first (the test of the test): the verifier passes a clean database and detects a deleted
  acknowledged row, a corrupted payload, and a wholly zeroed data page.
- The SQL error log confirms real crash recovery each time (for example "2980 transactions rolled
  forward, 1 transactions rolled back").

| Result over 10 cycles | |
|---|---|
| Cycles passed | 10 of 10 |
| Rows acknowledged | 2.5 to 6.4 million per cycle; 42.5 million in total |
| Acknowledged rows missing | 0 in every cycle |
| Rows failing their checksum | 0 in every cycle |
| `CHECKDB` | clean in every cycle |
| Kill to database online | 2.0 to 5.1 s |
| Unacknowledged in-flight rows | 0 to 202 per cycle (allowed either way) |

- Conclusion: SQL Server's own crash recovery is sound under this load. The M2 gate (S-1a and S-1b)
  is met; M3 still waits for S-1d.
- For S-1d: the writer and verifier are reusable, but need (a) an acknowledgement ledger on another
  machine, (b) a way to run the writer against SQL on the server, which needs a temporary TCP
  listener and firewall rule on the server (a spike-only exception to the shared-memory-only rule,
  removed afterwards), and (c) a power-cut orchestrator instead of the process kill.
- Dependency note for M3: `Microsoft.Data.SqlClient` 7.1.1 brought in
  `System.Security.Cryptography.Pkcs` 9.0.18, which carries known advisories. Irrelevant for a local
  spike; pin a patched version when the real client dependency is chosen.

### 2.4 S-2 time-series layout, workstation (2026-10-17): done, A-ordered recommended

Question: which layout carries the ticker stream (the dominant one) and still serves screener and chart queries, over a 14-day
window? Code: branch `spike/s2-timeseries` (local, not merged, like S-1), `spikes/s2-timeseries/S2.Layouts`. Results files and
logs are outside the repository (`C:\KalTermSpikes\s2`). Same machine as 2.2 and 2.3 (device cache on); SQL Server `max server memory`
capped at 16 GB for the runs and restored afterwards; each database had a hard `MAXSIZE` (80 GB data, 40 GB log) and was dropped.

**Layouts.** All three read through one view, so the query text is identical.
- **A:** one table, daily-partitioned clustered columnstore.
- **A-ordered:** as A with `ORDER (market_id, ts)`; each sealed day is rebuilt (ordered) after it loads, about 25 s per 60 M-row day.
- **B:** a daily-partitioned rowstore for recent days (clustered on `(ts, market_id)` plus a `(market_id, ts)` index) and a cold
  daily-partitioned columnstore; the day-end job moves yesterday across by `SWITCH` out, `INSERT ... SELECT` and `TRUNCATE` of the stage.

**Workload.** A deterministic synthetic ticker stream: 8,000 markets with a heavy-tailed activity distribution (a few markets
produce most updates), prices as random walks in whole cents held as 10^-6 dollars, sizes in hundredths, cumulative volume, 10
columns, about 90 bytes as a row. 700 rows/s (the reference's measured ticker rate) is 60.48 M rows a day; 13 historical days
(786 M rows) were bulk-loaded, then day 14 was written live: 700, 3,500 and 14,000 rows/s (1x, 5x, 20x) and unthrottled, 60 s each,
in one-second batches, with a reader looking up the latest row of a hot market every 100 ms.

**The harness was verified before it was trusted.** `verify` loads a small dataset into each layout and compares every query's result,
row for row, with an independent in-memory computation over the same generated rows; it also checks that ingest adds exactly the rows
written, that retention removes exactly the right rows, and that day-end conserves every row. A negative control (one row deleted
behind its back) makes it fail, as it must. The verifier and a scaled-down dry run found two real problems before the long runs
(findings 2 and 7 below).

| 13 days (786 M rows) | A | A-ordered | B (rerun) |
|---|---|---|---|
| Storage | 15,642 MB, 20.9 B/row | 15,492 MB, 20.7 B/row | cold 15,233 MB, 22.0 B/row; **hot day 7,009 MB, 121.5 B/row** |
| Ingest 1x / 5x / 20x, batch p99 | 42 / 24 / 245 ms | 49 / 46 / 188 ms | 12 / 21 / 66 ms |
| Ingest unthrottled | 229,000 rows/s, p99 207 ms | 215,000 rows/s, p99 233 ms | 206,000 rows/s, p99 72 ms |
| Batches over 1 s, deadlocks | 0, 0 | 0, 0 | 0, 0 |
| Log per ingested row (5x / 20x) | 17 / 14 B | 16 / 14 B | 180 / 141 B |
| q1 latest row per market, last hour (warm) | 291 ms | 224 ms | 458 ms |
| q2 one market, raw rows, 24 h (warm) | 241 ms | **110 ms** | 124 ms |
| q3 one market, per minute, 24 h (warm) | 302 ms | **33 ms** | 63 ms |
| q4 every market, per hour, 24 h, full scan (warm) | 1.2 s | 1.3 s | **11.4 s** (a day is hot) |
| q5 one market, per hour, all days (warm) | 162 ms | 149 ms | 202 ms |
| `TRUNCATE` of the oldest day (60.5 M rows) | 8 ms | 5 ms | 13 ms |
| `DELETE` of one day | 127 s, 13.1 GB of log | 198 s, 13.9 GB of log | 125 s, 13.1 GB of log |
| Day-end work, with 1x ingest running | compress today's row groups 0.8 s, no pause | ordered rebuild of yesterday **27 s; ingest stalled 27 s** (1 deadlock, retried) | switch out **3.7 s** (ingest stalled; 1 retry), move to cold **82 s**, `TRUNCATE` of the stage 2 ms; **readers blocked 85 s** |
| After the rollover (B) | | | q1 262 ms, q2 96 ms, q3 21 ms, q4 1.26 s, q5 414 ms |

Pass criteria, fixed before the runs: sustain at least 5x the expected peak (about 4,000 rows/s) with batch p99 under 500 ms;
chart queries (q2, q3) under 200 ms warm; the full scan (q4) under 5 s; dropping a day under 1 s without a log blow-up; on a tie the
simpler layout wins. **A:** misses the chart criterion narrowly (241 and 302 ms). **A-ordered:** meets every criterion. **B:** meets
the chart and ingest criteria and **fails the full scan (11.4 s) while a day is in the hot rowstore**; after the rollover it passes.
All three drop a day in milliseconds with `TRUNCATE`.

**Findings (apply them to the M3 schema):**
1. **Partition by day and drop a day with `TRUNCATE ... WITH (PARTITIONS (n))`.** 5 to 13 ms, 164 to 303 KB of log, against a
   `DELETE` of the same rows: 2 to 3 minutes and 13 to 14 GB of log, which would nearly fill the 20 GB log cap. The storage governor must
   drop partitions, never delete rows.
2. **`LOCK_ESCALATION = AUTO` on every partitioned table.** Without it, sealing one day's partition deadlocked with ingest into another
   (found by the verifier on the ordered layout).
3. **The ingest writer must retry** on error 1205 (deadlock victim) and **error 4891** ("insert bulk failed due to a schema change of the
   target table"): a partition `SWITCH` invalidates an in-flight bulk insert. B's first run died on the second one.
4. **Ordering the columnstore by `(market_id, ts)` costs nothing in space** (20.7 against 20.9 B/row) and speeds the single-market
   chart queries (q3 9x: 302 to 33 ms). Note on noise: the same structure compressed to 20.5 to 24.8 B/row across runs and points
   (B's cold tier read 23.8 B/row at 7 days in the first attempt and 21.2 in the rerun), so ordered-versus-unordered storage differences
   below about 10% are within run-to-run variation.
5. **Rowstore is expensive:** 122 B/row (about 6x the columnstore) and about 10x the log per row. A hot rowstore tier is affordable for
   one day (7 to 8.6 GB), not for several, and it makes the full-scan query 9x slower while it holds the last day.
6. **`INSERT ... WITH (TABLOCK)` into the cold columnstore blocks readers of the view for the whole move (82 s).** Without `TABLOCK` the
   rows do not compress directly.
7. **`sys.dm_db_partition_stats.row_count` is unreliable for columnstore** (it overstated rows by up to 1.5x during delta-store
   transitions). Count with `COUNT_BIG(*)`.
8. Rebuilding a sealed day with the ordered `REBUILD PARTITION` takes 24 to 27 s per 60 M rows but **blocks ingest into that partition for
   the duration**. The remedy (rebuild into a staging table, then `SWITCH` it in, so the live table is never locked) was **not measured**.
   Until it is, ingest must buffer at least 30 s of data (about 20,000 rows at 700 rows/s).

**Run-to-run noise.** The first ingest test after a fresh load costs more than the later ones, and by a varying amount (B's first test
logged 896 B/row in the first attempt and 271 in the rerun; the ordered layout's logged 106 against 16 at 5x). The steady-state rows
(5x and up) are the comparison. The unthrottled test varied by more than 40% between B's two runs (138,000 and 206,000 rows/s) while the machine
was busier in the first. The reader's latency under the unthrottled flood (1.5 s on columnstore, 2.9 s on the hot rowstore) is the cost
of a flood 300x the real rate and is not a layout difference.

**Caveats.** Synthetic data (random walks, whole cents); real data may compress differently. Only the ticker stream was measured; order
book deltas and trades were not. This workstation's NVMe acknowledges write-through writes from volatile cache (2.2), so **ingest and
log-rate figures are optimistic** until the key runs are repeated on the target server during S-1d. Storage and query figures do not depend on
the flush path.

**Recommendation (not yet approved by the owner): A-ordered**: one daily-partitioned, ordered clustered columnstore. It meets every
criterion with the least machinery (no hot/cold tiers, no rollover job, no view). Its one weakness is the sealing pause (finding 8). B's
advantage is a shorter pause (3.7 s) at the price of a second table, a view, a stage table and a rollover job, 7 to 8.6 GB of rowstore a day, and
the slow full scan. Open work: measure the staging-and-switch re-seal before the M3 schema is final.

### 2.5 S-4 unattended install on a clean machine (2026-10-18): done, passed

Question: on a pristine Windows Server 2025, with nobody at the console, can we load `libsodium.dll` and sign Ed25519 exactly as the
Python reference does, and install the KalTerm SQL Server instance with Windows authentication only and **no network exposure**, every wait
bounded, a preflight that refuses when a prerequisite is missing, and survival of a reboot? Code: branch `spike/s4-install` (local, not
merged), `spikes/s4-install` (host and guest PowerShell, an answer-file template, and `LibsodiumProbe`, a self-contained console app that
signs the 21 committed vectors with the product's own `Ed25519PrivateKey`). Nothing secret is committed: the VM's administrator password is
random and kept in a DPAPI-protected file outside the repository.

**Setup.** Hyper-V generation 2 VM (4 vCPU, 8 GB, 80 GB dynamic disk, no network adapter), installed unattended from the Windows Server 2025
evaluation ISO (Standard, Desktop Experience) with an `autounattend.xml` on a small ISO built with the built-in IMAPI2 classes (no ADK):
**7.2 minutes** from start to a usable guest, then a checkpoint named `clean` (18.4 GB on disk). Files go in over PowerShell Direct; every step
runs as SYSTEM in a scheduled task with a timeout, as a service installer would run. SQL Server media: the 2025 Enterprise Developer
set already on this workstation (`C:\SQL2025\EntDev_ENU`).

**Result: two consecutive runs from the checkpoint, 15 of 15 steps each, about 6 minutes of step time each (362 s and 364 s):**

| Step | Result |
|---|---|
| The pristine OS cannot load libsodium (documents the runtime requirement) | fails as expected, exit 255 |
| Preflight refuses while the Visual C++ runtime is missing | exit 1, "REFUSING" |
| Runtime install `-WhatIf`; install; install again | no change; 5 s; idempotent |
| Probe once the runtime is there, as SYSTEM | 21 of 21 vectors signed exactly |
| Preflight passes; refuses when disk space is short; a refused install changes nothing; install `-WhatIf` changes nothing | as designed |
| Unattended SQL Server install, as SYSTEM | **exit 0 in 182 s and 185 s** |
| Probe after SQL Server | 21 of 21 |
| Reboot: the instance comes up on its own and verifies | up and answering **108 s** after the wait began (delayed start) |
| A second install | refused (the instance exists) |

**What the installed instance looks like:** Enterprise Developer 17.0.1000.7; named instance `KALTERM`; service account
`NT Service\MSSQL$KALTERM`; `IsIntegratedSecurityOnly = 1`; protocols in the registry Sm = 1, Np = 0, Tcp = 0 (set from the
configuration file with `TCPENABLED=0` and `NPENABLED=0`); data, logs, tempdb and backups under a chosen directory.

**Findings (apply them to M8):**
1. **The Visual C++ 2015-2022 x64 runtime is a hard requirement.** A clean Windows Server has none, and the product's signer then fails with
   `DllNotFoundException: Unable to load DLL 'libsodium' or one of its dependencies` (as Administrator and as SYSTEM alike). Investigated
   from the checkpoint: installing only the runtime (3 s, `VC_REDIST_X64.EXE` ships in the SQL media under `redist\VisualStudioShell\VCRuntimes`)
   fixes it; so does placing `vcruntime140.dll` and `vcruntime140_1.dll` next to `libsodium.dll` (app-local, nothing installed). The installer
   must do one of the two (app-local DLLs must come from the official redistributable under Microsoft's redistribution terms), and the
   preflight now checks for the runtime.
2. **"Shared memory only" means "no network exposure", not "no TCP socket".** SQL Server always opens a Dedicated Admin Connection on
   loopback (127.0.0.1 and ::1, a high port), reachable only by sysadmins; `remote admin connections = 0` keeps it local, and it cannot be turned
   off. The local named pipe `\\.\pipe\MSSQL$KALTERM\sql\query` also accepts local connections but is not reachable over the network (port 445
   is closed). The verification therefore asserts: protocols disabled in the registry; no SQL Server listener on a non-loopback address; the
   machine address refuses TCP 1433; the pipe is not reachable over the network. (The first version treated the loopback listener and the local
   pipe as failures, and its "TCP refused" check connected by instance name, which needs SQL Browser, so it could pass for the wrong reason.)
3. **The service is Automatic (Delayed Start):** about two minutes after boot it answers. Anything that checks it after a reboot (the
   installer's `verify`, a health check) must wait, bounded, for it.
4. **Setup also installs the SQL Server Agent and the telemetry (CEIP) service and enables usage metrics.** For a trading terminal telemetry
   should be off; the setup parameters for that were **not tested** here (M8).
5. **Self-contained publishing needs no .NET runtime on the target** (the probe is 79 MB); only the native runtime above is needed.
6. Harness lessons: a blank-disk install DVD shows "Press any key to boot from CD or DVD" and gives up in seconds, so the VM script presses a key
   through Hyper-V's keyboard interface; PowerShell transcripts log caught errors as `TerminatingError`, so monitors must not treat them as
   failures; `Set-VM` takes `-AutomaticCheckpointsEnabled`, not `-AutomaticCheckpointType`.

**Limits.** Windows Server 2025 only (decision 8); topology A (a single Windows 11 box) is not covered. The VM had no network, so Windows
Update and download behaviour are untested. Setup ran as SYSTEM in a scheduled task, equivalent to but not identical with a real installer.
Evaluation media (180 days), not a licensed install.

### 2.6 The target server and S-1a on its drive (2026-10-18): done

**The box.** A Dell **OptiPlex 7060** (not the 7080 Micro that was ordered; the owner did not take the difference up with the seller): service tag
3YY93Y2, BIOS 1.32.0, Intel Core i7-8700 (6 cores, 12 threads, 3.2 GHz, 8th-generation desktop part), 31.8 GB RAM, one NVMe drive, an Intel
I219-LM NIC. Windows Server 2025 **Datacenter Evaluation** (build 26100; S-4 tested Standard, same engine), installed by hand from the
evaluation ISO, hostname `kaldb`, 192.168.1.20 on the workstation's LAN. BIOS: AHCI/NVMe mode, **AC Recovery = Power On**. The NIC first
negotiated 10 Mbps (a cable or port problem) and was fixed to 1 Gbps.

**Remote management.** WinRM on 5985 with the firewall rule limited to the workstation's address; a local administrator account `kalterm`
whose credential is kept in a DPAPI-protected file on the workstation (the assistant uses it without ever seeing the password);
`LocalAccountTokenFilterPolicy = 1` (without it Windows strips the administrator rights from a network logon by a local account other than
the built-in Administrator, and WinRM answers "Access is denied"; revisit before production); this workstation's WinRM client trusts exactly
that one address; RDP enabled for the workstation only; High-performance power plan; automatic Windows Update off for the test period
(**re-enable before production**: a surprise reboot ruins a run).

**The drive.** A generic, unbranded NVMe: `SSD 1TB`, firmware SN15108, 954 GB, wear 0%, 45 C. Windows reports `IsPowerProtected = False` and
`IsDeviceCacheEnabled = True`: no power-loss protection and a volatile cache, the S-1a situation.

**S-1a on this drive** (`diskspd` 2.x from Microsoft's GitHub release, signature verified; the same scripts as 2.2, branch
`spike/s1d-power-cuts`; cache on / off / on, the off state set through the registry value `UserWriteCacheSetting` under the device's
`Device Parameters\Disk` key plus a reboot, then restored):

| 4K write, queue depth 1 | Workstation WD SN580, cache on | Workstation, cache off | **Server, cache on (A1 / A2)** | Server, cache off |
|---|---|---|---|---|
| write-through (`-Sw`) | **23 us** | 215 us | **314 / 295 us** | 573 us |
| no-buffer + write-through (`-Sh`) | 19 us | 209 us | 302 / 372 us | 325 us |
| random 8K write-through | 26 us | 233 us | 254 / 245 us | 452 us |
| 4K write + `FlushFileBuffers` | 228 us | 488 us | 1.1 to 1.9 ms | 1.1 ms |
| SQL single-thread autocommit, mean | 48 us | 234 us | **303 us** (3,299 commits/s) | not run |

The workstation's drive acknowledged write-through writes 10x faster than its flash could commit them when its cache was on (the
KalshiTerm2 failure mode). The server's drive takes about 0.3 ms either way, the order of a real flash commit: **the red-flag
signature is absent**. An explicit flush is slow on this drive (0.8 to 1.6 ms extra); SQL's log does not use it, checkpoints do.

**SQL Server on the server.** Installed with the S-4 scripts (VC++ runtime 2 s, preflight 11 of 11, unattended setup 221 s, verification
passed: shared memory, Windows authentication only, no listener on a non-loopback address); data under `C:\KalTerm\data`; Windows
Defender exclusions for that folder and `sqlservr.exe` only (real-time protection stays on).

### 2.7 S-1d real power cuts on the target server (2026-10-18): passed

Question: after a real cord pull under write load, is every commit SQL Server acknowledged still there, and is the database intact?
Code: branch `spike/s1d-power-cuts` (local, not merged), `spikes/s1d-server`, program `S1d.PowerCut`.

**Method.** The writer runs **on the server** (8 threads; a single-row autocommit or a 100-row transaction; each row is `(id, SHA-256 of
the id)`; a checkpoint every 5 s so a cut is likely to land while data pages are being written) and talks to SQL Server over shared
memory, as the product will, so no TCP listener was opened and "shared memory only" stayed intact. The moment SQL Server acknowledges a
commit the writer sends it, over a no-delay TCP connection, to a **ledger on the workstation** (a machine that is not being cut), which
appends it to a file and keeps the highest acknowledged id per thread. After the cut the verifier waits (bounded) for the instance, runs
`DBCC CHECKDB (all errors, extended logical checks)`, and checks that every acknowledged id up to each thread's last is present with a
valid checksum, against **every ledger of the database so far**, so older data is re-proved after each later crash. Settings:
`DELAYED_DURABILITY = DISABLED`, `PAGE_VERIFY = CHECKSUM`, simple recovery, device cache at its default (**on**). The ledger detects a
silent power loss by a 15 s read timeout (a powered-off machine sends no FIN or reset).

**The harness was proven before any cut.** Self-test: the verifier detects a deleted acknowledged row, a corrupted payload, a fallen row
count and a wholly zeroed data page. Two rehearsals on a separate database: a hard kill of `sqlservr.exe` under load (8.3 M acknowledged
rows, genuine crash recovery in 3 s) and a hard kill of the writer (15.7 M rows across both cycles), both passed. A negative control
(a forged ledger claiming a million rows SQL Server never wrote) failed with 999,900 acknowledged rows missing, exit code 1.

| Real cord pull | Cycle 1 | Cycle 2 | Cycle 3 |
|---|---|---|---|
| Load before the cut | 26 s | 56 s | 48 s |
| Acknowledgements in the ledger | 181,928 | 360,386 | 304,527 |
| Server's own evidence | Kernel-Power 41, event 6008 | same | same |
| SQL crash recovery | 8,691 rolled forward, 1 back | 19,888 forward, 4 back | 23,647 forward, 3 back |
| Power-on to usable (the box powered itself on) | about 30 s | about 30 s | about 30 s |
| Acknowledged rows verified (cumulative) | 9,174,395 | 27,426,457 | **42,839,572** |
| **Missing acknowledged rows** | **0** | **0** | **0** |
| Bad checksums; `CHECKDB` | 0; clean | 0; clean | 0; clean |
| Rows beyond the acknowledged ones (in flight at the cut; allowed either way) | 100 | 101 | 203 |

**Result: passed (3 of 3).** With the drive's write cache enabled and no power-loss protection, SQL Server's commits survived real cord pulls.
This matches the S-1a latency result for this drive. The database always ended up ahead of the ledger, never behind. The PLAN section 6
M3 gate ("S-1d has passed on the target server") is met on this machine.

**Limits (these belong in any decision that leans on this result).**
- Three cuts are evidence, not proof; each cut came 26 to 56 s into the load. More cuts, longer runs and a heavier data-page workload would
  raise confidence.
- A loss in the last few milliseconds before a cut would show only as a ledger undercount, not as a missing row; the direction of the
  error is safe but the test is blind to it.
- One unbranded drive in a single-drive box. A different drive may behave differently, so the product's installer `verify` must still
  report `IsPowerProtected` and the device cache state and warn when there is no power-loss protection.
- The OS and the data share the drive. Windows came back cleanly all three times (no repair prompts, no file-system errors seen).

**Lessons.** Detect a silent power loss with a read timeout, not a connection event. PowerShell parameter names must not collide with
common-parameter aliases (`-Db` is `-Debug`'s alias). The harness's own path guard blocks `Remove-Item` on paths under the spike folder
(a false positive on paths with spaces or wildcards), so tools delete databases through SQL and leave files in place. Write scripts to
files; large heredocs and backslash-heavy one-liners are fragile here.

### 2.8 M3 slice 3.0: what SQL Server does the CI runner offer (2026-10-18)

A throwaway workflow on the branch `ci/probe-sql` (never merged) ran on `windows-latest` (image `windows-2025-vs2026` 20261007.264.1,
Windows Server 2025, 4 cores, 16 GB, 33 GB free on C:). No SQL Server service is installed (only `SQLWriter`), but **LocalDB is**:
instance `MSSQLLocalDB`, version SQL Server 2025 RTM-CU3 17.0.4025.3, Express edition, with `SqlLocalDB.exe` and `sqlcmd` on the PATH.
On a fresh LocalDB instance every feature the S-2 layout needs worked: partition function and scheme, a partitioned table with an
ordered clustered columnstore index, `LOCK_ESCALATION = AUTO`, `DATETRUNC`, `TRUNCATE ... WITH (PARTITIONS (1))` (one row left of two),
and `REORGANIZE ... COMPRESS_ALL_ROW_GROUPS`. Decision D4: integration tests use LocalDB in CI. Lesson: a final `choco list | Select-String`
with no match returned exit 1 and failed the step; end diagnostic steps with `exit 0`.

## 3. M2 Core

### 3.1 Fixed-point money (2026-10-16): done

- `KalTerm.Core.Money`: `Dollars` (10^-6) and `Contracts` (10^-2), `readonly record struct` over a
  `long`; `PrecisionException`; an internal exact parser. Parse, `TryParse`, `FromDecimal`,
  `ToDecimal`, invariant `ToString`, checked add/subtract/negate, ordering. `long.MinValue` is not a
  valid value, so the range is ±(2^63 - 1) as in the reference.
- No `double` or `float` anywhere in the public surface (a reflection test enforces it). Deliberate
  deviation from the reference, which accepts floats via their shortest decimal form.
- The parser is exact integer arithmetic (no BigInteger, no floating point): significant digits are
  stripped of leading and trailing zeros, so any remaining negative shift means "too precise".
- Parity: `tools/vectors/generate_fixedpoint.py` loads the reference `fixedpoint.py` by path as the
  oracle (reference commit 3d19d50) and writes 210 parse cases (102 refusals), 34 round trips and 12
  float inputs that are unsupported by design. Committed at
  `tests/KalTerm.Core.Tests/Vectors/fixedpoint.json`; CI never runs Python.
- Where KalTerm is deliberately stricter than the reference (whitespace, underscores, non-ASCII
  digits) the vector records `csharp: refuse`.
- **Finding in the reference** (reported, not edited): `to_e6("1e-999999999")` returns 0. Python's
  `Decimal` underflows at extreme exponents, so a nonzero value silently becomes zero and passes the
  exactness check, which breaks the reference's own "refuse, never round" rule. No practical impact
  (Kalshi never sends such values). The port refuses it; the vector carries a `note`.
- Verification: 278 tests pass (244 from vectors), build with 0 warnings, `dotnet format` clean;
  coverage 98.1% lines and 95.8% branches (`Dollars` and `Contracts` 100%).

### 3.2 Fees (2026-10-16): done

- `KalTerm.Core.Fees`: `FeeMultiplier` (non-negative, 10^-6) and `KalshiFees` with `TakerFee`,
  `RoundUp`, `TakerMultiplier` and `QuadraticFamily`. Typed: `TakerFee(FeeMultiplier, Contracts, Dollars)`
  returns `Dollars`. Only the quadratic family is modelled; other fee types return `null` so callers
  skip rather than guess (as in the reference).
- Exact: the numerator `M x 7 x C x P x (10^6 - P)` is checked 128-bit arithmetic, divided by 10^16 and
  rounded up; no floating point. The reference uses unbounded integers; a fee that does not fit a `long`
  raises `OverflowException` here (inputs of the order of 10^12 contracts times 10^12 multiplier). A
  numerator that overflows 128 bits always implies a result that overflows a long, so both cases agree.
- Domain: a negative count or a price outside [$0, $1] is `ArgumentOutOfRangeException`; a negative
  multiplier cannot be constructed; a rounding unit below one micro-dollar is refused. `RoundUp` of a
  negative amount goes toward zero (the ceiling), as in the reference.
- Parity: `tools/vectors/generate_fees.py` calls the reference `kalshi_core.fees` as the oracle
  (commit 3d19d50): 1,819 fee cases (a 9 x 13 x 12 grid, 400 seeded random, 7 out-of-domain, 8 extremes
  of which 4 overflow), 171 round-up cases and 63 multiplier cases; plus the reference's own tests
  (Kalshi's worked example 3,639; the peak 17,500; the never-rounds-down cases) and an independent
  `decimal` computation. No divergence from the reference was found.
- Refactor: the generators now share `tools/vectors/common.py`; `fixedpoint.json` regenerates
  byte-identical.
- Verification: 2,365 tests pass, build with 0 warnings, `dotnet format` clean, coverage 98.5% lines and
  96.7% branches (`KalshiFees` and `FeeMultiplier` 100%). The reflection test that bans floating point
  now covers these types too.

### 3.3 Token-bucket rate limiter and coverage gate (2026-10-16): done

- `KalTerm.Core.RateLimiting.TokenBucket(decimal tokensPerSecond, decimal capacity, TimeProvider?)` with
  `AcquireAsync(decimal cost = 10, CancellationToken)`; `IDisposable` (it owns the FIFO `SemaphoreSlim`,
  held across a wait, as the reference holds its lock). Starts full, refills continuously, capped at
  capacity; cost outside (0, capacity] and rate or capacity of zero or less are refused.
- Exact integer arithmetic: tokens are held as micro-tokens times the timestamp frequency in 128-bit
  integers, so refill and wait times carry no drift. The reference uses floats: its sustained-throughput
  scenario ends at 8.999999999999982 where the exact answer is 9, which the port returns exactly.
  Rate, capacity and cost take at most six decimal places (otherwise `ArgumentException`).
- Added over the reference: a `CancellationToken` (a cancelled wait spends nothing). A clock that goes
  backwards is accepted as a new origin and adds nothing.
- **Bug found and fixed by the tests:** `Task.Delay` works in whole milliseconds and truncates. The first
  version waited 55 ms where 55.56 ms was needed, then retried with a sub-millisecond wait that truncated
  to zero; under a fake clock that spun forever (597 CPU-seconds before it was killed), and under the real
  clock it would have busy-waited up to a millisecond. Fix: every wait is rounded up to a whole
  millisecond. The surplus stays in the bucket, so the long-run rate is exact; a test pins that
  (18 acquires of 10 at 180 tokens/s end within 1 ms of one second). Another test pins that a
  10-microsecond deficit still waits a full millisecond.
- Test support: `AutoAdvanceTimeProvider` (a `TimeProvider` whose timers fire at once and advance the
  clock, like the reference's FakeTime). A manual mode lets a test fire each wait itself. The FIFO test
  first recorded order when each worker's continuation ran, which races on the thread pool even when the
  bucket grants in order; it now steps one waiter at a time and reads each completion. 25 consecutive
  full runs showed no failure or hang.
- Parity: `tools/vectors/generate_ratelimit.py` runs the reference bucket under its fake clock: the
  reference's five scenarios plus 40 seeded random schedules of acquires, idles and concurrent acquires,
  recording the clock after every step (45 scenarios), and validation cases. `ratelimit.py` imports
  pydantic via `config`; the generator stubs that one module to stay standard-library-only. The port
  agrees within 1 ms per acquire (the rounding above).
- **Coverage gate:** `tools/ci/check-coverage.ps1` fails CI if line coverage in any Cobertura report is
  under 90% or no report exists (coverlet.MTP's threshold option is documented as unsupported). Checked
  locally three ways: passes at 90, fails at 99.9, fails on an empty folder. New CI step after the tests.
- Verification: 2,447 tests pass, build with 0 warnings, `dotnet format` clean; coverage 98.4% lines
  and 96.4% branches (`TokenBucket` 97.4% and 90.0%).

### 3.4 Configuration and environments, `KalTerm.Kalshi` (2026-10-16): done

- New projects: `src/KalTerm.Kalshi` (I/O-side library, references `KalTerm.Core`), `tests/KalTerm.Kalshi.Tests`
  and `tests/KalTerm.TestSupport` (shared test helpers `VectorFile` and `AutoAdvanceTimeProvider`, moved with
  `git mv` out of `KalTerm.Core.Tests` so both test projects use one copy).
- `KalTerm.Kalshi.Configuration`: `KalshiEnvironment` (Demo = 0, so an unset value can never mean production),
  `KalshiEndpoints` (**the only file naming Kalshi hosts**, typed as `Uri`), `KalshiSettings` (immutable,
  validated; `Default`, `Create`, `FromVariables`, `FromEnvironment`) and `KalshiConfigurationException`.
  `KalshiRateLimits.ReadBucket/WriteBucket(settings)` build the `TokenBucket`s at budget x margin.
- Behaviour: `KALSHI_ENV` unset or empty means demo; only `demo` / `production` (case-insensitive) are valid,
  anything else is refused (never guessed). Budgets > 0, margin in (0, 1], and budget x margin must have at most
  six decimal places (the bucket's resolution). Variable names match case-insensitively, as on Windows. No `.env`
  file support, by design; trading-key settings wait for M6. No new NuGet packages.
- Enforced rules: a test scans every `.cs` under `src/` and fails if a Kalshi host name appears outside
  `KalshiEndpoints.cs` or `localhost` appears anywhere (CLAUDE.md non-negotiable). It has a negative control.
  Its first version matched `kalshi.co` inside the namespace `KalTerm.Kalshi.Configuration`; the pattern now
  matches host names only, and a regression test covers that exact case.
- Parity: `tools/vectors/generate_config.py` reads the reference `config.py` with `ast` (it needs pydantic to
  import) and extracts the URL maps, defaults, constraints and prefix; the tests assert the port equals them,
  so demo/production URLs are checked against the original. The budget table applies the reference formula.
- **Coverage gate corrected.** The first multi-project run failed the gate at 42.6%: each test project's report
  also lists the assemblies it merely references (`KalTerm.Core` was 19% in the Kalshi tests' report, 98% in its
  own). The gate was judging reports; it now merges line coverage per assembly across all reports (a line is
  covered if any report covers it) and judges each production assembly on the union; `KalTerm.TestSupport` is
  excluded as test infrastructure (visible in the output and a parameter). Verified under PowerShell 7 and 5.1:
  passes at 90, fails at 97 (`KalTerm.Kalshi` is 96.0%), fails on an empty folder, judges TestSupport when not
  excluded.
- Verification: 2,513 tests pass, build with 0 warnings, `dotnet format` clean. Merged coverage: `KalTerm.Core`
  98.4% (244 of 248 lines), `KalTerm.Kalshi` 96.0% (95 of 99).

### 3.5 Ed25519 signer (2026-10-16): done

- `KalTerm.Kalshi.Auth`: `Ed25519PrivateKey` (loads an unencrypted PKCS#8 PEM; wraps NSec; `IDisposable`),
  `KalshiSigner` (`Sign`, `Headers`, `FromSettings`; the three `KALSHI-ACCESS-*` headers) and `AuthException`.
  NSec.Cryptography 26.4.0 is the only new package (decided in S-3); all library-specific code sits in
  `Ed25519PrivateKey`, so the BouncyCastle fallback would touch one class.
- Message is `timestamp-ms + METHOD + path-without-query`, UTF-8, signed and base64-encoded, as in the reference.
  The default timestamp comes from the injected `TimeProvider`.
- Key handling: the key is imported **non-exportable** (a test proves `Export` of the private key throws) and is
  held in libsodium protected memory; `Dispose` wipes it and further use throws `ObjectDisposedException`;
  `ToString()` of both classes never shows key material; error messages never echo PEM text. Signing is
  thread-safe (2,000 parallel signatures agree). This closes the S-3 item "key-memory protection not tested",
  except for page-locking of the memory itself, which is NSec's own behaviour and is not tested here.
- Error parity: NSec only says "bad format" for an RSA key, so the algorithm OID is read from the PKCS#8
  header with the built-in `System.Formats.Asn1` and the key is named as the reference does: RSA, EC, Ed448,
  X25519, DSA, or "algorithm <oid>". Encrypted, public-key, wrongly labelled, truncated, mis-encoded and empty
  PEMs are all "is not an unencrypted PEM private key"; a missing or unreadable file is "cannot read private
  key file <path>". Whitespace, CRLF and text around the PEM block are accepted exactly as the reference does.
- Parity: `tools/vectors/generate_ed25519.py` loads the reference `auth.py` (pydantic-dependent `config` stubbed
  through the shared `common.stub_settings_module`) and records: 21 raw signatures (identical to the 21 S-3
  vectors, checked against the spike branch), 19 key-file texts with the reference's outcome, and 168 header sets
  from the reference's real `KalshiSigner.headers()` (4 methods x 7 paths x 2 timestamps x 3 keys, including
  queries, non-ASCII and empty paths). All match byte for byte. The generator needs Python's `cryptography`
  package (dev-only; the vectors are committed).
- Test support: `AutoAdvanceTimeProvider` now also answers `GetUtcNow` (start 1,700,000,000,000 ms).
- Deferred to M6: the trading signer, key-file permission/ACL checks on Windows, and `~` expansion in key paths.
- Evidence for S-4: this slice loads and runs `libsodium.dll` through NSec on GitHub's `windows-latest` runner (a
  clean machine, no SQL Server or dev tools beyond the SDK) as part of CI; S-4 still has to prove it on a clean
  server VM.
- Verification: 2,752 tests pass, build with 0 warnings, `dotnet format` clean; merged coverage `KalTerm.Core`
  98.4% (244 of 248 lines), `KalTerm.Kalshi` 94.6% (176 of 186).

### 3.6 Clock-skew estimator (2026-10-16): done

- `KalTerm.Core.Clock` (pure, no I/O): `ClockSample`, `ClockSkew` (record with `Status` and `Describe()`),
  `ClockSkewStatus` (Ok / Skewed / Uncertain), `ClockSkewEstimator.Estimate`, `HttpDate`, `ClockCheckException`.
  No new packages.
- Method as in the reference: each sample gives an interval `[sent - server - 1 s, received - server]` for
  `local - server` (the `Date` header is truncated to whole seconds); the samples' intervals are intersected;
  disagreeing samples fall back to the median of the midpoints with the widest half-width and are flagged
  low-confidence. `Skewed` only when even the nearest edge is beyond the threshold (default 2 s, our own choice);
  `Ok` when the whole interval is inside; otherwise `Uncertain`, never a guess.
- **Exact arithmetic**: instants are `DateTimeOffset` and offsets `TimeSpan` (100 ns ticks); the reference's floats
  carry about 0.2 microseconds of noise at today's epoch. Boundaries are exact (an edge equal to the threshold is
  not "beyond" it); tests pin this to the tick. A sample received before it was sent is refused (not in the
  reference; a monotonic clock cannot produce one).
- `ClockSample.FromHeaders(sent, received, date, age)` holds the rule the reference's REST client does inline:
  server time is `Date` plus `Age` (an `Age` that is not plain digits counts as zero; a huge one is a clear error),
  and a missing or unparseable `Date` is a `ClockCheckException`. The sampling loop (spacing, count) belongs to
  slice 2.7.
- `HttpDate` accepts exactly the three formats HTTP requires receivers to take (standard, RFC 850, asctime), case
  insensitive, GMT only, a real weekday name that is not cross-checked against the date, two-digit years pivoting at
  69 as in the reference. The reference's Python parser is far more lenient: it also accepts numeric zones, no zone,
  `UTC`, any word ending in a comma in place of a weekday, a four-digit year in RFC 850, and surrounding
  whitespace. Those are refused here, each recorded in the vectors as `csharp: error`.
- `Describe()` formats two decimals rounding half to even on the exact tick value. Python rounds exact binary ties
  by the binary value, so a handful of values (x.xx5) can print a different last digit; the vectors drop such
  cases and the unit test pins the exact-tie behaviour (0.125 -> 0.12).
- Parity: `tools/vectors/generate_clock.py` imports the reference `clock.py` and records 306 sample sets built like
  the reference test's `simulate` (offsets from -10 to +60 s, five round-trip times, three thresholds, one to eight
  samples, 40 inconsistent sets), 6 description cases, the no-samples error and 70 date strings. Estimates agree
  within 2 microseconds (float noise); status, consistency and description text match exactly. 2 cases were
  dropped as rounding ties.
- **Lesson from the vectors:** my first generator run had zero inconsistent cases. The test that checks the file's
  breadth failed, and the cause was that every inconsistent set had a half-width of exactly 0.525 s, a rounding tie
  my own filter removed. Round-trip times are now chosen to avoid ties, so the fallback path is covered.
- Verification: 3,208 tests pass, build with 0 warnings, `dotnet format` clean; merged coverage `KalTerm.Core`
  98.2% (333 of 339 lines), `KalTerm.Kalshi` 94.6% (176 of 186).

### 3.7a REST models (2026-10-16): done

- `KalTerm.Core.Money.Level` (new, 10^-6): strike levels such as `floor_strike`; the reference uses a float, which our
  rules forbid. Same scale and exactness as `Dollars`, but a different type so the two cannot be mixed up.
- `KalTerm.Kalshi.Models` (positional records; no new packages): `ExchangeStatus`, `ExchangeIndexStatus`, `Market`,
  `MveSelectedLeg`, `KalshiEvent` (not `Event`: CA1716, a VB keyword), `Series`, `Trade`, `MarketsPage`, `TradesPage`,
  `EventsPage`, `SeriesList`, `PriceLevel`, `OrderBook` (`FromResponse` for the `orderbook_fp` shape). Prices are
  `Dollars`, counts `Contracts`, strikes `Level`, the series fee multiplier `FeeMultiplier`; nothing passes through a
  floating-point type. Unknown fields are ignored (Kalshi adds fields over time).
- Source-generated System.Text.Json (`KalshiJsonContext`): snake_case names, nullable annotations respected,
  required constructor parameters respected. Fixed-point fields accept a JSON string (what Kalshi sends) or a number,
  read from the token text; a value finer than its scale, or not a number, is refused, never rounded.
  `KalshiJson.Parse` turns every JSON problem into a `KalshiResponseException` naming what was being read and, via
  `JsonException.Path`, which field.
- `StrictDateTimeOffsetConverter`: ISO 8601 with an explicit UTC offset only. The stock reader accepts a timestamp with
  no offset (taken as local time) and a bare date, so the same payload could mean different instants on different
  machines; those, a space for the `T`, epoch numbers, impossible dates, leap seconds and offsets beyond +-14 h are refused.
  Up to nine fractional digits are accepted and kept to 100 ns (Python keeps microseconds).
- Parity: `tools/vectors/generate_models.py` runs the reference's pydantic models over 229 payloads (every model; the
  reference tests' own payloads, the live multivariate shapes, and an adversarial corpus). The test compares the port's
  serialised model to pydantic's dump: identical key sets, prices and counts numerically, date-times at microsecond
  resolution. 189 cases agree field for field; 40 are recorded, deliberate differences where pydantic coerces (a bool for
  an int or a float, `"true"` for a bool, `"1"` for an int, a number or a string for a datetime) or accepts a value the
  port refuses as inexact. One reference quirk is left out of the corpus: `Decimal(0.15)` is the float's binary expansion.
  The port reads the number's text, `0.15` (a unit test pins it).
- **Real bugs found by the oracle (all fixed):** (1) with `required` + `init` properties the source-generated
  deserialiser overwrote defaults such as `= ""` with `null` when a field was absent, so the models became positional
  records, whose constructor defaults it honours. (2) System.Text.Json accepted offset-less timestamps (above).
  (3) Converter errors lost the field name; it is now added from `JsonException.Path`. (4) The reference silently accepts
  bool for int and float fields; the port refuses. Three of my own expectations about pydantic were wrong (it coerces a
  bool to an int, and a string to a float); the corpus tags were corrected from the oracle's actual behaviour.
- Verification: 3,522 tests pass, build with 0 warnings, `dotnet format` clean; merged coverage `KalTerm.Core` 98.3%
  (351 of 357 lines), `KalTerm.Kalshi` 96.3% (341 of 354).

### 3.7b REST client core (2026-10-16): done

- `KalTerm.Kalshi.Rest`: `KalshiRestClient` (`IDisposable`; `new KalshiRestClient(settings, options?, handler?)`),
  `KalshiRestClientOptions` (signer, bucket, `TimeProvider`, `ILogger`, `MaxRetries` 5, timeout 30 s), `KalshiQuery`
  (immutable query builder: nulls dropped, bools `true`/`false`, invariant numbers, escaped, a `DateTimeOffset` becomes
  epoch seconds, a repeated name replaces), `KalshiApiException` (status, whole body, message shortened to 200 characters),
  `ReadOnlyViolationException`. Endpoints in this slice: `ExchangeStatusAsync`, `MarketsPageAsync`, `IterateMarketsAsync`.
  New package: `Microsoft.Extensions.Logging.Abstractions` 10.0.13 (MIT, Microsoft; one dependency, also Microsoft's).
- **Read-only, twice over.** The client has no write methods (a reflection test checks the public surface), and a
  `DelegatingHandler` is the outermost layer: any non-GET (POST, PUT, DELETE, PATCH, HEAD, OPTIONS), through `SendAsync` or the
  synchronous `Send`, raises `ReadOnlyViolationException` before anything reaches the inner handler (the stub records zero
  requests). The write path stays a separate trading client with its own key (M6).
- **Pipeline per attempt:** acquire rate-limit tokens (every attempt, retries included); sign `GET` over the full path
  `/trade-api/v2/...` without the query, with a fresh timestamp each attempt; send; accept only HTTP 200 (201, 202 and 204 are
  errors, as in the reference); retry 429, 500, 502, 503, 504 and transport errors with delay
  `min(8 s, 0.5 s x 2^attempt) x (0.5 + 0.5 x jitter)`; a 4xx is never retried. The caller's cancellation is never retried;
  an HTTP timeout is. A connection lost while reading the body (it surfaces as an IO or request exception) is retried too,
  which the reference's `httpx.TransportError` covers implicitly.
- Added over the reference: `User-Agent: KalTerm/<version>` and `Accept: application/json`; a retry notice logged at
  Information level with the path and reason but never the query or headers; the production banner (Warning, "Kalshi client
  using PRODUCTION environment (read-only)") through `ILogger`, nothing when demo; a bad body is a
  `KalshiResponseException` naming the endpoint. The client owns and disposes the HTTP pipeline and any bucket it built itself;
  a bucket passed in stays the caller's.
- Test support: `StubHttpMessageHandler` in `KalTerm.TestSupport` (scripted responses and exceptions, records every request,
  fails loudly when the script runs out), the role the reference fills with `respx`. The Kalshi tests also gain `ListLogger`.
- Tests (65 new): the reference's REST tests ported (signing over the full path, cursor pagination, growing 429 backoff,
  give-up after the maximum, 4xx not retried, transport errors retried, non-GET blocked with zero requests sent, tokens
  acquired on each attempt, production banner and none for demo) plus: every retryable status, the jitter bounds and the 8 s
  cap, a fresh signature and timestamp on each retry, only-200, timeout retried but caller cancellation not, an IO failure
  mid-body, bucket and handler ownership, query escaping, and logs that never carry the query. The rate-limit test has a
  control (a single attempt leaves the bucket able to serve the next call without waiting). 20 consecutive runs of the
  Kalshi suite showed no failure or hang.
- Verification: 3,587 tests pass, build with 0 warnings, `dotnet format` clean; merged coverage `KalTerm.Core` 98.3%,
  `KalTerm.Kalshi` 95.1% (462 of 486 lines).

### 3.7c Remaining read endpoints, clock-skew sampling, live test (2026-10-16): done (live run pending)

- `KalshiRestClient` gains (still no portfolio, no writes): `IterateTradesAsync`, `EventsPageAsync` / `IterateEventsAsync`,
  `MultivariateEventsPageAsync` / `IterateMultivariateEventsAsync` (their own endpoint; the ordinary one is never touched),
  `MarketAsync`, `EventAsync`, `SeriesAsync` (each unwrapped from its envelope; a missing wrapper is a
  `KalshiResponseException` naming what was asked), `SeriesListAsync`, `OrderBookAsync(ticker, depth = 0)` (always sends
  `depth=`, as the reference does) and `ClockSkewAsync(samples = 5, spacing = 250 ms, threshold = 2 s)`. Tickers are
  URL-escaped, and the signature covers the escaped path actually sent.
- **The read-only guarantee is now an allow-list.** The first guard against write methods was a blocklist of write-sounding
  words in method names; it tripped over `OrderBookAsync` (an order *book* is a read). It is replaced by an exact allow-list of
  the 15 approved public methods: adding any public method, a write method above all, fails the test until someone extends
  the list on purpose.
- **Clock sampling.** Each sample is built by the 2.6 code from the response's `Date` and `Age`, read exactly as sent
  (`HttpHeaders.NonValidated`; the typed accessor would re-format the date), and timed around the attempt that succeeded, not
  around rate-limit waits or earlier retries (a test pins that `Sent` is the time of the second attempt after a 503 and its
  500 ms backoff). That is a deliberate improvement on the reference, which times the whole call; the interval it gives is
  still guaranteed to contain the true offset, and is tighter. Sleeps happen between samples only (4 samples, 3 sleeps).
- Core change: `GetResponseAsync` (internal) returns the body, the winning attempt's send/receive times and the raw `Date` and
  `Age`; `GetStringAsync` wraps it, so all existing behaviour is unchanged.
- Tests: the reference's trade, events, multivariate and clock-skew tests ported (a 4 s-ahead local clock is `Skewed` and
  contains the true offset; a good clock is `Ok`; the `Age` header rescues a cached reply, with the control that without it the
  same reply looks skewed; a missing or unparseable `Date` is a clear error), plus envelope unwrapping, escaping, depth, and
  validation. 15 consecutive runs of the Kalshi suite showed no failure or hang.
- **Live test** (`LiveKalshiTests`, seven tests): gated by `KALSHI_INTEGRATION=1` (the same switch as the reference); the target
  comes from `KALSHI_ENV` (production only when set explicitly) and the read-only key from `KALSHI_KEY_ID` /
  `KALSHI_PRIVATE_KEY_PATH`; without them it skips. It reads: the signed exchange status; 1,000 ordinary open markets (more than
  100, none multivariate, every price between $0 and $1); combo markets (all multivariate, with legs); the series list and a
  combo events page; the busiest market's order book (positive quantities, sane prices); an hour of recent trades (yes + no =
  $1.00 on each); and a clock reading (not `Skewed`). About a dozen GETs. It cannot place or change an order, and it never
  prints the key id, the key path or key material. Without the variable the seven tests show as skipped, in CI too.
- Verification: 3,619 tests (3,612 pass, 7 live skipped), build with 0 warnings, `dotnet format` clean; merged coverage
  `KalTerm.Core` 98.3%, `KalTerm.Kalshi` 95.5% (510 of 534 lines).

### 3.7d Live evidence against production (2026-10-16): passed

The owner ran the opt-in live tests from their own terminal, against the production API with a read-only Ed25519 key (no key
id, path or material was printed or recorded). All 7 tests passed in 4.9 s (about 13 GET requests); then the added check below
passed. What the run read:

- Exchange status: `exchange_active=True`, `trading_active=True`.
- 1,000 ordinary open markets parsed (every price between $0 and $1; none multivariate); 200 combo markets parsed, each with its
  legs (the first has 9); 15,187 series parsed, **every one with a fee schedule** (so every fee multiplier read as an exact
  fixed-point value); combo events parsed; the busiest market `KXTEMPMIAH-26OCT1619-T83.99` (24 h volume 8,342.24) had an order
  book of 7 yes and 10 no levels, top yes bid 0.09 for 63.00 contracts, all prices in range and quantities positive.
- 200 trades from the last hour (newest `2026-10-16T22:50:03.637060Z`); on every one yes price + no price = exactly $1.00.
- Clock: 0.12 s ahead of Kalshi (+-0.18 s, 5 samples): ok.
- **No real value was finer than its fixed-point scale and no real timestamp lacked a UTC offset**, so the two strictest design
  choices (refuse never round; explicit offsets only) rejected nothing real.
- **Signature acceptance (the 8th test, added at the owner's request).** Market data is public, so those reads cannot show whether
  the server accepts our signatures. This test reads an account endpoint (the balance) twice through the client's internal GET: unsigned,
  which the server **refused with HTTP 401**, and signed with the owner's key, which it **accepted with HTTP 200**. Refused
  then accepted proves the server checks credentials and accepts our Ed25519 signature. The test prints only status codes and the
  response's field names (`balance`, `balance_breakdown`, `balance_dollars`, `portfolio_value`, `updated_ts`), never a value;
  the field names are a head start for the M6 portfolio models. The public client surface still has no portfolio methods.
- Fix to the docs: the live-test command now runs the test assembly directly (`dotnet test --filter-class` would also hit the other
  test projects, which have no such class); CLAUDE.md shows the single-test form too.

### 3.8a WebSocket models (2026-10-16): done

Public-channel message models in `KalTerm.Kalshi.Models` (`WsMessages.cs`): `TickerMsg`, `TradeMsg`, `LifecycleMsg` (market and
multivariate lifecycle), `EventLifecycleMsg`, `OrderbookSnapshotMsg`, `OrderbookDeltaMsg`, the `WsMessage` envelope with a typed
`Payload()` (null for types without a model, `KalshiResponseException` for a known type whose payload is missing or invalid), and
the marker `IWsPayload`. Same rules as the REST models: exact fixed-point, unknown fields ignored, required fields enforced.

- **Vectors:** `tools/vectors/generate_ws_models.py` runs 414 payloads through the reference's pydantic `ws_models`
  (`tests/KalTerm.Kalshi.Tests/Vectors/ws_models.json`); 70 are recorded deliberate differences (a stricter port: JSON type
  coercion, finer-than-scale values, integers beyond 64 bits). All pass; the envelope case compares the envelope plus its payload.
- **Snapshot levels** are `[price, quantity]` pairs on the wire; a small converter (`PriceLevelPairListJsonConverter`) reads and
  writes that shape and reuses the exact readers. An absent side is empty; an explicit null is refused, as in the reference
  (a non-nullable constructor parameter defaulting to null, normalised in the body).
- **Left out on purpose:** the private channels (fill, user_order, market_position; M6) and the reference's `received_at` (the
  client stamps it from `TimeProvider` in 2.8b). The corpus has no private-type case because the port deliberately has no model
  for them; `WsMessageTests` pins that they yield no typed payload.
- **Verification:** 4,045 tests (4,037 pass, 8 live skipped), build 0 warnings, format clean, coverage `KalTerm.Core` 98.3%,
  `KalTerm.Kalshi` 96.2% (606 of 630 lines).

### 3.8b WebSocket client core (2026-10-16): done

`KalTerm.Kalshi.WebSocket`: `KalshiWebSocket` (connect, `SubscribeAsync`, `UnsubscribeAsync`, `UpdateSubscriptionAsync`, `Messages`,
`CloseAsync`), `KalshiWebSocketOptions`, `WsSubscription`, `KalshiWsException` (carries Kalshi's error code), and two internal seams,
`IWsConnector` / `IWsConnection`, with the one real implementation `ClientWebSocketConnector` over `ClientWebSocket`.

- **Ported from the reference `ws.py`:** headers signed fresh on each connect over the URL's path; increasing command ids with a
  `TaskCompletionSource` per command; error replies become exceptions carrying the code (`[6] Already subscribed`); a command times
  out on `TimeProvider`; the link closing fails waiting commands at once; a reply that carried `sid` and `seq` leaves a `control`
  marker in the stream so gap detection can count the sequence number it used; unparseable frames are logged and dropped; an
  unmatched `error` reply is passed on, an unmatched `ok` is only logged; channel names pass through unchecked.
- **Not ported:** reconnect and resubscribe (2.8c), overflow-by-reconnect and stats (2.8d), `request_snapshot` (M3). `WsMessage`
  gains `ReceivedAt` (never serialised), stamped from `TimeProvider`.
- **Stream:** a bounded `Channel<WsMessage>` (default 500,000). A lost link or a full queue delivers what is already queued and then
  throws `KalshiWsException` from `Messages`; a deliberate close ends it normally. A full queue closes the connection.
- **Public surface pinned:** an allow-list test, as for REST. The only commands are subscribe, unsubscribe and update_subscription.
- **Tests (40 new):** 32 over a scriptable in-memory connection and a manual-fire fake clock, 8 over a real socket against an
  in-process Kestrel server on a loopback port (headers arrive, text round-trips, fragments are joined, binary and oversize messages
  refused, a drop without a close handshake reads as a close, a refused upgrade fails, and the whole client end to end). The test
  project alone references `Microsoft.AspNetCore.App`; no product project does. Repeated 10 times: no flakes.
- **Bug found by the real-socket tests:** sending after dispose threw `ObjectDisposedException` from the send lock instead of
  `KalshiWsException`; fixed.
- **Verification:** 4,085 tests (4,077 pass, 8 live skipped), build 0 warnings, format clean, coverage `KalTerm.Core` 98.3%,
  `KalTerm.Kalshi` 97.0% (835 of 861 lines).

### 3.8c WebSocket reconnect and resubscribe (2026-10-16): done

The client now replaces a lost connection (`AutoReconnect`, default on). A supervisor loop reads until the link ends, then
reopens it with backoff `min(max, base * 2^attempt) * (0.5 + 0.5 * jitter)` (defaults 0.5 s and 30 s, unlimited attempts), signing
the new upgrade fresh, re-sends every active subscription in creation order, and puts a `reconnected` message
(`ReconnectedMsg`: `resubscribed`, `failed`, `reason`, `dropped`) in the stream. Data that arrives while the subscriptions are being
restored is held and released after that message; if the link drops again mid-restore the held data is discarded and the whole
round repeats. A subscription Kalshi refuses with an error code is reported in `failed` and dropped; trouble with the link itself
keeps every subscription for the next round. Commands during a reconnect fail fast ("not connected"). `CloseAsync` during backoff
ends the stream cleanly; running out of attempts delivers what is queued and then throws "reconnect attempts exhausted".

- **Scope decision (the owner accepted):** sequence-gap detection is not in the reference's `ws.py` but in the order book builder
  (`orderbook.py`), so it belongs to M3. 2.8b's `control` markers are its input.
- **Differences from the reference, found while porting (all fixes):**
  1. *State follows the reply.* The reference is single-threaded; here a reply wakes the caller on another thread. A link that
     dropped right after a reply could be restored from a stale subscription list (a lost subscription, or a stale market set). Each
     command now applies its state change (store the subscription, update the market set, remove it) on the reader as the reply is
     received, before the caller resumes.
  2. *A failed restore closes the connection.* In the reference a resubscribe that times out leaves a live link with nothing restored
     until the server happens to drop it. Here a failed or overflowed restore closes the link so the supervisor retries.
  3. *Retry keeps every subscription.* After a mid-restore failure all the old subscriptions are put back under their old sids (the
     reference merges old and new, which can collide when new sids equal old ones).
  4. The supervisor catches an unexpected error, logs it and ends the stream with its message instead of dying silently.
- **Not here:** overflow-triggered reconnect, wait-to-drain and stats (2.8d). A full queue still ends the stream with an error, and
  `dropped` is always 0 for now. Held data that cannot fit the queue when released is treated as an overflow, never lost silently.
- **Tests (+20, 60 in the namespace):** the reference's eight reconnect tests ported to the scripted fake (which now serves a sequence of
  connections), jitter and cap checks, typed event, ordered release of held data, fast failure while reconnecting, updated market set,
  unexpected errors, flush overflow, a timed-out resubscribe; plus two real-socket tests (the server closes and the client really
  reconnects and resubscribes; a refused reconnect upgrade is retried). Two tests were racy (a consumer that keeps pace never overflows
  a queue; the server aborting before the client saw the upgrade) and were made deterministic; 80 consecutive runs of the namespace pass.
- **Verification:** 4,105 tests (4,097 pass, 8 live skipped), build 0 warnings, format clean, coverage `KalTerm.Core` 98.3%,
  `KalTerm.Kalshi` 96.7% (1,003 of 1,037 lines).

### 3.8d WebSocket overflow, stats and live tests (2026-10-17): done

A consumer that falls `MaxQueueMessages` behind makes the client close the connection and count the overflow; what is already
queued is still delivered in order. With `AutoReconnect` (the default) it then waits until the consumer has drained the queue to half
the limit (polling every 50 ms, so no reconnect storm), reconnects, and the `reconnected` message carries `reason: "overflow"` and
`dropped: N`, the messages received but discarded since the last `reconnected` message, so the consumer knows to backfill. Without
`AutoReconnect` the stream ends with "receive queue overflow" after the backlog. `KalshiWebSocket.Stats` returns
`WsStats(QueueDepth, QueueLimit, HighWater, Overflows, Dropped)`.

- **Two hardening changes beyond the reference** (the owner approved): the `reconnected` message is written with a waiting write, so
  it can never be dropped when the queue is full (the reference's queue is unbounded, so its put never fails); and the data held
  while the subscriptions are restored is bounded by the queue limit (beyond it counts as an overflow and the cycle repeats). Held data
  that does not fit when released counts toward `dropped` and triggers an overflow reconnect (2.8c ended the stream with an error).
- **Count semantics:** `dropped` is reported once, in total. If a restore finishes while the reader is overflowing, the earlier
  `reconnected` message may carry the count and the later `overflow` one say 0. A test originally assumed otherwise and failed once in
  60 runs; it now asserts the total.
- **Internal hook:** `DropConnectionAsync` (internal, not on the pinned public surface) cuts the link from our side, for the live
  reconnect test.
- **Live tests (opt-in, production, read-only key, standing permission; `LiveKalshiWebSocketTests`), run 2026-10-17, 4 of 4 passed in 24 s.** One
  signed WebSocket upgrade per test, `subscribe` commands only, plus one REST markets page. Public data read:
  - `trade` for 8 s: **2,599 trades**, all parsed, yes + no = $1.00 on every one, one contiguous sequence (for example
    KXNFLGAME-26OCT18NONYG-NYG yes 0.51 / no 0.49 x 18.95; KXBTC15M-26OCT171415-15 yes 0.918 / no 0.082).
  - `multivariate_market_lifecycle` for 6 s: **4,222 messages**, every one parsed through the typed models: event_lifecycle 1,005,
    created 1,369, close_date_updated 617, settled 612, determined 619.
  - Order book for the busiest market (KXLALIGATOTAL-26OCT17RBBBAR-8) for 8 s: a snapshot first (28 yes / 37 no levels), then
    **265 deltas**, all parsing exactly, prices in range, contiguous sequence (first delta: no -10.00 at 0.49).
  - Link cut from our side while subscribed to `trade`: 18 trades drained, then `reconnected` (connection_lost, the old sid reported),
    then trades resumed on the new connection.
  - Nothing about the account was read; no value other than public market data was printed.
- **Refactor:** the live tests' shared pieces (skip reason, credentials, summary file) moved to `LiveSupport`; the WebSocket tests'
  shared scripting and waits to `WsTestKit`.
- **Tests (+19 net; 80 in the namespace plus 4 live):** the reference's overflow tests ported (ordered overflow reconnect, error without
  auto-reconnect, no overflow when keeping up, reconnect waits for the drain), plus close-while-draining, the never-lost
  `reconnected` message, held data beyond the limit, stats, and the drop hook. 100 consecutive runs of the namespace pass.
- **Verification:** 4,117 tests (4,105 pass, 12 live skipped), build 0 warnings, format clean, coverage `KalTerm.Core` 98.3%,
  `KalTerm.Kalshi` 96.7% (1,041 of 1,076 lines).
- **M2 status:** every slice 2.1 to 2.8d is built and verified. The order book builder and its gap detection are M3 (the plan's
  decision); the private channels are M6.

## 4. M2 Core summary (2026-10-17): done

Gate (PLAN §6): parity tests green. Met. Slices 2.1 to 2.8d are built, each on its own branch and PR, each with the coverage gate
and `dotnet format` clean. M2 never touched SQL.

**Final numbers (after 2.8d):** 4,117 tests (4,105 pass, 12 opt-in live tests skipped by default and in CI), build 0 warnings,
coverage `KalTerm.Core` 98.3% (351 of 357 lines), `KalTerm.Kalshi` 96.7% (1,041 of 1,076), gate 90% per assembly.

**What exists**
- `KalTerm.Core` (no I/O): fixed-point `Dollars` / `Contracts` / `Level` / `FeeMultiplier`, fees, token-bucket rate limiter, clock-skew maths.
- `KalTerm.Kalshi`: configuration (the only place Kalshi hosts appear, enforced by a scan test), Ed25519 signer, GET-only REST client with
  retries and pagination, the models for REST and the public WebSocket channels, and the WebSocket client (signed connect,
  subscribe, reconnect with resubscribe, bounded stream with overflow handling and stats).
- Differential tests against the Python reference: golden vectors for fixed-point, fees, rate limiter, configuration, Ed25519 signing,
  clock skew, REST models (about 400 cases) and WebSocket models (414 cases); every place the port is deliberately stricter is recorded in
  the data (`port: "error"` plus a reason), not hidden in a test.
- Live evidence against production with a read-only key (BUILD-LOG 3.7d, 3.8d): REST models, strict parsing, signature acceptance
  (401 unsigned, 200 signed), and WebSocket trade, lifecycle and order book streams including a real reconnect.

**Lessons that recurred (apply them in M3 onwards)**
1. *Exact, never rounded.* Prices and counts are read from the text of the JSON token; a value finer than its scale is refused. Real
   production data (15,187 series, thousands of markets and messages) never tripped it.
2. *Pin a client's public surface with an allow-list test,* not a blocklist of write-sounding words. Both clients have one.
3. *Port the behaviour, then look for what the original got for free.* The reference runs on one thread; a threaded port needs state
   changes applied as the reply is received, bounded buffers, and a restore that closes a link it could not finish (BUILD-LOG 3.8c, 3.8d).
4. *Real sockets find what fakes cannot* (a send after dispose threw the wrong exception); fakes find what real sockets cannot
   reproduce on demand (timeouts, overflow, mid-restore drops). Both are in the suite.
5. *A flaky test is a bug.* Racy tests were made deterministic and the namespace re-run 60 to 100 times, rather than retried.
6. *Scripted edits and large heredocs are fragile in this environment;* write the script to a file and run it.

**Outstanding before M3:** S-1d (power cuts on the OptiPlex 7080, on order), and the unstarted spikes S-2 (time-series layout) and S-4
(unattended install, clean VM). The order book builder and its gap detection (M3) and the private channels (M6) are deliberately not in M2.

## 5. M3 Vertical slice

### 5.1 Slice 3.1: `KalTerm.Data` (2026-10-18)

**Built.** `src/KalTerm.Data` (DbUp migrations embedded as resources, `Microsoft.Data.SqlClient`) and `tests/KalTerm.Data.Tests`.
- `DatabaseConnectionOptions`: server (default `lpc:.`, shared memory), database, Windows authentication only, never a password. `DataSource` opens connections.
- `Migrations/0001_schema_v1.sql`: tables `market`, `ticker`, `trade`, `ingest_gap`, `ingest_state`. Money is `bigint` (`_e6` dollars, `_e2` contracts).
  `ticker` and `trade` are on the daily partition scheme `ps_day` with an ordered clustered columnstore on `(market_id, ts)` and `LOCK_ESCALATION = AUTO`,
  no foreign keys, no unique constraints (de-duplication is the writer's job, slice 3.2). `trade.taker_side` is 0 unknown, 1 yes, 2 no.
- `SchemaMigrator`: DbUp, one transaction per script, forward only; failures raise `SchemaMigrationException`; log goes to an `ILogger` through source-generated messages.
- `DatabaseProvisioner` (static): creates the database, then sets `DELAYED_DURABILITY = DISABLED`, `PAGE_VERIFY = CHECKSUM`, `RECOVERY SIMPLE`
  and the data file's hard `MAXSIZE` (required, rounded up to a whole MB, no default). Running again changes nothing; drift is corrected and reported.
- `PartitionManager` (static): adds day boundaries ahead of need (`SPLIT RANGE` of the empty last partition, so no data moves) and drops a day with
  `TRUNCATE ... WITH (PARTITIONS (n))` on both tables; it refuses a day that has no boundary of its own.

**Decisions made while building (flag if you disagree).**
- `RECOVERY SIMPLE`, as in the spikes. With the default FULL model and no log backups the log would grow without bound against a 500 GB budget. M8 designs backups and may revisit.
- Provisioner and partition manager are static classes (analyzer CA1822: they hold no state). Tests use LocalDB; nothing needs a seam yet.
- No strike/rules/title text columns on `market` yet; added by a later migration when something reads them.

**Test environment.** `KalTerm.TestSupport.LocalDbServer` gives each test class its own uniquely named database on `(localdb)\MSSQLLocalDB`, dropped afterwards.
`KALTERM_TEST_SQL_SERVER` points the tests at another server (for example `lpc:.\KALTERM` on the target server). When SQL Server is unreachable the tests **skip** on a
development machine and **fail** on CI (`GITHUB_ACTIONS` or `CI` set), so CI cannot pass by skipping. This workstation has no LocalDB runtime, so locally the 35
database tests skip and `KalTerm.Data` shows low coverage in the local gate; CI is where it is judged.

**Verified on `kaldb`.** The test assembly was published self-contained with `-p:NoCoverage=true` (coverlet.MTP is a private asset and is not in the publish's
`deps.json`, so the host cannot load it), copied to `C:\KalTermSpikes\m3\data-tests-nocov`, and run as SYSTEM (non-interactively) against the real SQL Server 2025 instance
over shared memory: **35 of 35 passed**, including the drift test, the ordered-columnstore and partition checks, and the truncate test. Afterwards `kaldb` held
only the four system databases.

**Problems and fixes.**
- `Microsoft.Data.SqlClient` 7.1.1 pulls in `System.Security.Cryptography.Pkcs` 9.0.18, which has high-severity advisories; warnings are errors, so the build failed. Fixed with
  central transitive pinning (`CentralPackageTransitivePinningEnabled`) and a pinned 10.0.13.
- The coverage gate judged DbUp's assemblies (they ship symbols, so coverlet instruments them) at 0%. The gate now judges only assemblies matching `-Include` (default `KalTerm.*`).
- `COUNT_BIG` returns `bigint`: a test that read it as `int` failed on the real server.
- A `-r win-x64` publish rewrites the shared `project.assets.json`; a later `--no-restore` build then ran without coverlet (handshake failure). Re-run `dotnet restore` after a publish.
- xUnit analyzer xUnit1051 requires passing `TestContext.Current.CancellationToken`; CA1816 requires sealing a disposable test class.

### 5.2 Slice 3.2: the write path (2026-10-18)

**Built** (`src/KalTerm.Data/Writing`, tests in `tests/KalTerm.Data.Tests/Writing`).
- `BulkWriter<T>`: producers call `TryWrite` (never blocks); one background loop writes a batch when it holds 5,000 rows or 1 s after its first row, whichever is first.
  Each batch is one `SqlBulkCopy` with an internal transaction and `BatchSize = 0`, so a failure leaves nothing behind and a retry cannot duplicate rows
  (tested: a batch with one over-long `trade_id` leaves zero rows).
- Retry (`SqlRetryPolicy`): 1205, 4891, timeouts, anything SqlClient calls transient, connection-level errors (severity 20+, -1, 2, 53) and the errors of a
  restarting or recovering server (4060, 922, 945, 18456). Capped exponential backoff (1 s to 30 s, up to 20% jitter) on `TimeProvider`; retries never stop while the
  buffer absorbs the stall. Unhealthy after 5 consecutive failed attempts, healthy again after a success. Anything else (a refused value, a missing table) is a
  "poison" batch: dropped, counted, logged, and the loop carries on. A wrong database name therefore retries forever, visibly, instead of dropping data silently.
- Overflow: at 500,000 buffered rows new rows are dropped and counted, and the earliest and latest dropped timestamps are kept; `TakeDroppedRange()` returns and resets
  them so slice 3.4 can write an `ingest_gap` row. A poison batch or rows abandoned at shutdown are recorded the same way.
- Shutdown: `DisposeAsync` stops intake, writes what is buffered for at most 10 s, abandons a stuck write, and counts what was left as dropped.
- `Writers.Ticker` / `Writers.Trade` with `TickerRow` / `TradeRow` (all `long` fixed-point; `null` where a message omitted a field); `TakerSide` is the stored tinyint.

**Deviation from the plan.** The sink fills a reused `DataTable` per batch rather than streaming through a custom `DbDataReader`. Simpler and robust; the measurement
below shows the writer far ahead of the load, so it stays.

**Verified on `kaldb`** (self-contained publish as in 5.1): 89 of 89 tests pass over shared memory, and no test database is left behind.

**Throughput (opt-in test `WriterThroughputTests`, `KALTERM_THROUGHPUT=1`, real SQL Server 2025 on `kaldb`, 5,000 distinct markets, default options).**

| Load | Result |
|---|---|
| Paced 20,000 rows/s for 30 s (600,000 rows) | all written, 0 dropped, buffer high-water 3,001 to 5,001 rows, last batch 34 ms, slowest batch 194 to 262 ms (three runs) |
| Burst: 1,000,000 rows offered in 0.1 s | 510,000 written, 490,000 dropped (buffer 500,000 plus the batch in flight), drained in about 3.3 s: at least 150,000 rows/s |

Reading: the sink can write about 150,000 ticker rows/s, 7x the paced load, so the 500,000-row buffer is about 3 s of full-speed writing and about 25 s of arrivals at
20,000 rows/s (31 s at 16,000). After a 30 s stall the backlog drains in a few seconds. The default stays at 500,000; revisit in 3.8 against the real feed rate.
Limits: 30 s runs on an empty table with one writer; compression, log growth and `CHECKPOINT` effects over hours and with the trade writer running alongside are for 3.8.

**Problems and fixes.**
- Error 4060 (cannot open database) was first classified as poison. The host must survive a server restart or a database still recovering, so the restart errors retry.
- `Microsoft.Data.SqlClient` retries a failed connect once after 10 s by default, which hid the writer's own backoff and made each failed attempt take 10 s;
  `ConnectRetryCount = 0` is now in the connection string (tested).
- **CI (LocalDB) found two things `kaldb` did not.** (1) Right after `CREATE DATABASE` the provisioner's settings query once returned a NULL column
  (`SqlNullValueException`), apparently while the new database was not yet fully online; it now waits, bounded at 60 s, until the database is `ONLINE` and every
  setting is readable. (2) SQL Server refuses `SPLIT RANGE` on a populated partition of a columnstore table, and the last partition holds every day after the newest
  boundary, so once rows exist with no boundary above them it can never be split. **Rule for the host (slice 3.4): add boundaries (today and some days ahead) at
  start-up before the writers start, and keep adding ahead of the clock.** `PartitionManager` says so; the writer database tests add their boundaries first.
- **The "null column" race was `AUTO_CLOSE`.** On LocalDB (and Express) databases are created with `AUTO_CLOSE ON`; an idle database closes and its settings read back as NULL
  until something opens it, so waiting never helped (one CI test timed out after the full 60 s). The provisioner now pins `AUTO_CLOSE OFF` (the right production setting anyway)
  before it reads anything; the `ALTER` also wakes a closed database. The 60 s wait stays as a bound for a database that is genuinely recovering.
- The harness blocks `Remove-Item` even inside a script that runs on the server; use `[System.IO.File]::Delete` there.
- Fake clock tests: `AutoAdvanceTimeProvider` in manual mode leaves cancelled timers in its queue, so tests pump until their condition holds instead of firing a counted number of timers; 25 consecutive runs of the unit suite passed.

**Coverage gate raised (2026-10-18, owner's decision).** 95% line coverage for `KalTerm.Core` and `KalTerm.Data`, 90% for the rest (PLAN.md decision 9;
`check-coverage.ps1 -AssemblyMinimum`). `KalTerm.Data` was at 94.1% on CI, so the DbUp log adapter moved to its own internal class (`LoggerUpgradeLog`) and is tested at every
level, which also covers the generated logger methods. What remains uncovered is deliberate: the provisioner's wait for a database that is not yet online (it needs a database stuck offline
for up to 60 s), one loop-exit line in the writer, and the null-return branch of the settings read.

### 5.3 Slice 3.3: market discovery (2026-10-18)

**Built.**
- `KalTerm.Data.Markets`: `MarketStore` upserts a batch with one round trip (bulk copy into a temporary table, then one `MERGE ... HOLDLOCK` that returns each ticker's id and
  whether it was inserted), in one transaction; `market_id` never changes, `first_seen_at` is set once, `last_seen_at` moves. A ticker repeated in a batch counts once (last wins);
  sub-titles are cut to the column's 256 characters rather than refused. `MarkUnlistedAsync` sets `status = 'unlisted'` on markets that are still tradable (`active`, `open`) and were
  not seen in the pass. `MarketIdMap` is the in-memory ticker-to-id lookup the writers will use. `IMarketStore` lets the logic be tested without a database.
- `KalshiRestClient.IterateOpenMarketsAsync`: `status=open`, `mve_filter=exclude`, 1,000 per page (on the client's approved-method list, tested).
- `KalTerm.Ingest` (new project): `MarketDiscovery` runs one pass (list, skip and count any combo that slips through, upsert page by page, then unlist), `MarketMapper`, and
  `RefreshSchedule`, the owner's cadence rule as a pure function: every 5 minutes on the clock, but every 30 s from :55:00 to :04:59 of each UTC hour (30 ticks an hour). The scheduler
  loop that uses it is slice 3.4.
- Safety rules, each tested: a listing that fails midway unlists nothing (what was stored stays); an empty listing unlists nothing (more likely a glitch than an exchange with no
  open markets); unlisting only touches markets last seen before this pass started; an unlisted market that is listed again becomes tradable again.
- `DatabaseFixture` and `Requirement` moved from `KalTerm.Data.Tests` to `KalTerm.TestSupport` (with xunit assert/core references and a reference to `KalTerm.Data`) so
  `KalTerm.Ingest.Tests` shares them.

**Verified on `kaldb`.** Data tests 107 of 107 and Ingest tests 46 of 46 over shared memory; no test database left behind.

**Live sizing (production, read-only key, GET only, public data; 2026-10-18).** `LiveDiscoveryTests` listed the open non-combo markets once and ran the real discovery over them
against an in-memory store (no database):
- **121,880 open markets** (0 combos slipped through the filter), all with status `active`, in **12,847 events**; about 122 page requests, **32.0 s** to list them; a second pass (24.3 s)
  saw 121,898, so about 18 markets appeared in the minute between.
- Longest ticker 48 characters, event ticker 41, sub-title 92 (columns allow 128, 128, 256); every market has a close time; the mapping did not throw on any of them.

**Database half of a pass at that size** (`DiscoveryScaleTests`, 122,000 synthetic markets, opt-in `KALTERM_THROUGHPUT=1`, `kaldb`): first insert pass 6.4 s, an update pass with nothing
changed 5.8 s, a pass with 100 gone and 100 new 5.8 s.

**What this meant for the schedule, and the decision (owner, 2026-10-18: option 2).** One full pass costs about 18 to 32 s of Kalshi listing plus about 6 s of database work and about 122
requests, which is one 30-second tick long, so a full pass every 30 s in the windows would run back to back. Decision: **full passes only on the 5-minute boundaries, incremental passes
on the other 30-second ticks** of the windows (12 full and 18 incremental per hour; `RefreshSchedule.KindOf`).
- `KalshiRestClient.IterateMarketsUpdatedSinceAsync(since)`: `min_updated_ts` + `mve_filter=exclude`, 1,000 per page. Verified live (probe test, GET only): accepted; adding `status=open`
  is refused with HTTP 400 (as the KalshiTerm2 reference found), so the feed returns markets in whatever status they now have. Statuses seen: `active`, `closed`, `determined`, `finalized`,
  `inactive`, `initialized`. A 2-minute look-back near the top of an hour returned 2,811 markets (mostly the hourly settlements) in 3 pages and 1.0 s.
- `MarketDiscovery.RunAsync(PassKind.Incremental)`: looks back from the last completed pass by a 2-minute overlap; stores the markets we already know (with their true new status, which is how a
  settlement is recorded) plus unknown markets that are `active`; **ignores** unknown markets that are not tradable (settled history, not yet open) so the catalog stays "open markets" (D8);
  reports known markets that came back non-tradable as `Removed` (idempotent across the overlap); never unlists; before any completed pass it runs as a full pass; a failed pass leaves the bookmark.
- Live end-to-end check (production, read-only key, in-memory store): full pass 121,749 markets in 17.7 s; then, 30 s apart, incremental passes of **0.1 s each** storing 71 and 108 markets
  (22 and 108 known, 49 new, 14 no longer tradable, 61 and 74 ignored). That is mid-hour (03:09 UTC); the top-of-hour windows carry more settlements, about 2,800 in 2 minutes, still seconds.
- Not measured: how late Kalshi's `updated` index runs behind a change; the 2-minute overlap is the reference's measured safety margin and is a constructor argument.

**Problems and fixes.**
- A `Dollars` helper in the test kit shadowed the `Dollars` type in another test file; removed.
- `MarketDiscovery` now checks the cancellation token itself on every market instead of trusting the source to.
- Shell heredocs with embedded quotes failed again; files were written with the editor tool.

### 5.4 Slice 3.4a: the stream ingest pipeline (2026-10-18)

Slice 3.4 is split (owner, 2026-10-18): **3.4a** is the pipeline with no network and no host (this entry); **3.4b** is the host (scheduler, partitions, start-up, shutdown, `ingest_state`).

**Built.**
- `KalTerm.Data.Writing.IRowWriter<T>` (`TryWrite`, `TakeDroppedRange`), implemented by `BulkWriter<T>`, so the logic in front of a writer is tested without a database.
- `KalTerm.Data.Gaps`: `GapRecord`, `GapKinds` (`ws_reconnected`, `ws_lost`, `writer_dropped`), `IGapLog` and `GapStore` (plain inserts into `ingest_gap`, one transaction; every record is checked before anything is written).
- `KalTerm.Ingest.StreamMapper`: ticker and trade messages to `TickerRow` / `TradeRow` in fixed-point. The row time is the server's `ts_ms` when it is positive and inside the calendar, else the receive time.
- `KalTerm.Ingest.StreamIngestor.RunAsync(messages)`: routes rows to the writers through the `MarketIdMap`; counts (and does not store) data for a market not in the catalog, and messages whose payload cannot be read;
  ignores `control` markers and other types; records a `ws_reconnected` gap from the last data message to the `reconnected` message; records `ws_lost` and rethrows when the stream ends with a `KalshiWsException`;
  collects each writer's dropped range every second (`writer_dropped`). Gaps wait in a bounded list (1,000) and are retried while the database is down; the oldest are discarded and counted past the bound.
  A final flush (bounded at 10 s) runs however the stream ends, including cancellation.

**Left out of the plan's list, deliberately.** No gap row for an unknown-ticker burst: a market that appears before discovery sees it is a small, known loss, so it is counted and logged at 1, 10, 100, ... occurrences.

**Verified.** Local: format clean, build without warnings, 4,330 tests, 0 failed (65 skipped, the database tests: no LocalDB on this workstation). Gate (local, database tests skipped): Core 98.3%, Ingest 97.4%, Kalshi 96.8%; Data is judged on CI.
On `kaldb` (shared memory, as SYSTEM, `tools/kaldb/run-db-tests.ps1`): Data 111 of 111 run passed, Ingest 96 of 96 run passed (the skips are the opt-in throughput and live tests).

**Problems and fixes.**
- **`kaldb` found a silent truncation.** A gap `kind` of 33 characters was cut to the column's 32 by the SQL parameter, not refused. A misspelled kind would have been stored under the wrong name. `GapStore` now refuses a missing or over-long kind before storing anything (tested with and without a database).
- The editor tool writes LF; the repo's `.editorconfig` wants CRLF, so `dotnet format` flags every new file. Run `dotnet format KalTerm.sln` after writing files.
- xUnit1051 rejects a test helper that takes a `CancellationToken` unless every call passes `TestContext.Current.CancellationToken`; the stream helper takes none and the cancellation test throws from inside the stream.
- `AutoAdvanceTimeProvider` with auto-firing timers cancels a `CancellationTokenSource(timeout, clock)` at once; ingest tests build it with `autoFire: false`.
- Syncing the published tests to `kaldb` by file size missed 8 files whose size was unchanged; the script now compares SHA-256 and refuses to run unless every file matches.

**Local model trial (PLAN 6.4), use 1.** The full test and coverage log (42,278 characters, about 10k tokens) went through the helper: 16 s, about 350 tokens back. Its counts (4,330 / 1 failed / 4,264 / 65) and the failing test and reason matched the raw log, and it also reported the missing LocalDB. The failure was read from the raw log and the coverage verdict came from `check-coverage.ps1`, as the rules require.
Not a use: reading `KalshiWebSocket.cs` whole and a directory listing that printed 89 KB were wasted context; a narrower listing (or the helper) would have done.

**Local model trial: ended (owner, 2026-10-18).** One real use (above); accurate, but no saving over `grep`/`tail` on structured output, plus 16 s of latency (about 30 s cold) and about 12 GB of VRAM while loaded. The real token cost this slice was whole-file reads and noisy command output, which a local model does not fix. Discontinued and removed: Ollama, the 9 GB model, the helper, and its PATH and Startup entries. The untested case, long unstructured logs (the slice 3.8 soak), is not pursued unless the owner raises it.

### 5.5 Slice 3.4b: the host (2026-10-19)

**Built.**
- `KalTerm.Data`: `IngestStateStore` (`IIngestState`, one `MERGE`; a value over 400 characters is refused, not cut), `PartitionMaintenance` (`IPartitionMaintenance`: boundaries for today through N days ahead), gap kinds `ingest_down` and `ws_down`.
- `KalTerm.Ingest.IngestRunner` (no hosting dependency). **Start-up:** add partition boundaries (today + 7 days), load the market ids (a first full discovery pass if there are none), log the time since the last run as an `ingest_down` gap, mark the run `running`, start the writers.
  A start-up failure is thrown and releases the writers. **Running:** three loops. The stream loop opens a stream, runs `StreamIngestor`, and replaces the stream whenever it ends or fails (waits 1 s, doubling to 60 s; the wait resets only once a stream has delivered data);
  the span with no stream becomes a `ws_down` gap. The discovery loop follows `RefreshSchedule`; a failed pass is counted and logged and the next tick tries again. The maintenance loop writes a heartbeat to `ingest_state` every 15 s, tops up partitions hourly and logs one status line a minute.
  Only recoverable errors (WebSocket, HTTP, socket, IO, timeout) are retried; anything else stops the run, after an orderly shutdown, and surfaces. **Stopping:** loops stop, the writers drain (10 s), the last gaps (including abandoned rows) are stored, the run is marked `stopped`; none of that can throw.
  `ingest_state` holds one row, `run`: its time is the last heartbeat while running and the clean-stop time otherwise, so the next start knows how long it was down and whether the stop was clean (a crash loses at most one heartbeat interval at the start of the gap).
- `StreamIngestor` can run for a later stream (counters and waiting gaps carry over) and gained `NoteGap` and `FlushAsync`. `IStreamSource` / `KalshiStreamSource` subscribe `ticker` and `trade` with no market list (every market).
- `KalTerm.Ingest.Hosting`: `IngestHostOptions` (`Ingest:Server`, `Database`, `ProvisionMaxSizeGb`, `PartitionDaysAhead`), `DatabasePreparer` (a missing database is an error unless `ProvisionMaxSizeGb` is set, then it is created with that hard `MAXSIZE`; migrations run every start), `IngestComposition` (the real storage and Kalshi feed), `IngestWorker` (a `BackgroundService`: a failure logs, sets exit code 1 and stops the host).
- `src/KalTerm.Ingest.Host`: the executable, 20 lines on the Generic Host. Settings: `appsettings.json` next to the exe, `KALTERM_Ingest__*` variables, command line; Kalshi settings from `KALSHI_*` (demo unless `KALSHI_ENV=production`, with the production banner).
  Run: `dotnet run --project src/KalTerm.Ingest.Host -c Release`.

**Decision recorded.** No coverage-gate exclusion for the Host assembly (the plan expected one): no test project loads it, so it never appears in the coverage reports.

**Verified.** Local: format clean, no build warnings, 4,379 tests, 0 failed (74 skipped: the database tests, no LocalDB on this workstation; local gate Data 45%, Ingest 89.0% for that reason, judged on CI).
On `kaldb` (`tools/kaldb/run-db-tests.ps1`): Data 118 of 118 run passed, Ingest 137 of 137 run passed. The end-to-end test provisions a database of its own (1 GB), runs the whole composition with a scripted feed, checks the ticker and trade rows, the markets, the `stopped` state, and then that a second run logs one `ingest_down` gap.
The runner tests use the real clock with tiny intervals and wait for outcomes; 5 consecutive runs passed.
Smoke run of the executable with no database: it logged "database 'KalTerm' does not exist ... set Ingest:ProvisionMaxSizeGb to create it", created nothing and exited with code 1.

**Problems and fixes.**
- The fake clock fires every timer at once, which cancels a `CancellationTokenSource(timeout, clock)` immediately and makes loops spin; the runner takes a `Schedule` seam and its tests run on the real clock.
- The "stream delivered data" test used the last-data timestamp, which identical test timestamps defeat; the runner counts data messages instead.
- The first retry design reset the wait on every successful open, so a server that accepts and drops a connection would be hit every second; it resets only after data arrived.
- The host's content root was the current directory, so `appsettings.json` would be missed under a service or scheduled task; it is now the executable's folder.
- `IWsConnector` / `IWsConnection` are internal to `KalTerm.Kalshi`; `KalTerm.Ingest.Tests` was added to its `InternalsVisibleTo` to reuse the scripted connection.
- `tools/kaldb/run-db-tests.ps1` mangled the suite list when run with both suites (a nested `-ArgumentList`); fixed.
- CI hit a race in a slice 3.2 test, `BulkWriterTests.ReportsUnhealthyAfterConsecutiveFailuresAndRecoversOnSuccess`: it waited for the sink to record the batch and then asserted the writer was healthy, but the writer notes the success after the sink returns. It now waits for the state it asserts on (8 clean runs; the failure itself was not reproducible locally).
- This workstation has a SQL Server default instance reachable at `lpc:.` after all (the host's smoke run reached it), though no LocalDB; the project still runs database tests on `kaldb`.

**Not done (per the plan): a live run.** Nothing in 3.4 has talked to Kalshi's WebSocket feed yet beyond the earlier live message tests. The optional short production read-only run, and then the soak in 3.8, each need the owner's go-ahead.

#### 5.5.1 Live-style run on `kaldb` (started 2026-10-18 22:22 server time; owner's go-ahead for the production read-only feed, 100 GB database cap, SQL memory cap 20 GB)

**Setup (once).** The read-only key file was copied to `kaldb:C:\KalTerm\secrets\` (readable by SYSTEM and Administrators only; never read or printed), `KALSHI_ENV=production`, `KALSHI_KEY_ID` and `KALSHI_PRIVATE_KEY_PATH` set as machine variables, and the instance's `max server memory` capped at 20,480 MB.
The ingest runs as the scheduled task `KalTerm-Ingest` (SYSTEM, at boot, restarts 3 times a minute apart on failure, no time limit) from `C:\KalTerm\ingest\releases\<name>`; `release.txt` names the release, `run.ps1` is the wrapper (settings: `lpc:.\KALTERM`, database `KalTerm`, `ProvisionMaxSizeGb=100`; console log to `logs\ingest-<time>.log`, newest 30 kept).
Tools: `tools/kaldb/deploy-ingest.ps1` (hash-synced release, task, wrapper; `-Restart` is a hard stop and start) and `tools/kaldb/ingest-status.ps1` (read-only status: task, process, last status line, warnings, database size, approximate row counts and rates, gaps, run state).

**Not a blocker.** The soak runs from a pinned release folder; builds, tests and new releases never touch it, and a new release only runs after `-Restart`. Database test suites use databases of their own on the same instance and never touch `KalTerm` (they do share CPU and disk). Monitoring is read-only and on demand, plus an hourly session check that reports only anomalies.

**First findings.**
- First start: the host created the `KalTerm` database (`MAXSIZE` 100 GB, recovery simple), applied the schema, and discovered 120,367 open non-combo markets, all within about 27 s of starting; the feed was subscribed and streaming straight away.
- First minutes: about 25,000 ticker rows and 4,000 trade rows a minute stored, 0 dropped, nothing buffered, process about 180 MB.
- About a third of the feed's messages are for combo markets (`KXMVE...`), which D8 skips. They were counted as "unknown markets" and tripped a warning at every power of ten; they are now counted separately (`combos_skipped`) and quietly.
- By accident (a bug in the deploy script's `-Restart` switch, fixed) the process was hard-killed and restarted a minute and a half in. That gave the first live proof of the crash path: one `ingest_down` gap, run state back to `running`, ingest resumed.

**Bugs found in the new scripts.** The status script collapsed single-row query results (PowerShell unrolls a one-element array); the deploy script passed `[bool]$Restart` in an argument list, which PowerShell reads as a non-empty string, so it always restarted.

#### 5.5.2 Reboot test of the ingest on `kaldb` (2026-10-18)

The test asked of an unattended process: after a reboot, does it come back by itself, and is the downtime recorded? Two reboots, the second after a fix.

**Reboot 1 (22:33:39 server time): the ingest did not come back.**
- Windows' shutdown stopped the host gracefully (the run state was marked `stopped` at 22:33:49), so Windows' own clean-shutdown handling reaches the Generic Host.
- `kaldb` booted at 22:34:10 and the task started the host at 22:34:14. SQL Server on `kaldb` is **Automatic (Delayed Start)** and was not up until 22:36:17, so the host could not reach the database and exited with code 1 (a clear message in the log).
- The task's "restart on failure" (3 times, 1 minute apart) **did not retry**: the task sat in `Ready` with last result 1. A start-up ordering problem like this is what only a reboot shows.

**Fix (tooling only).** The wrapper `run.ps1` (generated by `tools/kaldb/deploy-ingest.ps1`) now waits for the SQL Server service to be running, bounded at 10 minutes, then supervises the host: any exit other than code 0 is retried after a wait that doubles from 15 s to 120 s, and only a clean exit ends the wrapper. One log file per wrapper launch.

**Reboot 2 (22:38:05): recovered by itself.** Boot at 22:38:36, the task started at 22:38:39, the wrapper waited until SQL was up (22:40:43), the host started at once and was connected, with 0 dropped, within minutes. The new release (combo fix) was the one running. The downtime is one `ingest_down` gap from 22:33:49 to 22:40:46 ("the previous run stopped cleanly"), about 7 minutes of ticker data that cannot be recovered (trades would be backfilled in M4).
The host had been down since its failed start, so reboot 2 itself cost no further data.

**A failed first attempt.** The first reboot command (`Start-Process shutdown.exe` inside a remote session) did nothing: the session was removed straight away and took the process with it. Running `shutdown.exe` directly worked. The watcher script also now refuses to report success unless the boot time changed.

**Requirements this adds for M8.** The real Windows service must declare a dependency on the SQL Server service and set its own recovery actions, because a scheduled task's restart-on-failure cannot be relied on to retry a process that exits with an error. Until then the wrapper's own supervision does the job. The host itself still exits when the database is unreachable at start-up (by design: a failure to start is thrown), which is why the supervision matters.

### 5.6 Slice 3.5: the API and its contracts (2026-10-19)

Built in three commits: 3.5a contracts, 3.5b readers, 3.5c the API.

**Built.**
- `KalTerm.Contracts` (no dependencies): the models that cross the API (market, tick, trade, gap, status, health, error, and their pages). Prices and counts are decimal **strings** built from the stored integers (`560000` is `"0.560000"`, `1850` is `"18.50"`), times are ISO-8601 UTC with microseconds, names are snake_case, nulls are written, cursors are opaque URL-safe text.
  `ApiFormat` does the formatting; its golden vectors come from the reference server's own `api/format.py` (`tools/vectors/generate_api_format.py`). `ApiJson` is a source-generated context; one test pins the exact JSON of every model and another checks every model is registered.
- `KalTerm.Data.Reading` (Dapper): `MarketReader`, `TickReader`, `TradeReader`, `GapReader`, `StatusReader`, each behind an interface. Every read is parameterized and bounded (market, time window, at most 1,000 rows, 15 s timeout). `varchar` parameters are sent as `varchar` (Dapper's default `nvarchar` would turn a seek into a scan).
  Markets page by ticker. Ticks and trades go forward by time from a start (a cursor resumes exactly: ticks may share a millisecond and have no key, so a cursor is the time plus how many rows at that time were already sent; trades order by time then id), or, with no start, return the latest rows within a 7-day lookback, oldest first. The status read uses small catalog queries and looks for the newest row in 5-minute, then 1-hour, then 24-hour windows.
- `KalTerm.Api` (ASP.NET Core minimal API, library) and `KalTerm.Api.Host` (the executable): `GET /healthz`, `/readyz`, `/v1/status`, `/v1/markets` (filters `status`, `event`, `q`; `limit` up to 500; cursor), `/v1/markets/{ticker}`, `/v1/markets/{ticker}/ticks` and `/trades` (`start`, `end`, `limit` up to 1,000, cursor), `/v1/gaps`. Nothing writes; every other method is a 405.
  Times must carry an offset (`Z` or `+hh:mm`) and are converted to UTC; a start at or after the end is refused. Every error has the reference's shape, `{"error": code, "fields": [...]}`: 422 `invalid_parameter` (names the parameters, never their values; a parameter given twice is invalid), 404 `not_found`, 405 `method_not_allowed`, 503 `unavailable` (database unreachable or timed out), 500 `internal_error`. No failure detail leaves the process. Every response carries `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.
  The status report (`StatusBuilder`) puts problems first: ingest never recorded or stopped, a heartbeat older than 60 s, no ticker or trade row for 2 minutes while ingest says running, none at all in 24 hours, the data file at 90% of its cap, less than 50 GB free. It also reports the gap counts of the last 24 hours with a note that ticker data in a gap is lost.
- **Loopback only (D5).** The process refuses a URL that is not an `http` loopback IP with a port, listens on exactly that endpoint, and checks every bound address after start-up; a configuration mistake prints one line and exits with code 1.
  Settings: `appsettings.json` next to the exe, `KALTERM_Api__*` variables (`Url`, `Server`, `Database`). Run: `dotnet run --project src/KalTerm.Api.Host -c Release` (default `http://127.0.0.1:5280`).

**Verified.** Local: format clean, no warnings, 4,549 tests, 0 failed (96 skipped: the database tests, no LocalDB on this workstation). On `kaldb`: Data 146, Ingest 142 and Api 117, all run passed, including three end-to-end API tests over a real database and the real readers.
Probe of the reader queries on the live soak database (over a million rows, ingest writing): 1 to 158 ms each (freshness checks about 150 ms, everything else under 120 ms).
Smoke run of the executable on this workstation: `/healthz` 200, `/readyz` and the database endpoints 503 without a database, 404 and 422 for unknown routes and bad input, listening on 127.0.0.1 only, and a wildcard address refused at start-up.

**Problems and fixes.**
- System.Text.Json escapes `+` as `+` by default, which would change every time string; the wire context uses the relaxed encoder (the API never embeds JSON in HTML).
- A parameter named `end` trips CA1716 (a Visual Basic keyword); the readers call it `until`.
- Dapper maps records by constructor-parameter name, so columns are aliased to PascalCase; a record named `TradeRow` already existed on the write path, so the read models are `Stored...`.
- The `kaldb` script now writes each suite's runner on the server itself and knows the `api` suite.
- Database API tests must use the real clock: with the fake clock the seeded gap fell outside the "last 7 days" window.

**Left out (as planned).** Series filter (no series ticker is stored), order book (3.7), candles, retention, auth, TLS (M4), watchlists, alerts, layouts, analytics, and a least-privilege database login for the API (M8). The API is not deployed to `kaldb` yet.

#### 5.6.1 The API on `kaldb` (2026-10-19; owner's go-ahead)

Deployed beside the ingest as the scheduled task `KalTerm-Api` (SYSTEM, at boot, the same wrapper as the ingest: waits for the SQL Server service, supervises the host), from the pinned release `C:\KalTerm\api\releases\api-b63069e`, listening on `http://127.0.0.1:5280` only. It reads the live soak database; the ingest is untouched.
`tools/kaldb/deploy-service.ps1 -Service ingest|api` now does both deployments (the ingest's `deploy-ingest.ps1` is a thin forwarder). The wrapper it generates for the ingest is byte-for-byte the one already running (checked with `-PrintWrapper`), so the soak's deploy path did not change.

**First calls against the live data** (over loopback from the server, while the ingest wrote about 25,000 ticker rows a minute into a database of over 1.5 million rows):
- `/healthz` and `/readyz` 200; `/v1/status` 200: no problems, ingest `running` with a 14 s heartbeat, ticker and trade streams 0 to 1 s old, data file 264 MB of 102,400 MB, 920 GB free, markets by status, and the two `ingest_down` gaps from the reboot test.
- `/v1/markets` pages by cursor (`limit=3` then the cursor gives the next page); the status and search filters work (`status=closed&q=KXHIGH`).
- For the busiest market: the market itself, the latest 3 ticks, a forward read from 2 minutes ago in pages of 5 (page 2 starts one tick after page 1 ends), and the latest 3 trades with exact decimal strings and the taker side.
- `limit=0` gives 422, an unknown market and an unknown route 404.
- Timings once warm: 3 to 157 ms (the busiest market's ticks and trades 120 to 160 ms); the first call of each kind about 300 ms and the first status 700 ms, which is start-up (JIT).
- The process uses about 45 MB and listens on `127.0.0.1:5280` only.

**Not yet:** the API runs as SYSTEM with the same database rights as the ingest; a least-privilege login (read-only) is M8's. Nothing reaches it from outside the machine; the CLI (3.6) will reach it through remoting until M4 adds TLS and tokens.

### 5.7 Slice 3.6: the CLI (2026-10-19)

Built in two commits (3.6a client, 3.6b/c CLI and the remote runner).

**Built.**
- `KalTerm.Client`: `IKalTermApi` (status, markets, one market, ticks, trades, gaps) and `KalTermApiClient` over `HttpClient`. It only ever sends `GET`, builds the query in a fixed order with escaping, turns a refusal into an `ApiException` carrying the API's own error code and the fields it named, and an unreachable or slow API into an `ApiUnavailableException` naming the address. A separate library so the later UI reuses it.
- `KalTerm.Cli` (the executable is `kterm`): `status`, `markets`, `market TICKER`, `ticks TICKER`, `trades TICKER`, `gaps`. Shared options: `--api URL` (or `KALTERM_API_URL`, default `http://127.0.0.1:5280`), `--json` (the API's exact JSON; for `market` just the market), `--width`, `--no-color`.
  `markets` takes `--status`, `--event`, `--search/-q`, `--limit`, `--cursor`; `ticks` and `trades` take `--since 15m|2h|1d`, or `--start`/`--end` (times must carry an offset, as at the API), `--limit`, `--cursor`; `gaps` takes `--since` (default 24h) and `--limit`. A page with more behind it prints `More available: --cursor ...`.
  `status` puts problems first, then ingest, streams, storage, markets and the gaps (downtime) of the last 24 hours with the note that ticker data in a gap is lost. **Exit codes:** 0 fine, 1 the API could not be reached or refused the request or the command line was wrong (one line, no stack trace), 2 `status` found problems.
  Display only shortens: prices keep two decimals and lose trailing zeros (`0.560000` is `0.56`), counts get thousands separators and lose `.00`, times are UTC. `--json` keeps the exact strings.
- `tools/kaldb/kterm.ps1 -ComputerName ... -CredentialPath ... [-Publish] -- <kterm arguments>`: publishes the CLI when asked, syncs it by hash to `C:\KalTerm\cli` on the server and runs it there (the API listens on loopback only until M4), returning the output and kterm's exit code.

**Verified.** Local: format clean, no warnings, 4,690 tests, 0 failed (96 skipped: the database tests). New: 21 client tests (stubbed HTTP: URLs, escaping, every error path), 120 CLI tests (the real command-line parser and views over a fake API and Spectre's test console: output, exit codes, error messages, addresses, widths; formatting tables; the table layout). Coverage: `KalTerm.Client` 100%, `kterm` 94.6%.
**Live on `kaldb`** (through `kterm.ps1`, against the running soak): every command, `--json`, and the failure cases (unknown market, bad `--since`, the API refusing `--limit 0`) with exit codes 1; `status` exit 0 with no problems.

**Problems found, mostly only by running it for real.**
- **Remoting mangles non-ASCII text.** The server-side process encodes output in the server's OEM code page and PowerShell decodes it differently on the way back: the `·` separator arrived as `ú` and Spectre's `─` table rules as `Ä`. Output is now plain ASCII (separators `|`, tables of dashes), and a test fails if any command prints a non-ASCII character.
- **Spectre's `Table` squeezes every column** when one long cell does not fit, ignoring fixed widths (the gaps table lost its Id and wrapped "Length"). Tables are now `TextTable`: columns as wide as their widest cell, only the last column wraps with a hanging indent, and on a console too narrow for that the last column moves under each row.
- `kterm.ps1` ran with `$ErrorActionPreference = 'Stop'`, so the CLI's own error line (stderr) became a terminating error and ended the session; stderr is now carried back as output and the exit code is returned.
- Setting `[Console]::OutputEncoding` inside a remoting session fails ("The handle is invalid"); not needed once the output is ASCII.
- **The coverage gate did not see the CLI**: its assembly is named `kterm` and the gate only judged `KalTerm.*`. `check-coverage.ps1 -Include` is now a list (`KalTerm.*`, `kterm`).
- `dotnet format` (the fixing form) turned a compile error into a stub method that throws; run it only on a green build, and `--verify-no-changes` otherwise.
- A scripted edit writing `\n` inside a C# string literal produced a real newline three times; such edits now use the editor tool or character codes.
- `gh pr merge` merged PR #35 while its CI was still running; it finished green, but the merge should wait for CI.

**Left out (as planned).** Profiles, tokens, TLS and pinning (M4); live-updating views; the order book view (3.7); watchlists, alerts; anything that trades.

### 5.8 Slice 3.7: the order book builder, its publication and the `book` view (2026-10-19)

- **3.7a** (committed earlier): `KalTerm.Kalshi.Books` (`LocalOrderBook`, `BookTracker`) ported from the reference and replayed against it with
  `tools/vectors/generate_orderbook.py` (golden scenarios in `Vectors/orderbook.json`). One addition the reference lacks: `BookTracker.MarkStale`,
  used when a book message cannot be read.
- **3.7b**: the ingest process builds books for a static `Ingest:WatchList` (at most 100 markets; empty means no order book subscription, never an
  empty market list, which Kalshi reads as every market). `KalshiWebSocket.RequestSnapshotAsync` sends `update_subscription` with `get_snapshot`.
  `OrderBookKeeper` recovers a gap per market: ask for an in-stream snapshot, wait 5 s, else fall back to REST (marked approximate), and retry
  old stale books every 30 s. It publishes changed books about once a second, and every book every 15 s (heartbeat), to `dbo.orderbook_state`
  (migration 0002, top 100 levels per side as text, one row per market, no history). The API reads that table (slice 3.7c).
- Verified: unit tests for the keeper, runner, ingestor, stream source, host options and level text; an end-to-end composition test (snapshot to a
  row, and a market without a snapshot published as stale). Data, Ingest and Api suites pass on kaldb (160, 175, 117 tests). Locally the Data
  coverage reads 42 % only because LocalDB is missing here; CI judges it.
- **3.7c**: `dbo.orderbook_state` is read by `BookReader` (and summarised in `StatusReader`; a database without migration 0002 reads as "no books" instead of failing).
  The API gains `GET /v1/orderbooks?depth=N` and `GET /v1/markets/{ticker}/orderbook?depth=N` (depth 1 to 100, default 10; 404 for a market that is not
  watched), and `/v1/status` a `books` section: a stale book is a problem, and so are books not rewritten for 60 s while ingest says it is running.
  Kalshi sends bids only, so the contract carries `yes_bids` and `no_bids`; asks are derived by the client (`kterm book`: a yes ask is 1 minus a no bid,
  computed on whole micro-dollars). `kterm book` lists all watched markets (best bid, ask, spread, STALE/APPROX flag, age); `kterm book TICKER --depth N`
  prints the ladder. `kterm status` has a Books line.
- Live check (2026-10-19, production, public `orderbook_delta` channel, subscribe only, plus three REST book reads): three moderately active markets
  (KXTEMPMIAH-26OCT1912-T85.99, KXTRUMPSAY-26OCT26-SUPE, KXTRUMPSAY-26OCT26-MELA) were followed for 60 s: 3 snapshots, 33 deltas, 0 gaps, no crossed book,
  and all three built books equalled the REST books read right after the window. Test: `LiveKalshiWebSocketTests.ABookBuiltFromTheLiveStream...`.
- Not rolled out: kaldb still runs the pre-3.7 build; the watch list goes live in slice 3.8 with the owner's go-ahead (ingest restart, migration 0002).

### 5.9 Slice 3.8: the rollout on kaldb, the M3 gate (2026-10-19)

- Owner approved the restart and production as the feed. Watch list: the 50 markets with the most ticker rows in the last hour among active markets closing
  in more than 3 days (picked from kaldb's own data, read-only). `deploy-service.ps1` gained `-WatchList` (ingest only; written as `KALTERM_Ingest__WatchList__N`
  lines in the wrapper). Pass `-ReleaseDir` as an absolute path (a relative one broke the file hashing; nothing had been switched at that point).
- Ingest `host-d475ac4` restarted 09:56 server time (hard stop; the downtime is the third `ingest_down` gap); migration 0002 applied on start; log showed
  `books=50/50` within two minutes. API `api-d475ac4` restarted afterwards.
- Gate through `kterm` on kaldb (CLI republished with `-Publish`): `status` passes with a Books line (`50 watched, none stale`), `book` lists all 50
  (empty books of ended matches show as `-`), `book TICKER --depth 4` prints a ladder with derived asks and a 0.02 spread, `book NOPE-1` is `not found` (exit 1).
  Book-versus-REST equality was shown by the live test in 5.8 (3 of 3 equal after 33 deltas).
- The soak continued on the same database throughout; ticker rows resumed at about 35,000 per minute.

### 5.10 Slice 4.1: storage governor and retention (2026-10-19)

- Owner decisions: raw retention 30 days for both ticker and trade (one window: the two tables share the partition function, so a day is dropped from both),
  budget thresholds 80 % (status warns) and 90 % (the governor drops), the newest 3 days never dropped, and the data file cap on kaldb raised from 100 GB to
  **500 GB** (`ALTER DATABASE ... MODIFY FILE (MAXSIZE = 512000MB)`, done 2026-10-19, no restart). Settled-market slimming and merging of old empty partition
  boundaries are parked (PLAN section 9).
- `KalTerm.Data.Governor`: `StorageGovernor` runs inside the ingest host every 15 minutes (`GovernorCheckInterval`). **Retention**: once a day from 00:30 UTC every
  boundary day older than `Ingest:RetentionDays` (default 30, must exceed 3) is truncated, oldest first, and the day is recorded (`governor.retention_day`); a failed
  day is retried at the next check. **Budget**: measured against **used** space (`FILEPROPERTY(..., 'SpaceUsed')`), because `TRUNCATE` frees pages inside the file
  but never shrinks it; at 90 % of the cap the oldest raw day is dropped, one per check (freed pages return asynchronously), never in the same check as a retention
  drop, and never inside the newest 3 days (then the report says nothing more can be dropped). Each check records `governor.last` (`ok` or `error`, percent used, days
  dropped) and `governor.oldest_day`.
- `/v1/status` storage section: `used_bytes`, `used_percent`, `oldest_data_day`, `governor_last`, `governor_age_seconds`. Problems: 80 % used; 90 % used (the governor is
  dropping); a governor error; no governor check for an hour while ingest runs. The old file-size warning is gone (the file does not shrink after a drop).
  `kterm status` shows used versus cap and a Retention line.
- Measured on kaldb: the oldest-day query takes 176 ms on 22 M rows, so it runs in the governor and its result is stored, not computed per status call.
- Tests: policy against a fake store and clock (retention window, once a day, time of day, floors, shedding order, failures, cancellation); the store on real
  partitions on kaldb (oldest day, boundaries, truncation, refusal of a day without a boundary); runner and composition. Data, Ingest and Api suites pass on kaldb.
- Rolled out 2026-10-19 together with 4.2 (ingest `host-21c5cc5`, API `api-21c5cc5`): the first governor check was ok at 0 % used; `kterm status` shows the Retention line.

### 5.11 Slice 4.2: trade backfill (2026-10-19)

- Owner approved the window rules (10 s padding, 6 h cap taking the most recent part, 15-minute slices, wait 30 s after a gap closes), backfilling the gaps already
  recorded by the soak, and batching the rollout with 4.1 (one ingest restart). Ticker updates and order books in a gap are never recovered.
- Migration 0003 adds `backfill_status` (pending, running, done, failed, skipped), `backfill_attempts`, `backfill_found`, `backfill_added`, `backfill_at` and
  `backfill_note` to `ingest_gap`; older rows become `pending`, so the gaps recorded so far are recovered the first time the worker runs.
- `TradeBackfiller` (`KalTerm.Ingest.Backfill`) runs in its own loop in the ingest host (a long gap takes minutes and the maintenance loop must keep the heartbeat). It claims
  the oldest closed, settled, pending gap (`BackfillStore.ClaimNextAsync`, one UPDATE with READPAST, so a second process cannot take the same gap; a gap left `running` by a dead
  process is taken over after 10 minutes). Per 15-minute slice it reads `GET /markets/trades` (`RestTradeSource`, whole-second `min_ts`/`max_ts`) and the trades already stored
  for the slice plus 5 s; a trade is written only if its (market, trade id) is not stored and was not returned in the previous slice, so re-running a window cannot duplicate a
  row (the reference's worst bug). Trades go through the normal trade writer; combo trades are skipped as in the stream, unknown tickers are counted. It pauses 100 ms after every
  1,000 trades so discovery keeps its share of the one rate limiter. Three attempts, a minute longer between each, then `failed`.
- Reporting: `GapDto.backfill` (status, found, added, at, note), `GapSummaryDto.backfill_pending` and `backfill_failed`; a failed backfill is a status problem; `kterm gaps` has a
  Trades column and `kterm status` counts waiting and failed backfills. `Ingest:BackfillEnabled` (default true) switches it off.
- Rollout order matters: the API reads the new columns, so ingest (which applies migration 0003 at start) must be restarted before the API. A new API against an unmigrated
  database would fail `/v1/gaps`.
- Tests: the backfiller against a fake store, source and writer (padding, slicing, caps, duplicates in storage and within and between slices, writer full, retries, failures,
  cancellation); the store on real tables on kaldb (claim order, settle, open and market-specific gaps, retry delays, takeover, completion); the readers; an end-to-end
  composition test (a pending gap, a fake REST source: two rows written, the combo skipped, the gap `done`); the runner loop. Data, Ingest and Api suites pass on kaldb.
- **Live check (owner opted in, 2026-10-19, production, GET only, public trade data) found a bug in the first version.** `LiveTradeBackfillTests` reads a three-minute window that ended
  ten minutes ago, whole and as two slices. The first run: 27,808 trades whole but 15,223 + 12,430 in two slices; a second run with a per-second breakdown showed every missing trade
  was in the last second before the cut. **Kalshi's `max_ts` is exclusive (and `min_ts` inclusive), and trade times carry milliseconds** (the filter is whole seconds). The first
  version cut slices at `end - 1 s`, which would have lost a second of trades at every 15-minute boundary. Fixed: a slice is `[start, end)` with `max_ts = end`, the last slice
  reads one second past the padded end, `ITradeSource` documents the exclusive end, and the unit tests pin the ranges. Final run: 28,982 whole = 14,626 + 14,356 in two slices,
  no repeated id, every trade inside the window, yes + no = $1 on every trade (about 135 requests across the three runs).
- Rolled out 2026-10-19 together with 4.1 (ingest `host-21c5cc5`, migration 0003 applied at start, then the API): the four `ingest_down` gaps of the soak were recovered the same minute (235 of 2,704, 55,282 of 63,978, 1,131 of 5,530 and 540 of 3,927 trades written; the rest already stored or combos), and `kterm gaps` shows them as recovered.

### 5.12 Slice 4.3: backup and restore (2026-10-19)

- Owner decisions: the target is a network share on another machine of the owner's (seen on kaldb as a mapped drive; its name is deliberately not in the repository), grandfather-father-son
  retention, and a new admin console. kaldb is in a workgroup and SQL Server runs as the virtual account `NT SERVICE\MSSQL$KALTERM`, which cannot authenticate to the share, so SQL Server
  writes to a **staging folder** (its own default backup folder) and the ingest host verifies the file and copies it to the share with a stored credential, then deletes the staging file.
  The database uses the simple recovery model, so backups are full and differential only.
- `KalTerm.Backup` (library): `BackupService` takes a full backup weekly (Sunday UTC, or when none is under 7 days old) and a differential on the other days; `WITH CHECKSUM, COMPRESSION`,
  then `RESTORE VERIFYONLY`, then a copy to `name.bak.partial` on the target, a SHA-256 comparison of the copy, a rename and last the manifest (`name.bak.json`), so nothing that looks like a
  backup exists until it is whole. The **target must carry a marker file** (`kalterm-admin backup init-target`, run once with the share present) and have 1.2 times the last backup free; the
  staging folder too. Retention (`GfsRetention`, pure and tested): the newest full backup is always kept; the newest full of each of the last 4 ISO weeks (fathers) and 12 calendar months
  (grandfathers); a differential only while it is under 7 days old (sons) **and** its full backup is kept; only files in our naming are ever deleted, the manifest first.
- **The manifest holds per-day row counts of ticker and trade from just before and just after the backup.** Ingest keeps writing during a backup, so a restored copy is judged by
  `min(before, after) <= restored <= max(before, after)` per day (tables only grow; retention may drop a whole old day), and a day that did not change must match exactly.
- `RestoreService` never overwrites: it creates a **new** database (refusing the live name and any existing name), copies the chain (a differential and its full backup) to staging checking each
  file's hash against the manifest, restores with `MOVE` to new files, runs `DBCC CHECKDB` (physical-only by default, `--full-check` for the complete check) and compares the counts with the
  manifest. Switching the server to a restored copy stays a manual step. `VerifyAsync` does the same into a scratch database that is dropped again, and records the result.
- `BackupSchedule` in the ingest host (own loop, checked every minute): a backup is due when none was ever good, when the last good one is over 26 hours old, or after 08:00 UTC when there has been
  none today; after a failure it waits 30 minutes; after every full backup it proves the backup by restoring it (physical-only). `Ingest:BackupTarget` (empty = off), `BackupStaging`,
  `BackupCredentialPath`, `BackupDaily/Weekly/Monthly`, `BackupAt`.
- The share credential is stored by `kalterm-admin backup set-credential` (password from a prompt or standard input, tried against the share first) protected with DPAPI for the machine in a
  file whose ACL allows only SYSTEM and administrators (`C:\KalTerm\secrets\backup-share.cred`); `tools\kaldb\admin.ps1 -SetShareCredential` asks on the workstation and stores it on the server.
  The share connection (`WNetAddConnection2`) has no drive letter and lives only for the run.
- `kalterm-admin` (new project, runs on the server; `kterm` stays read-only): `backup init-target | set-credential | run [--full|--diff] | list | verify [--full-check]` and
  `restore --database NAME [--backup NAME] [--keep] [--full-check]`; `tools\kaldb\admin.ps1` runs it through remoting.
- Reporting: `/v1/status` has a `backup` section and `kterm status` a Backup line. Problems: no finished backup 36 h after a target was configured; the last good backup older than 36 h; the latest
  attempt failed; the last proof by restore failed.
- Tests: GFS rules (six months of Sunday fulls with daily differentials), naming, manifest JSON and count comparison, the target (marker, space, listing, deletion of only our names, partial
  files), the credential store (round trip, ACL), the services against a fake database (kinds by day, failure leaves nothing behind, retention, network connection used and released, chain
  restore, hash mismatch, refusals), the schedule, the admin commands; and **real SQL Server on kaldb**: full and differential backups restored into a new database that passes `CHECKDB` and
  whose counts equal the live ones, a damaged file failing verification, the whole service plus a proof by restore, and the ingest host taking a backup of its own database.
- **A mistake worth recording:** the first version of the admin and service tests used the real share's UNC path. Because the workstation can reach that share, one test created the folder and the
  marker file there and the others wrote 5-byte fake backups into it. It was noticed because two tests that should have failed passed; the five test-made files were checked (fake manifests) and
  deleted, nothing else on the share was touched, and the tests now use only `\\127.0.0.1\kalterm-no-such-share` (an address on this computer where nothing can be written) and neutral names.
  CLAUDE.md now says tests never use a real host, share or account name.
- **First real run on kaldb (2026-10-19, through `admin.ps1`, against the owner's share, GET/read of nothing but our own database):** the owner stored the share credential; `backup init-target`
  wrote the marker from kaldb over SMB; a **full backup of the live 1.1 GB database took 21 s and is 564 MB** (compressed) on the share; a **differential** one 2 s and 30 MB; a **restore drill** of the
  full backup into a new database took 13 s, the complete `DBCC CHECKDB` was clean and the per-day row counts agreed with the manifest while ingest kept writing; the same for the full + differential
  chain (14 s); `backup verify` (the proof the host runs after each full backup) passed; no scratch database and no staging file was left behind. Two defects found by this run and fixed: `backup list`
  did not connect to the share with the stored credential (it showed an empty folder), and `admin.ps1` bound the command words to its own `-Instance`/`-Database` parameters (now non-positional).
- Rolled out 2026-10-19: ingest `host-bae1930` with `-BackupTarget`; `kterm status` shows the Backup line (last good backup, last proof by restore ok).

### 5.13 Slice 4.5: users and tokens, TLS and certificate pinning (2026-10-19)

- Owner decisions: the loopback plain-HTTP listener keeps answering `/healthz`, `/readyz` and `/v1/status` **without a token** (the hourly soak check uses it; only local administrators can reach it
  and they already have the database), everything else needs a token; the server's address (192.168.1.20) is a router reservation; the firewall rule is for the local subnet only; tokens do not expire
  unless asked.
- `KalTerm.Security` (new, shared): the self-signed server certificate (ECDSA P-256, SANs for the host names and addresses, 2 years), stored in a folder only SYSTEM and administrators can open with the
  key's password protected for the machine (DPAPI), certificate fingerprints (SHA-256, upper-case hex; people may type colons or spaces), and the file-protection helper that `KalTerm.Backup` now shares.
- Migration 0004: `app_user` (unique lower-case name, role `read` or `admin`, disabled flag) and `api_token` (SHA-256 of a 256-bit random token, label, created, optional expiry, last used to within
  a minute, revoked). The token (`kt_` plus 43 characters) is shown once; a copy of the database holds nothing usable.
- API: `Api:Url` stays the loopback listener; `Api:LanUrl` (https, one specific IP, an explicit port) adds the network listener with the certificate from `Api:TlsDirectory`. The process **refuses to start**
  serving the network without tokens or without a certificate, and stops itself if it finds it is listening anywhere it was not configured to (loopback http, or exactly the LAN address over https).
  Every request needs `Authorization: Bearer <token>` except `/healthz` and `/readyz` and, on the plain loopback listener only, `/v1/status` (a loopback listener reached by a remote caller, for example
  through a proxy, gets no exemption). A refusal is always the same bare `401 {"error":"unauthorized"}`: unknown, expired, revoked and missing look alike. After 10 failures in a minute an address gets
  `429 too_many_attempts` (with `Retry-After`) even with a good token. A known token is remembered for 30 seconds, so a revoked token can work for up to half a minute.
- `kalterm-admin`: `user add|list|disable`, `token create|list|revoke` (create shows the token once), `cert init|show|rotate`. These need no backup target. `cert rotate` keeps the names and gives a new
  fingerprint; it needs an API restart and every client must trust the new fingerprint again.
- Client: for an https address the client accepts **only** the certificate whose fingerprint the user pinned (the usual authorities do not apply); an unknown server is `NotTrusted` and a different
  certificate from the pinned one is a hard `Changed` error naming both fingerprints, and the token is never sent in either case. Pins live in `%APPDATA%\KalTerm\known-hosts.json`; tokens in
  `%APPDATA%\KalTerm\credentials.bin` (DPAPI for the Windows user). `kterm trust host:port` shows the certificate and pins it only if the person types the first 8 characters of the fingerprint
  (or passes the whole fingerprint with `--fingerprint`; without a keyboard and without `--fingerprint` the answer is no); `kterm login` tries the token on the server before storing it;
  `kterm logout`. `KALTERM_API_TOKEN` supplies a token for scripts.
- Tests: certificates and fingerprints; users and tokens on real tables on kaldb (hash only, expiry, revocation, disabled users, last-used once a minute); the middleware (uniform 401, bearer parsing,
  open paths, the loopback exemption and its limits, throttle, cache, revocation after the cache expires); host options; the admin commands; and the client against **a real TLS server on loopback**
  (trusted, not trusted, changed, the token reaches only a trusted server, a certificate loaded from disk works as a server certificate).
- A find: loading the stored certificate with ephemeral keys made Windows reject it as a server certificate; the default key storage works (the key is removed when the certificate object is disposed).
- **Rolled out and checked live (2026-10-19, from the owner's workstation to kaldb over the LAN):** ingest `host-eb10d4d` (restarted first: migration 0004 is applied by ingest at start), then the
  certificate (`cert init --host kaldb --ip 192.168.1.20`), user `steven` (admin) with a token created through `admin.ps1` and piped into `kterm login` without ever being printed, then API `api-eb10d4d`
  with `-LanUrl https://192.168.1.20:5280 -OpenFirewall`. The firewall rule reads back as inbound TCP 5280, allow, **remote address: LocalSubnet**; the server listens on exactly `192.168.1.20:5280` and
  `127.0.0.1:5280`. Checks: before `trust` the client refused the server and sent nothing; after `trust --fingerprint` (the fingerprint read on the server) and `login`, `kterm status`, `book` and `gaps`
  work from this computer; a wrong token gives the "run kterm login" message; with no token every `/v1/*` path (including an unknown path) is a bare 401 and `/healthz` is 200; a malformed
  Authorization header is 401; plain HTTP on the LAN address gets no answer; **a deliberately wrong pin gives the hard "certificate CHANGED" error naming both fingerprints, and the deliberate re-trust
  warns about the difference**; on the server, loopback `http://127.0.0.1:5280` answers `/v1/status` and `/healthz` without a token and `/v1/markets` with 401 (so the hourly soak check is unchanged);
  after 10 failed attempts in a minute the caller got 429 (even with the right token) while `/healthz` stayed open; `token list` shows the token's last use.
- `tools\kaldb\kterm.ps1` (the remoting wrapper) now works for `status` only (the loopback exemption); data commands need a token, so use the CLI directly from a computer that trusts the server.
- Not done: nothing is exposed except the API port; mDNS discovery, WebSocket push and per-user watchlists remain out of scope (parking lot / M5, M7).

### 5.14 Slice 4.4: candles (2026-10-19)

- Migration 0005: `dbo.candle` keyed `(interval_s, market_id, bucket_start)` (page compressed) and `ingest_gap.candles_built_at`. A candle is the yes-price open, high, low and close of the trades in a UTC,
  epoch-aligned bucket (a week starts on Monday), the volume and trade count, and the yes bid, ask and open interest of the last ticker update. No trade means no price; nothing is forward-filled.
- Stored intervals are configuration (`Ingest:Candles`, default `1d=all;1h=all;1m=watched`; 1, 5, 15 minutes, 1 and 4 hours and 1 day are allowed). The ingest host records the rules in `candles.stored`
  so the API knows what is stored. The candle job runs about once a minute: for each interval it builds the buckets closed more than 90 seconds ago (up to a day, or 6 hours for a minute interval, per run, so a
  catch-up does not hold the database), never reaches before the oldest raw day (rebuilding a candle after the governor dropped its rows would erase it), and remembers how far it has built.
- Gaps (option C): a bucket overlapping a gap is `partial` with `gap_kind` and the seconds covered. When a gap's trade backfill finishes, the candles over it are rebuilt and the trade flag clears; the
  book flag (bid, ask, open interest) stays. Rebuilding replaces the rows of the range, so running twice is harmless.
- `GET /v1/markets/{ticker}/candles?interval=&start=&end=&limit=&cursor=`: the interval is whole minutes up to a day, or a week (`15m`, `4h`, `1d`, `1w`, or bare minutes), default `1h`; anything else is
  `422 invalid_parameter` naming `interval`. The answer is the stored interval when it exists, else an exact roll-up of the coarsest stored shorter interval that divides it (first open, last close, highest
  high, lowest low, sums), else computed from the market's raw rows for at most 24 hours (`source` says which). A watch-list-scoped interval is used only for a watched market. The page carries
  `interval`, `source`, the candles and a `next_cursor` (the next bucket). `kterm candles TICKER --interval 15m --since 6h` shows them, partial ones in yellow. Times are still printed in UTC; the
  selectable client time zone (`--tz`/`--utc`) is a separate item not yet done.
- Tests: rules and roll-up (pure); the builder on real tables on kaldb (OHLC, sparse buckets, idempotent rebuild, gap flags with and without a finished backfill, slicing, compute equals stored); the job
  against fakes; the host building the candles of rows already in the database; the service (stored, rolled up, computed, paging, scope); the endpoint, contract pins and the CLI view.

### 5.15 The client time zone (2026-10-20)

- The database and API stay UTC and `--json` prints the API's own text. Every time `kterm` prints is shown in a chosen zone: `--utc`, else `--tz <zone>` (for example `America/Denver`), else `KALTERM_TZ`,
  else this computer's zone. There are no short aliases. A zone the computer does not know is refused before any call, and the message says whether it came from the variable.
- Tables name the zone in the column header (`Time (America/Denver)`); a time in a sentence carries the offset that applied at that moment (`2026-10-18 17:00:00 -06:00`, or `UTC`).
- The choice flows with the running command (an async-local), so nothing is shared between commands. A stored per-user setting waits for the per-user features (M5/M7).

## 6. M4 Server summary (2026-10-20): done

Gate "API contract tests green": met. The contract pin tests (`KalTerm.Contracts.Tests`, 27) cover every model that crosses the API, including the candle and page models; the
API, client and CLI tests, and the database suites on `kaldb`, pass; CI is green on `main` (PR #45 and #46 merged 2026-10-20).

| Slice | What | Notes |
|---|---|---|
| 4.1 | Storage governor and retention | budget 500 GB, hard `MAXSIZE`, oldest whole days dropped under pressure (5.10) |
| 4.2 | Trade backfill | recovers trades over a gap from REST; `max_ts` is exclusive (found by a live test) (5.11) |
| 4.3 | Backup and restore | GFS retention on the NAS share, restore only into a new database, proof by row-count manifest and `DBCC CHECKDB` (5.12) |
| 4.5 | Users, tokens, TLS, pinning | uniform 401, throttle, trust on first use (5.13) |
| 4.4 | Candles | stored `1d`/`1h` for all markets and `1m` for the watch list, exact roll-up, gap flags, `kterm candles` (5.14) |
| - | Client time zone | `--utc`, `--tz`, `KALTERM_TZ`, else the computer's zone (5.15) |

Running on `kaldb` at close: ingest and API release `host-7e4d2bc`; status reports no problems; backup and restore proof ok; the 1m watch-list candles were still catching up.

Carried forward, none in M4: a stored per-user time zone (with per-user settings, M5/M7), persisted deltas, live add/remove of watched markets, WebSocket push (M5), mDNS discovery. Parked
items stay in PLAN.md section 9.

### 5.16 Slice 5.1: market metrics (2026-10-20)

- `GET /v1/markets/{ticker}/metrics` and `kterm metrics TICKER`: computed when asked, for one market, as of now. No new tables, no migration. Unknown market is 404.
- Figures (all fixed-point, no floating point, division rounds half up): last, bid, ask, open interest and spread (ask minus bid) from the latest ticker update in the last 7 days; `volume_24h`, `trade_count_24h`
  and `vwap_24h` (sum of price times count over sum of counts) from the raw trades of that market in the 24 hours before now (the window is open at its start); `change_24h` is the last price
  minus the close of the last priced hourly candle that ended at or before 24 hours ago; `sma_20` and `sma_50` are the mean of the latest 20 or 50 priced hourly closes; `volatility_24h` is the sample
  standard deviation, in dollars, of the changes between consecutive closes among the latest 24 priced hourly closes (integer square root, no log returns).
- A metric that lacks data is `null`, not a smaller number: an average needs all of its closes, the volatility needs three, a spread needs both sides. Hours without trades have no price and are not
  closes (no forward fill). `partial` is true when a gap overlaps the 24 hours, or a candle used is flagged partial.
- Change from the approved plan: volume and VWAP come from raw trades only (not closed candles plus a raw tail), so they do not wait for the candle job and are exact.
- Tests: the arithmetic as pure vectors (rounding, a huge volume, short histories); the reader on real tables on kaldb (window edges, as-of filtering, reference close, closes newest first, the 50 cap, partial
  from a candle or a gap); endpoint, contract pin and CLI view.

### 5.17 Slice 5.2: the analyzer runner and the market_metrics analyzer (2026-10-20)

- Order change approved 2026-10-20: the runner comes before the screener (the screener reads the table the runner keeps fresh). Migration 0006 adds `dbo.market_metrics` (one row per active market) and
  `dbo.analyzer_run` (run history, kept 7 days).
- `IAnalyzer` (name, interval, `RunAsync(context)`) and `AnalyzerRunner`, a loop in the ingest host (`Ingest:Analytics`, default on; checks every 30 s). An analyzer starts when its interval has passed since its
  last start (a failure counts, so a broken one is not hammered); each run holds a session application lock (`sp_getapplock`, no wait) so runs never overlap, within a timeout (5 minutes), is recorded in
  `analyzer_run` (running, ok, failed, timeout; a stop is recorded as failed "stopped"), and raises the analyzer's change counter (`analyzer.NAME.version`) when its results changed. Failures are logged,
  never thrown.
- `market_metrics` (every 5 minutes): the universe is every market with a ticker update in the last hour, a trade in the last 24 hours or an hourly candle in the last 25 hours. `MetricsBatchReader` reads
  it in slices of 25,000 market ids (newest tick per market over the last hour, trade sums over 24 hours, the newest 50 priced hourly closes) and `MetricsInputsBuilder` (shared with the per-market endpoint)
  assembles the inputs, so both paths pick the same reference candle and the same partial flag. `MetricsCalculator` does the arithmetic. `MarketMetricsStore` bulk-copies a batch into a temporary table and
  writes only the rows whose figures differ (`changed_at` says when a row last changed; how fresh the table is comes from the run history); markets that left the universe are deleted after a completed pass.
- Difference from the single-market endpoint, by design: the table looks for the latest tick only in the last hour (a quiet market has no book figures rather than stale ones); the endpoint looks back 7 days.
- Found while testing: a temporary table made by a Dapper command with parameters is dropped when the command ends (it runs inside `sp_executesql`); create it with a command without parameters.
- Tests: the runner against fakes (due, interval, lock, failure, timeout, stop, store failure, pruning, name rules); the analyzer against fakes; the store and the lock on real tables on kaldb; the set-based
  reader against the per-market reader on identical data (must agree exactly), universe rules and slicing; the whole host filling the table and recording the run.
- Not yet: `status` does not show the analyzer runs (to come with the screener, which shows the refresh age); the rollout and a measured run on the production feed.

### 5.18 Slice 5.3: the screener and the analytics status line (2026-10-20)

- `GET /v1/screener?sort=&order=&status=&min_volume=&limit=&cursor=` ranks the markets of `dbo.market_metrics` joined to the catalog. Sorts: `volume` (default), `trades`, `change`, `last`, `spread`,
  `open_interest`, `volatility`; `order` is `desc` (default) or `asc`; `status` filters by market status; `min_volume` is contracts with up to two decimals. A bad parameter is a 422 naming it.
- Paging is keyset on (sort key, market id), so a page never repeats or skips a row while the table refreshes. A market without a value for the sort (no change yet, no spread) is last whichever way the
  list runs (its key is stored as the extreme that sorts last). Ties break by market id.
- The reply carries `refreshed_at` (the start of the newest successful `market_metrics` run, the moment the figures describe) and `age_seconds`, so a stalled runner shows in the data itself; `null` until a
  run has finished. Each row has the same figures as the metrics endpoint plus the market's event, status, yes label and `changed_at`.
- `status` gains an Analytics line and problems: the last run failed or timed out (with its message), or no successful run for 15 minutes while ingest runs. Older servers report `null`.
- `kterm screen [--sort S] [--asc] [--status S] [--min-volume N] [--top N] [--cursor C]`: partial rows are marked in yellow, changes carry a sign, the footer says how old the figures are; times follow the
  chosen time zone.
- Tests: the reader on real tables on kaldb (sorts, ties, missing values last in both directions, keyset paging over ties and missing values both ways, filters, the refresh time from the newest successful
  run only, limits); endpoint parameters, cursor and mapping; the status builder; contract pins; the CLI view and the status line.

### 5.19 Slice 5.4: per-user watchlists, alerts and the stored time zone (2026-10-20)

- Rollouts first, for the record. 5.2 (2026-10-20): ingest restarted for migration 0006; the first `market_metrics` run took 15.3 s for 201,567 markets, the second 10.6 s with 16,724 rows written (only changed rows are
  written); every figure of the two busiest markets equalled the per-market endpoint. 5.3 (2026-10-20): API redeployed; the top six active markets by 24-hour volume agreed with Kalshi's own `volume_24h` within about 0.2%
  for five of them and 6.5% for one match trading actively (our figures were 92 s old and the window definitions differ slightly).
- Migration 0007: `watchlist`, `watchlist_market`, `user_setting`, `alert`, `alert_fire`, all hanging off `app_user`. Every store call takes the user id of the caller (from the token) and filters by it; there is no way to
  name another user. Limits: 20 watchlists, 500 markets a list, 200 alerts. Watchlist names are lower-case letters, digits, dots, dashes and underscores.
- Alerts watch one market. Kinds and thresholds: `price_above` / `price_below` (dollars, strictly between 0 and 1; the last traded price), `spread_above` (dollars; ask minus bid), `volume_spike` (a whole factor 2 to 1,000: the
  last 15 minutes of volume against the average 15 minutes of the 24 hours before; no baseline means no judgment), `oi_change` (contracts, at least 1: open interest moved by that much, up or down, over the last hour).
- Firing rule (`AlertRules`, pure): the first judgment of an alert only records whether its condition already holds, so nothing fires for what was true when it was made; after that it fires when the condition becomes true,
  no sooner than its cooldown (default 60 s, 10 to 86,400) after the last fire, and is ready again once the condition has cleared. Data that is missing, or overlaps a gap (the last minute for price and spread, the
  recent 15 minutes for a spike, the last hour for open interest), decides nothing. Resuming a paused alert makes it judge afresh.
- The `alerts` analyzer runs every 10 s in the runner. Two runners now, each in its own loop (alerts; metrics), so a 10-second metrics refresh never delays an alert; the runner check interval is 5 s. It reads the raw
  ticker and trade rows of only the markets that have alerts, records each judgment and every fire in one transaction, and raises the `analyzer.alerts.version` counter when something fired (for the push in 5.5).
- The API gains its first writing endpoints: `GET/POST /v1/watchlists`, `GET/DELETE /v1/watchlists/{name}`, `PUT/DELETE /v1/watchlists/{name}/markets/{ticker}`, `GET/POST /v1/alerts`, `PATCH/DELETE /v1/alerts/{id}`,
  `GET /v1/alerts/fires?after=&limit=`, `GET/PUT/DELETE /v1/me/settings/time_zone`, and `GET /v1/screener?watchlist=NAME`. Bodies are small JSON objects (at most 4 KB, a bad one is a 422 naming `body`); a duplicate
  or a full list is a 409; a missing market a 404 `market_not_found`; without a user (a server that does not require tokens) all of them are 401. The client sends changes only under `/v1/watchlists`, `/v1/alerts`
  and `/v1/me/settings` (a test enforces it) and never talks to Kalshi.
- CLI: `kterm watchlist list|show|create|delete|add|remove`, `kterm alert list|add|pause|resume|remove|fires`, `kterm settings tz [ZONE] [--clear]`, `kterm screen --watchlist NAME`. The stored time zone now sits in the
  precedence after `KALTERM_TZ` and before the computer's zone; it is asked for only when a token is known for the server and quietly ignored if the call fails.
- Tests: the kinds, thresholds and the firing rule as pure tests; the stores on real tables on kaldb (isolation between two users, limits, cascades, ordering of fires, the evaluation reads with gaps and the
  insert chunk, applying outcomes in one transaction); the analyzer against fakes; the whole host firing an alert from ticks written while it runs; endpoint validation and ownership; contract pins; client requests;
  the CLI commands.
- Not yet: the push of fires to clients (5.5; for now `kterm alert fires --after ID`).
- Found by the live check after the rollout: the first alerts analyzer took 34 to 39 seconds a run for three alerts. The cause was how the newest tick of each market was looked up: ids taken from a joined temporary
  table made SQL Server scan the ordered columnstore for every market (about 10 s a lookup, measured), while the same lookup with the ids written into the statement takes about 0.2 s. The reader now writes the ids
  (in chunks of 500) into the statement and widens the window step by step (1 minute, 10 minutes, 1 hour, 24 hours), so busy markets are found at once and only quiet ones need the longer looks.

### 5.20 Slice 5.5: pushing alert fires to clients (2026-10-20)

- `GET /v1/alerts/stream` is a Server-Sent Events stream of the caller's own fires over the same HTTPS listener, token and pinned certificate. It opens with `id: N / event: ready` (N is where it stands: the newest
  fire id, or the id asked for), then one `event: fire` per fire (`id` is the fire id, `data` the same JSON as `GET /v1/alerts/fires`), and a `: keep-alive` comment every 15 s while quiet. `?after=ID` or a
  `Last-Event-ID` header replays what came after that id first; by default only new fires are sent. Fires are read in batches of 100, so a client far behind catches up in steps.
- How a fire reaches a stream: one watcher in the API (`AlertStreamWatcher`) reads the `analyzer.alerts.version` counter once a second; the streams wait on it (`AlertStreamHub`) and query their own new fires only
  when it moves, so idle streams cost one small read a second in all. A stream reads the counter before it reads its fires, so a fire between the two still wakes it.
- Limits (owner 2026-10-20): 10 streams per user and 250 in total; over a limit the answer is `429 too_many_streams` with `Retry-After: 30`. A stream rechecks its token about every 30 s (at its next wake-up after
  that), and ends if the token was revoked, expired or its user disabled; the next connect is then refused. Without a user (a server that does not require tokens) the stream is a 401.
- Client: `IKalTermApi.StreamAlertFiresAsync` yields what the stream says (the `ready` event and the fires); `AlertWatcher` keeps it going: it reopens after a drop, an unreachable server or a closed stream from the
  last id it saw (waits of 1 s doubling to 30 s, starting over when something arrives; 30 s after `too_many_streams`) and gives up only on a refusal waiting cannot cure, such as an unaccepted token.
- CLI: `kterm alert watch [--after ID]` prints one line per fire (`--json`: one object per line) on standard output and its notices (reconnecting) on standard error, until Ctrl+C.
- Tests: the hub (limits, wake-up, timeout, failed reads, cancellation); the stream over a test server (ready event, a fire after open, `after`, `Last-Event-ID`, keep-alive, limit and release, 401, bad `after`, a revoked
  token ending the stream); the event parser and the client against a stub; the watcher's resume, backoff and refusals; the CLI command.
- Rollout and live check of 5.5 (2026-10-20): API redeployed (`host-327d431`). `kterm alert watch` from the owner's workstation over HTTPS, with price alerts on a 15-minute BTC market, received two fires 1.95 s and
  1.27 s after the server committed them (including the one-second counter poll and any clock difference between the machines). The reconnect and resume path is covered by tests only.

## 7. M5 Analytics summary (2026-10-20): done

Gate "per-user alerts fire": met. A user makes an alert from the command line; the analyzer judges it every 10 seconds against live data; the fire is stored and reaches the user's terminal about one to two seconds
later; all of it scoped to the user's token. Every slice was rolled out on `kaldb` and checked against the production feed.

| Slice | What | Notes |
|---|---|---|
| 5.1 | Market metrics | last, 24-hour change, volume, VWAP, spread, averages and volatility, integer arithmetic only; volume and VWAP matched Kalshi's trades exactly (5.16) |
| 5.2 | Analyzer runner and `market_metrics` | due check, per-analyzer lock, timeout, run history, change counter; 204,000 markets refreshed in 10 to 15 s, only changed rows written (5.17) |
| 5.3 | Screener | `GET /v1/screener`, keyset paging, refresh age, `status` analytics line; top markets agreed with Kalshi's volumes (5.18) |
| 5.4 | Watchlists, alerts, stored time zone | five alert kinds, edge-triggered with cooldown, gaps skip; first writing endpoints, scoped to the token's user (5.19) |
| 5.5 | Push | Server-Sent Events, resume by id, 10 streams per user and 250 in all, reconnecting client (5.20) |

Found only by the live checks, none by the fakes: the alerts analyzer took 34 to 39 s a run until the tick lookups were written with the ids in the statement (0.5 s after); a temporary table made by a command with
parameters is dropped when the command ends; two tests of one class shared rows (CI ordering). The standing question for each slice stays "what would the real service do that my fake cannot?".

Running on `kaldb` at close: ingest `host-84f4848`, API `host-327d431`; status reports no problems; the analyzers run every 5 minutes (metrics) and 10 seconds (alerts).

Carried forward, none in M5: persisted deltas, live add/remove of watched markets, WebSocket push (for two-way needs), a desktop notification or sound (with the UI, M7), per-user layouts (M7), mDNS discovery. Parked items
stay in PLAN.md section 9.

## 8. M6 Trading (started 2026-10-20)

Plan: PLAN.md section 6.7 (approved 2026-10-20). Nothing in this milestone has sent, or can yet send, a write: the transport stays GET-only until slice 6.4, and slices 6.1 to 6.6 are built and tested
without touching the real account except to read it.

### 8.1 Slice 6.1: what the account looks like (2026-10-20)

Measured with the read-only production key, GET only, names and kinds of fields only (the live test `LiveAccountShapeTests` prints no balance, price, count or id), and checked against Kalshi's docs.

**Docs facts re-verified today** (all agree with the reference's 2026-10-13 findings):
- Create Order V2 is `POST /portfolio/events/orders`; body `ticker`, `side` (`bid` buys YES, `ask` sells YES; there is no yes or no field), `count` (fixed-point string, 0 to 2 decimals), `price` (fixed-point dollars,
  2 to 4 decimals), `time_in_force` (`fill_or_kill`, `good_till_canceled`, `immediate_or_cancel`; required), `self_trade_prevention_type` (`taker_at_cross`, `maker`; required), optional `client_order_id`,
  `expiration_time` (good-till-cancelled only), `post_only`, `cancel_order_on_pause`, `reduce_only` (rejected unless immediate-or-cancel), `subaccount`, `order_group_id`, `exchange_index`. Default cost 10 tokens;
  non-default costs come from `GET /account/endpoint_costs`. The 201 reply has `order_id`, `fill_count`, `remaining_count`, `ts_ms`, optional `client_order_id`, `average_fill_price`, `average_fee_paid`.
- New since the reference: **`exchange_index` is optional and auto-routes by ticker** when omitted (`-1` requires auto-routing). Whether a shard that holds no collateral then accepts the order or rejects it
  is not documented: a question for the live slice (6.7), asked of the real account only with the owner's go.
- Balance: `GET /portfolio/balance` takes optional `exchange_index` and `subaccount`; `balance` and `portfolio_value` are cents (int64), `balance_dollars` a string, `updated_ts`, and `balance_breakdown` one row per
  shard with `balance` as a dollar string (no per-shard portfolio value). One call a second on the Basic tier: react to fills, do not poll.
- Positions: `market_positions` items have `ticker`, `exchange_index`, `total_traded_dollars`, `position_fp` (negative means NO), `market_exposure_dollars`, `realized_pnl_dollars`, `fees_paid_dollars`,
  `last_updated_ts`; `event_positions` items have `event_ticker`, `total_cost_dollars`, `total_cost_shares_fp`, `event_exposure_dollars`, `realized_pnl_dollars`, `fees_paid_dollars`; query `settlement_status`
  (`unsettled` default, `settled`, `all`), `count_filter`, `ticker`, `event_ticker`, `exchange_index`, `limit` up to 1000, `cursor`.

**Shapes seen on the account** (HTTP 200 unless noted):
- `/portfolio/balance`: `balance`, `balance_dollars`, `portfolio_value`, `updated_ts`, and a `balance_breakdown` of **four** shards (`exchange_index`, `balance` as a string).
- `/portfolio/positions`: `cursor`, `market_positions`, `event_positions`; both lists are empty on this account (no open positions), so the populated shape is the docs' above.
- `/portfolio/orders`: `cursor` and `orders`; an order has `order_id`, `client_order_id`, `ticker`, `exchange_index`, `status`, `type`, `action`, `side`, `book_side`, `outcome_side`, `yes_price_dollars`,
  `no_price_dollars`, `initial_count_fp`, `fill_count_fp`, `remaining_count_fp`, `maker_fees_dollars`, `taker_fees_dollars`, `maker_fill_cost_dollars`, `taker_fill_cost_dollars`, `self_trade_prevention_type`,
  `created_time`, `last_update_time`, `subaccount_number`, `user_id`.
- `/portfolio/fills`: a fill has `fill_id`, `trade_id`, `order_id`, `ticker` and `market_ticker`, `exchange_index`, `action`, `side`, `book_side`, `outcome_side`, `yes_price_dollars`, `no_price_dollars`,
  `count_fp`, `fee_cost` (a string), `is_taker`, `created_time`, `ts`, `subaccount_number`.
- `/portfolio/settlements`: `ticker`, `event_ticker`, `exchange_index`, `market_result`, `yes_count_fp`, `no_count_fp`, `yes_total_cost_dollars`, `no_total_cost_dollars`, `revenue`, `value`, `fee_cost`, `settled_time`.
- `/account/endpoint_costs`: `default_cost` and 17 entries of `method`, `path`, `cost`. `/account/limits`: `usage_tier`, `read` and `write` each with `bucket_capacity` and `refill_rate`, and `grants`.
  The write bucket is therefore read from the account at run time, not assumed.
- **`GET /portfolio/events/orders` is HTTP 404**: the V2 create route has no V2 list route. Orders are listed with the older `GET /portfolio/orders` (the docs call that path deprecated no earlier than
  2026-05-06; it still answers). The lookup after an unclear create answer depends on this list route.

**Open questions for the slices that follow.**
1. 6.4: how to find an order by `client_order_id` after an unclear answer if the list route changes or goes. (The list answers with `client_order_id` per order; whether it can be filtered by it is to be measured, so
   the lookup may have to page by time as the reference does.)
2. 6.2 and 6.7: whether auto-routing by ticker makes the shard transfers unnecessary, or whether an unfunded shard rejects the order (and how the rejection looks).
3. 6.3: the populated positions shape is taken from the docs; the first real position (slice 6.7) confirms it.
4. 6.5: the private WebSocket channel names and message shapes are not measured yet (they need a signed WebSocket and, to see data, activity on the account); 6.5 reads them from the docs and checks them in 6.7.

### 8.2 Slice 6.2: the risk layer (2026-10-20)

- New project `KalTerm.Trading` (references only `KalTerm.Core`; nothing on the server references it) with `Risk/`: `RiskChecker.Check(intent, snapshot, policy, now, production)`, a pure function, and the types around it
  (`OrderIntent`, `AccountSnapshot`, `ShardState`, `MarketState`, `RiskPolicy`, `Verdict`, `Refusal`, `Transfer`). Money is `long` millionths and counts `long` hundredths; the fee is `KalTerm.Core`'s exact quadratic
  model. Nothing in it does I/O, so nothing in it can touch the account.
- Rules, in the reference's order, all of them run so the person sees every reason: production switch, fee, snapshot (halted, stale or future-dated beyond 5 s, stream down), shard (unknown, not trading), per-order size
  and worst-case cost, per-market position (resting orders counted as if filled), capacity (open orders, exposure), daily loss (also trips the kill switch), funding. A missing snapshot stops after the production switch
  and the fee. The worst-case fee is taken at the worst price the limit allows (the fee peaks at 0.50) and rounded up to a cent; a sell locks only what it does not already hold; the fee of an amount so large it does not
  fit is an unknown fee, which refuses, never a crash. A refusal never hands out the transfer plan that broke a cap.
- Funding: the smallest transfers from the richest shards that can transfer, whole centicents, never more than a source holds, bounded by the per-order and per-day caps.
- `ApprovedOrder` has no public constructor and its internal one is called only by the checker; a test scans the sources to keep it so and another asserts no assembly is given `InternalsVisibleTo`.
- `risk.json` (`%APPDATA%\KalTerm`, via `RiskPolicyLoader`): the only way to change a limit. A missing file means the tiny defaults (order count 5, position 10, order notional $1, exposure $3, daily loss $2, 3 open
  orders, transfers $2 an order and $5 a day, snapshot 10 s, production off). Money and counts are strings (or whole numbers), never JSON fractions; an unknown or duplicate key, a negative value, too many
  decimals or a comment is an error naming the key. Differences from the reference: JSON instead of TOML (no new dependency), and no browser-UI timers.
- Tests (1,630): the reference's own cases and the rules one by one; a property test with the limits re-derived independently, 45,000 random scenarios over three seeds, that no approved order ever exceeds one;
  and **1,544 differential scenarios** (`tools/vectors/generate_risk.py` runs the Python `check` on hand-made and seeded random cases) where the port agrees on approval, every refusal code and message in order, fee,
  cost, funding and kill trip. The one deliberate difference is the file name in the production message.
- Still open for later slices (BUILD-LOG 8.1): whether Kalshi deducts resting-order collateral from a shard balance, and whether auto-routing makes the transfers unnecessary (both asked of the real account in 6.7).

### 8.3 Slice 6.3: the account read side and the trading key (2026-10-20)

- `KalTerm.Kalshi` gains the portfolio models (`AccountBalance`, `ShardBalance`, `MarketPosition`, `EventPosition`, `PositionsPage`, `Order`, `OrdersPage`, `Fill`, `FillsPage`, `Settlement`,
  `SettlementsPage`, `QueuePosition`) and the reads on `KalshiRestClient`: `BalanceAsync` (optional shard and subaccount), `PositionsPageAsync` / `IteratePositionsAsync`, `OrdersPageAsync` /
  `IterateOrdersAsync` / `OrderAsync`, `FillsPageAsync` / `IterateFillsAsync`, `SettlementsPageAsync` / `IterateSettlementsAsync`, `QueuePositionsAsync`. They are plain GETs through the same guarded path
  (signed, paced, retried only as before); the client still cannot send anything but a GET. Without a signer an account read is refused up front with a message, spending no request.
- Money and counts are parsed exactly (`Dollars`, `Contracts`); a field with more decimals than the scale is refused, never rounded. The `balance` field is `balance_dollars` cut to whole cents (so it loses up
  to 99 hundredths of a cent): the dollar value and the per-shard rows are the ones to use. Kalshi keeps every order on the YES book (`side` is always `yes`); the person's intent is `action` with
  `outcome_side`. Unknown fields are ignored; the shape tests fail if a payload (the reference's fixtures, whose field sets were measured on a live account) has a field the model lacks.
- The trading key (new `KalTerm.Trading/Keys`): `CredentialManagerKeyVault` keeps one Ed25519 key per environment (`demo`, `production`) as a generic credential in the signed-in user's Windows
  Credential Manager (`KalTerm/trading-key/<environment>`; the user name holds the Kalshi key id, the secret the PEM), through `advapi32` (`CredWriteW`, `CredReadW`, `CredDeleteW`). It is the user's own vault:
  not a file, not in the repository, not on the server, unreadable by the server's service accounts. `TradingKeyLoader.Store` refuses anything that is not an unencrypted Ed25519 PKCS#8 PEM, or an unusable key
  id or environment, before it is stored; `Load` builds a `TradingSigner` (the signer plus the key it owns, disposed together). The data key keeps its own path (environment variables and a file), so the two
  keys cannot be mixed up. An entry's text never contains the key.
- Tests: the models and the reads against a scripted handler (balance per shard and narrowed, positions with a negative long-NO, empty accounts, cursors, one order, queue positions, signing, no signer), the
  Credential Manager vault against the real Windows API under a name of the test's own (round trip, replace, case, too large, never leaves a credential behind), the loader (store, sign and verify, refusals).
- Live (read-only key, GET only, counts and yes or no only): the typed reads parsed the real account. Four shards; their rows add up to the total and the cents field is the dollars cut to whole cents; one shard
  asked for alone equals its row; positions (settled included, 6 market and 4 event positions on the first page) parsed, so the populated shape is confirmed; 2 orders, all executed, each with a yes price plus a
  no price of a dollar and ordered = filled + remaining; 20 fills on the first page, each with prices adding to a dollar, a positive count and a fee of zero or more; one order read by its id equals the same
  order in the list; 6 settlements; no resting orders. That closes open question 3 of 8.1 for the fields seen.
- Still open: the keyless questions of 8.1 (list route, private WebSocket shapes) belong to 6.4 and 6.5; no trading key exists yet (the owner creates one at Kalshi before 6.7).

### 8.4 Slice 6.4: the write transport (2026-10-20)

- New `KalTerm.Trading/Transport`. `KalshiTradingClient` is a separate client from the read client (which stays GET-only), built only from a `TradingSigner`. It refuses production unless `TradingClientOptions.AllowProduction`
  is set (the order service will set it only when `risk.json` enables production) and refuses a key for the other environment. Its HTTP pipeline starts with `RouteGuardHandler`, which allows exactly the routes of
  `TradingRoutes` (the reference's list: create, cancel all, batch cancel, cancel one, amend, decrease, the shard transfer, and `GET /portfolio/orders` for lookups) and throws `TradingRefusedException` for anything
  else before anything is sent.
- Payloads are built from integers: `FixedPointText.Count` (two decimals) and `Price` (four; a finer price is refused, never rounded). `OrderRequest.From(ApprovedOrder, clientOrderId)` is the only mapping from the
  risk layer: every order goes on the YES book (buy YES is a bid, sell YES an ask, buy NO an ask at the complement price, sell NO a bid at it). A client order id is required; reduce-only needs IOC; an expiration is for GTC only.
- Retry rules, from the reference: a 429 is waited out (0.5, 1, 2 s, then an API error) for every write. A create is sent once; after a transport error, a timeout or a 500, 502, 503 or 504 (or a 409) the order is looked
  up by `client_order_id` (`GET /portfolio/orders`, `min_ts` five seconds before the attempt, limit 1000, cursors followed) now and after each pause, and returned with `Recovered = true` if found, otherwise
  `WriteOutcomeUnknownException` (a 409 with no order found stays a 409). A definite 4xx is final. Cancel, batch cancel and cancel-all are idempotent and are retried after unclear answers (a 404 on a retry means done); amend, decrease and
  the transfer are never resent (unknown outcome). The transfer is in centicents and a positive whole number of them, source and destination must differ.
- Costs: each attempt spends its tokens from the write bucket (default 10, cancel 2, batch cancel 2 per order, chunked to what the bucket holds). The figures are options; reading them and the tier's rate from
  `/account/endpoint_costs` and `/account/limits` belongs to the order service (6.5), which knows the account.
- Audit: every attempt and its answer goes to an `ITradingAudit` (`JsonLinesAudit`: one JSON line per event, written through to disk). It carries times, routes, ids and statuses, never a key or a signature.
- Tests (about 100 new, all against a scripted fake, nothing sent anywhere): route allow and refuse tables, construction guards, exact payloads, the mapping of intents, every ambiguous-answer path, cursors, 429, cancels,
  amend, decrease, transfer, token costs from the bucket, the audit. `KalTerm.Trading` is at 96.8% lines.
- Nothing in 6.4 reaches the real Kalshi; no rollout is needed (the server does not run trading code).

### 8.5 Slice 6.5a: the account tracker and the trading state (2026-10-20)

Slice 6.5 is split in two so each PR stays reviewable: 6.5a is what the order service reads (this section), 6.5b is the service itself.

- `KalTerm.Kalshi` gains the typed private-channel messages `FillMsg`, `UserOrderMsg` and `MarketPositionMsg` (envelope types `fill`, `user_order`, `market_position`; channels `fill`, `user_orders`,
  `market_positions`), parsed exactly like the REST models: finer than six decimals for money or two for counts is an error, never a rounding.
- `TradingState` (`trading_state.json` next to `risk.json`, atomic temp-and-move writes): the UTC day, the day's realized-profit baseline, the day's transferred total, the kill-switch halt, client order ids
  whose outcome is unknown, and transfers begun and not confirmed (counted before the request, taken back only if Kalshi definitely refused). A new day resets the counters but not the halt or the unresolved items.
  A file that exists and cannot be read is an error (`TradingStateException`), never a fresh start, because a reset would erase a halt.
- `AccountTracker` follows the reference: Kalshi's private channels send no snapshot and no sequence numbers, so after subscribing and after every `reconnected` message it re-reads status, balance, positions
  and resting orders over REST, and until that completes the snapshot says the stream is not OK (the risk layer refuses). Messages that arrived meanwhile are applied afterwards; they are idempotent (an order message
  replaces the order only if not older, a fill counts once by trade id, a position message replaces the market's position and keeps figures it does not carry). The balance is polled every 5 s and at once after a fill;
  if polling fails the snapshot ages and the risk layer refuses as stale. An unreadable message stops the stream and fails closed, with the reason in `Fault`. The snapshot subtracts what resting orders may lock from each
  shard's gross cash (a resting order does not reduce Kalshi's balance, measured live in the reference).
- Tests (about 50 new, scripted REST and socket, nothing sent anywhere): the reference's account and state cases, plus the message parsing and precision refusals in the Kalshi tests. `KalTerm.Trading` is at 96.3% lines.
- Not checked against the real service: the private channels' wire shapes come from the reference's live measurements; the first look at them with the real trading key is part of 6.7 (a subscribe is a command,
  outside the read-only GET permission, so it waits for the owner's go).
- No rollout is needed (the server does not run trading code).

### 8.6 Slice 6.5b: the order service and the account's write budget (2026-10-20)

- `OrderService` (new `KalTerm.Trading/Orders`) is the one path from a wish to an order, ported from the reference. `PrepareAsync` is read-only: it asks Kalshi for the market (shard, status) and the series' fee multiplier
  (cached per event), refreshes the balance and asks the risk layer; the `Prepared` it returns, with `Describe` lines, says exactly what would happen including shard funding. `ExecuteAsync` re-checks with fresh data
  (`PlanChangedException` if the cost rose or the funding differs from what was shown), moves the money (`BeginTransfer` saved before the request, `FinishTransfer` after; a definite refusal is taken back out of the day's total,
  an unclear answer stays pending and stops the order), waits up to ten seconds for the balance to show the transfer (it lags, seen live), checks once more and only then sends the order built from the `ApprovedOrder`.
  An unclear create answer saves the client order id in the trading state. Prices finer than four decimals fail in prepare, before any money moves.
- Mapping to the YES book: buy YES is a bid, sell YES an ask, buy NO at q an ask at 1 - q, sell NO a bid at 1 - q. An amend is checked as the replacement with the old order left out of the account, takes the new total count, and a
  partly filled order may change its price only. Kalshi's action and outcome labels are never used to rebuild an order.
- Kill switch: halt first (saved, so a restart cannot undo it), cancel all, then list resting orders and batch-cancel what is found, up to ten passes a second apart; done only after two empty looks. Errors are reported, never
  hidden, and a listing that keeps failing is never "clean". The daily-loss guard (`GuardOnceAsync`, `StartGuard`) fires the same kill switch when the day's realized loss reaches the limit, and an order attempted over it trips it too.
  Cancels are never refused by the risk layer and work while halted; `CancelByIdsAsync` needs no tracker (it reads Kalshi's own record of each order). `TypedPhraseMatches` is the production confirmation check
  (`TICKER buy|sell yes|no COUNT PRICE`, numbers compared as numbers, ticker case-sensitive).
- The write budget is read from the account: `KalshiRestClient.EndpointCostsAsync` and `AccountLimitsAsync` (GETs) and `TradingBudget.ReadAsync` turn them into the bucket (the tier's rate and capacity times the margin) and the costs
  the trading client uses. Live (read-only key): the tier is basic, the write bucket holds 100 and refills 100 a second, the read bucket holds 600 and refills 200; cancels (one, all, batch) cost 2; create, amend, decrease and the
  transfer are not listed, so they cost the default of 10; the paths are written `/trade-api/v2/...` with `:order_id`. The budget takes the highest listed cost of the order routes and of the cancel routes.
- Tests (about 70 new): the reference's order-service cases (mapping, fees, funding, plan change, transfer outcomes, lagging balance, halts, kill switch passes and errors, guard, amend rules, cancel by id, typed phrase), against a
  scripted exchange that moves money and orders; the budget against the real response shape. `KalTerm.Trading` is at 95.0% lines.
- No rollout is needed (the server does not run trading code). Still nothing has been sent to Kalshi by this code.

### 8.7 Slice 6.6: the CLI (2026-10-20)

- `kterm` gains the trading commands (new `TradingCommands`, `TradingViews`): `buy`, `sell` (`--side yes|no`, `--ioc`, `--post-only`, `--dry-run`, `--yes`), `amend` (`--price` as the YES price, `--count` as the new total), `cancel`,
  `kill`, `resume`, `balance`, `positions [--all]`, `orders [--status resting|canceled|executed|all] [--limit]`, `fills`, and `trading-key set|show|delete`. They talk to Kalshi directly and never to the KalTerm server; `kterm` now references
  `KalTerm.Trading` (the server's projects still do not).
- What a command does: it opens a session for the mode it needs (`TradingSessions.OpenAsync`: Read, Preview, Exit, Trade, as in the reference), prepares the order, prints exactly what would happen (the worst-case cost, any shard transfer,
  every reason for a refusal), and stops there for `--dry-run`, which opens the Preview mode whose trading client refuses every write, so a dry run cannot send. A demo order asks `y/N` (`--yes` skips it). A production order is only sent after
  the order is typed out (`TICKER buy|sell yes|no COUNT PRICE`); `--yes` does not skip that; with no input available nothing is sent. Cancels, `kill` and `resume` are never risk-checked and use Exit mode: the real trading client with no
  account stream, allowed in production without the risk file's say-so (getting out is always allowed). Orders and amends in production need `production_enabled` in `risk.json`.
- Exit codes: 0 done, 1 refused or failed (nothing happened that you did not see), 2 a write whose outcome is unknown (`UNKNOWN:`, naming the id to look for in `kterm orders --status all` and `kterm fills`). Failures a person can act on are one
  line each (refusal reasons, plan changed while deciding, a refused or unknown transfer, funding that has not arrived, a Kalshi error status, no connection, a missing key).
- Session wiring: the write bucket and costs come from the account (`TradingBudget`), falling back to the settings' tier budget and the default costs if Kalshi cannot be asked; every write goes to `audit.jsonl` next to `risk.json`; a failure
  while opening closes what was already opened.
- `trading-key set --env demo|production --key-id ID --pem-file FILE` checks and stores the key in this Windows account's Credential Manager and prints only the last four characters of the id; `show` never prints the key. This is how the
  owner stores the trading key before 6.7.
- Tests (about 110 new): the commands against the real order service on a scripted exchange (dry run, demo and production confirmation, refusals, funding, unknown outcome, amend, cancel, kill, resume, every view and its JSON, the key commands),
  and the session factory with the real Kalshi clients against scripted HTTP (budget from the account, an order placed end to end with the audit written, preview cannot send, production refused unless enabled, Exit allowed). The shared fakes
  moved to `KalTerm.TestSupport`. `KalTerm.Trading` is at 94.8% lines and `kterm` at 96.8%.
- Live (read-only key, GET only; shapes and exit codes only, no values): `balance`, `positions` (and `--all`), `orders` (and `--status all`) and `fills` ran against the real account, exit 0 each, with the expected tables; the balance JSON has
  `balance`, `portfolio_value`, `shards` (each `shard`, `available`) and `halted`. Nothing was sent.
- No rollout is needed (the server does not run trading code). Still nothing has been sent to Kalshi.

### 8.8 Slice 6.7: live on the real account (2026-10-20)

Owner decision: development and testing use Kalshi production only, with a separate read-write trading key (Credential Manager) beside the read-only data key; the demo site is not used. Every write below was run by the owner from `kterm`
after being shown the exact order and saying "go"; the audit log (`audit.jsonl`) holds the chain for each. The server was not involved: the trading commands ran while no KalTerm API was listening on this computer, which is the
server-offline test.

| Step | What was sent | Result |
|---|---|---|
| 1 | Buy 1 YES at $0.01, post-only, on a shard-0 market far below the best bid | Placed (201); listed as a resting BID, YES $0.0100, NO $0.9900. A buy of YES is a bid on the YES book, as designed |
| 2 | Amend that order to $0.02 | Same order id, now BID $0.0200 with 1 left. The amend reply carries no remaining count we read (`resting -`), so the order list is the source of truth |
| 3 | Cancel it | Reply 200, "cancelled 1 left". Kalshi's resting list kept showing it for several seconds, then dropped it |
| 4 | Buy 1 YES at $0.01, post-only, on a shard-3 market with all money on shard 0 | The plan showed the $0.0200 transfer 0 to 3; sent once, shown in the balance at once; the order rested. Total unchanged |
| 4b | Cancel it, timing the list | The list was already empty on the first look, 1.9 s after the cancel |
| 5 | Place the shard-3 order again, then `kill` | Halted, cancel-all took the order (follow-up passes cancelled 0), book empty, 2.8 s in all; a dry-run order was then refused as halted; `resume` cleared it |
| 6a | Buy 1 YES at $0.03 IOC (taker) | Filled 1 at $0.03; fee $0.0021; position 1 YES, exposure $0.0300; the balance fell by exactly cost plus fee |
| 6b | Sell 1 YES at $0.02 IOC | Filled 1; fee $0.0014; position flat, realized -$0.0100, fees $0.0035; the balance fell by $0.0135 in the round trip |

- Findings. (1) A resting order does not reduce a shard's balance: the balance is gross cash (shard 3 still showed the full $0.0200 with the order resting), which is what the tracker assumes. (2) Kalshi's resting-order list can lag a cancel by
  seconds (not at all in 4b, several seconds after step 3), so the kill switch's rule of two empty looks a second apart within ten passes is right, and a lag longer than ten seconds would be reported, not hidden. (3) The real taker fee
  is the modelled fee rounded up to $0.0001 ($0.002037 became $0.0021, $0.001372 became $0.0014, $0.000693 became $0.0007), matching the fee model and the balance's alignment to $0.0001; the risk layer's worst-case bound (up to $0.01) is
  conservative. (4) Realized profit on a position excludes fees, which are reported separately. (5) A phrase typed wrongly at the production prompt (a pasted command) was refused with nothing sent.
- Not answered, parked: whether Kalshi's auto-routing makes shard transfers unnecessary. The risk layer refuses an order its shard cannot fund, so it cannot be tested without bypassing the layer.
- The $0.02 moved to shard 3 stays there (no command moves money back; Kalshi's web app can).
- Open small items: show the remaining count after an amend from the order list; the confirmation prompt reads one line, so a pasted command is refused rather than sent (working as intended).
