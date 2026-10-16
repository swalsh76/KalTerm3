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
