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
