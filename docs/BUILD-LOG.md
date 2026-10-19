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

**What this means for the schedule (to decide).** One pass costs about 25 to 32 s of Kalshi listing plus about 6 s of database work, sequentially, so roughly 30 to 38 s, and about
122 requests. The 30-second windows are therefore about one pass long: the loop will run back to back for ten minutes an hour (a missed tick is skipped, never queued), which is a
sustained 3 to 4 requests a second against Kalshi's read limit and about twice the database work of a 5-minute pass. Options are listed in the 3.3 report; none is built.

**Problems and fixes.**
- A `Dollars` helper in the test kit shadowed the `Dollars` type in another test file; removed.
- `MarketDiscovery` now checks the cancellation token itself on every market instead of trusting the source to.
- Shell heredocs with embedded quotes failed again; files were written with the editor tool.
