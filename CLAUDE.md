# CLAUDE.md

## Start here

**Read [docs/PLAN.md](docs/PLAN.md) before doing anything.** It is the source of truth for
architecture, tooling, and build order. How each slice was built and verified goes in
[docs/BUILD-LOG.md](docs/BUILD-LOG.md). Update both when decisions change.

KalTerm is a greenfield rewrite. The Python/Postgres predecessor (KalshiTerm2, `swalsh76/KalshiTerm2`)
is the **reference implementation**: its docs are the specification, and its tests and fixtures are the
oracle. No code is shared.

- Local clone of the reference: `C:\Users\stebe\dev\KalshiTerm2` (read it; do not edit it from here).
  Fixtures and tests live under `packages\kalshi-core\tests`, measurements in `docs\BUILD-LOG.md` §10.2.
- Longer list of lessons (durability, unattended operation, backups, testing):
  `C:\Users\stebe\dev\KalshiTerm2\docs\windows\CLAUDE.md`.

## Status

M0 and **M2 Core are done** (BUILD-LOG §4). Spikes S-3, S-1a and S-1b are done; S-2 and S-4 are not
started. Do not start M3 until S-1d (real power cuts) has passed on the target server (PLAN.md §6.1). S-1a found that this workstation's consumer NVMe acknowledges write-through writes
from volatile cache (BUILD-LOG §2.2).

## Non-negotiables

- **Windows only**, by design. .NET 10, C#, SQL Server 2025. No Docker, WSL or Linux VM in the product.
- **Trades go client to Kalshi directly, never through the server.** The trading key never leaves
  the workstation; the server has its own data-only key. In single-box mode the server's service
  accounts must not be able to read the user's profile or Credential Manager.
- **Demo is the default** in code and config; production is an explicit opt-in. The read client is
  GET-only (guard and test). The write path is a separate trading client with the separate trading
  key, refused in production unless enabled in the risk config, limited to a fixed list of order
  routes. Every order passes the risk layer and, in production, a typed confirmation.
  **Get the user's go-ahead before sending any real order.**
- The server is multi-user: named users, per-user tokens, per-user watchlists, alerts and layouts.
- **The API is the only exposed port.** SQL Server listens on shared memory only. Self-signed TLS
  with trust-on-first-use pinning.
- Storage budget **500 GB**, enforced by the storage governor and a hard `MAXSIZE` on the data file.
- Money is fixed-point `long`: dollars at 10^-6, counts at 10^-2. Never a float or `double`.
- MIT license. The repository is **public**: never commit secrets, keys, product keys, `.env` files
  or personal data. Commit with the GitHub no-reply address.
- Never hard-code `localhost` or Kalshi URLs outside the core configuration.
- The owner's name is **Steven** Walsh.

## Scope discipline

KalshiTerm 1 died of feature creep and directional changes. Guard against it:

- Build only the current milestone in PLAN.md. Anything else is out of scope until the user says so.
- If a request or idea is not in the plan, **say so explicitly** ("this is scope creep: ...") and ask
  whether to add it to the plan, defer it, or drop it.
- Architecture or direction changes go into PLAN.md first (with approval), then code.
- Park deferred ideas in PLAN.md §9 (Parking lot), never in code or TODO comments.
- Prefer the smallest thing that satisfies the milestone. Work on a branch; merge when the user approves.
- Describe an action and its effects before asking for approval. When live-testing against market
  data, repeat the data in the visible reply.

## Lessons carried over from KalshiTerm2

- **Durability first.** A hard power cut corrupted Postgres on Docker for macOS because flushes
  reached only the drive cache. Measure the flush path, then power-cut real hardware under write
  load and check integrity. Keep `DELAYED_DURABILITY = DISABLED` and `PAGE_VERIFY = CHECKSUM`.
- **Unattended operation.** Never design a step that can pop a dialog on an unwatched console. Bound
  every wait, log what was skipped, and refuse to proceed when a prerequisite is missing. Test
  services as their own accounts, non-interactively and after a reboot.
- **Backups.** The backup target carries a marker file. A restore is not proven until it is restored
  into a new database and compared with a row-count manifest.
- **Downtime is lost data** for tickers and order books (not recoverable); trades are backfilled and
  recorded in a gap log. `status` must say so.
- Ed25519 keys only. Combos are stored as counters plus a large-trade log, not per-combo rows.
- Differential-test any behavior-preserving port against the Python reference; contract tests for
  every model crossing the API.

## Conventions

- Solution and namespaces use the prefix `KalTerm`. `src/` and `tests/` layout, central package
  management, nullable enabled, warnings as errors, `.editorconfig` enforced.
- Tests: xUnit v3 on Microsoft Testing Platform. Test methods use plain PascalCase, no underscores
  (CA1707 is an error), e.g. `VersionIsNotEmpty`.

## Dev commands

```powershell
dotnet restore KalTerm.sln
dotnet format KalTerm.sln --verify-no-changes
dotnet build KalTerm.sln -c Release
dotnet test --solution KalTerm.sln -c Release --coverlet --coverlet-output-format cobertura --report-trx --results-directory TestResults
```

Regenerate golden vectors from the Python reference (only when the reference changes; the output is
committed and CI never runs Python; see `tools/vectors/README.md`):

```powershell
python tools/vectors/generate_fixedpoint.py --reference C:\Users\stebe\dev\KalshiTerm2
python tools/vectors/generate_fees.py --reference C:\Users\stebe\dev\KalshiTerm2
python tools/vectors/generate_ratelimit.py --reference C:\Users\stebe\dev\KalshiTerm2
python tools/vectors/generate_config.py --reference C:\Users\stebe\dev\KalshiTerm2
python tools/vectors/generate_ed25519.py --reference C:\Users\stebe\dev\KalshiTerm2   # needs: pip install cryptography
python tools/vectors/generate_clock.py --reference C:\Users\stebe\dev\KalshiTerm2
python tools/vectors/generate_models.py --reference C:\Users\stebe\dev\KalshiTerm2   # needs: pip install pydantic
python tools/vectors/generate_ws_models.py --reference C:\Users\stebe\dev\KalshiTerm2   # needs: pip install pydantic
```

Opt-in live tests against the real Kalshi API (read-only key, GET only, about a dozen requests, skipped by default and in
CI). **The owner granted standing permission (2026-10-16) for the assistant to run them itself**, within these limits:
GET only and never an order; the key id and path come from the owner's User environment variables
`KALSHI_KEY_ID` / `KALSHI_PRIVATE_KEY_PATH`, loaded into each command's own process and removed afterwards; never print or
read the key id, the key path or the key file (check it exists with `Test-Path` only); delete `live-summary.txt` after reading
it; and repeat what the run read in the visible reply (public market data and status codes, never an account value). A
summary lands in `live-summary.txt` next to the test assembly:

```powershell
dotnet build KalTerm.sln -c Release
$env:KALSHI_INTEGRATION='1'; $env:KALSHI_ENV='production'   # plus KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH
.\tests\KalTerm.Kalshi.Tests\bin\Release\net10.0\KalTerm.Kalshi.Tests.exe --filter-class "*LiveKalshiTests"
# the WebSocket live tests (public channels; subscribe commands only):
.\tests\KalTerm.Kalshi.Tests\bin\Release\net10.0\KalTerm.Kalshi.Tests.exe --filter-class "*LiveKalshiWebSocketTests"
# one test only, for example the signed-account-read check:
.\tests\KalTerm.Kalshi.Tests\bin\Release\net10.0\KalTerm.Kalshi.Tests.exe --filter-method "*TheServerAcceptsOurSignatureOnAnAuthenticatedRead"
```

(Run the test assembly directly: `dotnet test --filter-class` would also hit the other test projects, which have no such class.)

CI fails if any production assembly is under 90% line coverage, judged on the union of all test
projects' reports (coverlet.MTP's own threshold option is unsupported; `KalTerm.TestSupport` is test
infrastructure and not judged). To check locally after the test command above:

```powershell
powershell -NoProfile -File tools/ci/check-coverage.ps1 -Path TestResults -MinLinePercent 90
```
