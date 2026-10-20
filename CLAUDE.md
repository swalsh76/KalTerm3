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

M0, **M2 Core, M3, M4, M5 and M6 are done** (BUILD-LOG §4 to §8; M6 Trading was verified live on the real account under tiny limits; M7 UI is decided later and M8 Host layer is next; **no real order is sent without the owner's go-ahead**). All M1 spikes are done (BUILD-LOG 2.1 to 2.7); S-1d (real power cuts) passed on the target server, so M3 may start. The
spike proposals in PLAN.md 6.3 and the M3 decisions D1 to D8 (6.4) are approved; M3 reads production market data with the
read-only key, and a long ingest soak needs the owner's fresh go-ahead. S-1a found that this workstation's consumer NVMe acknowledges write-through writes
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
- Live tests (against the real Kalshi API) are run by the assistant without asking (standing permission, limits in Dev commands). Also write and run them where a fake cannot show real behavior (boundaries, units, precision, ordering, pagination, rate limits, timestamps): the slice 4.2 live check found that `max_ts` is exclusive, a bug no synthetic test could surface. Design each slice with the question "what would the real service do that my fake cannot?"
- Tests never use a real host, share or account name (the repository is public, and a test that does file I/O on a path that exists on the developer's network is a write to production storage). Use `\nasackups\...` as text and `\127.0.0.1\kalterm-no-such-share\...` where a path is really touched.

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
python tools/vectors/generate_api_format.py --reference C:/Users/stebe/dev/KalshiTerm2
python tools/vectors/generate_risk.py --reference C:/Users/stebe/dev/KalshiTerm2   # the risk layer: loads kalshiterm_client/risk.py by path, needs only kalshi_core
python tools/vectors/generate_orderbook.py --reference C:/Users/stebe/dev/KalshiTerm2   # needs: pip install pydantic pydantic-settings cryptography httpx websockets
```

Run the ingest process (demo by default; `KALSHI_ENV=production` plus the key variables for production, and `KALTERM_Ingest__ProvisionMaxSizeGb=<n>` to let it create the database):

```powershell
dotnet run --project src/KalTerm.Ingest.Host -c Release
```

Run the read-only API (loopback only until TLS and tokens exist; default `http://127.0.0.1:5280`, database `KalTerm` on `lpc:.`; `KALTERM_Api__Url`, `KALTERM_Api__Server`, `KALTERM_Api__Database` override):

```powershell
dotnet run --project src/KalTerm.Api.Host -c Release
```

Read a KalTerm server with the CLI (`status` exits 2 when it finds problems). Locally against a running API, or on the server through remoting (the API listens on loopback only until M4; pass the host and a credential file, or set `KALTERM_TEST_HOST` and `KALTERM_TEST_CRED`):

```powershell
dotnet run --project src/KalTerm.Cli -c Release -- status
.\tools\kaldb\kterm.ps1 -ComputerName <host> -CredentialPath <file> -- market <TICKER>
```

Read the server from this computer (once per server: trust its certificate, then store your token; the operator makes users and tokens on the server with `kalterm-admin user add` and `token create`):

```powershell
dotnet run --project src/KalTerm.Cli -c Release -- trust kaldb:5280          # compare the fingerprint with `kalterm-admin cert show`
dotnet run --project src/KalTerm.Cli -c Release -- login --api https://kaldb:5280
dotnet run --project src/KalTerm.Cli -c Release -- status --api https://kaldb:5280     # or set KALTERM_API_URL
# times show in the zone stored with `kterm settings tz`, else this computer's zone; choose with --utc, --tz America/Denver or KALTERM_TZ
```

Back up, restore and prove backups on the server (`kalterm-admin`, run on the server through remoting; the share credential is entered once with `-SetShareCredential`):

```powershell
.\tools\kaldb\admin.ps1 -BackupTarget <\\host\share\folder> -SetShareCredential
.\tools\kaldb\admin.ps1 -BackupTarget <\\host\share\folder> -- backup init-target
.\tools\kaldb\admin.ps1 -BackupTarget <\\host\share\folder> -- backup run --full
.\tools\kaldb\admin.ps1 -BackupTarget <\\host\share\folder> -- restore --database KalTerm_check
```

Opt-in live tests against the real Kalshi API (read-only key, GET only, about a dozen requests, skipped by default and in
CI). **The owner confirmed standing permission (2026-10-16, re-confirmed 2026-10-19: "always run them") for the assistant to run them itself, without asking**, within these limits:
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
# market discovery (sizing; the updated-since probe; a full pass then two incremental passes; about 130 requests in all; none touches a database):
.\tests\KalTerm.Ingest.Tests\bin\Release\net10.0\KalTerm.Ingest.Tests.exe --filter-class "*LiveDiscoveryTests"
# one test only, for example the signed-account-read check:
.\tests\KalTerm.Kalshi.Tests\bin\Release\net10.0\KalTerm.Kalshi.Tests.exe --filter-method "*TheServerAcceptsOurSignatureOnAnAuthenticatedRead"
```

Database tests (`KalTerm.Data.Tests`, `KalTerm.Ingest.Tests`) run on LocalDB (`(localdb)\MSSQLLocalDB`). If it is missing they skip on a development machine and fail on CI.
This workstation has no LocalDB, so **run them on the target server before committing database changes**: `tools/kaldb/run-db-tests.ps1 -ComputerName <host> -CredentialPath <cred file>`
(or set `KALTERM_TEST_HOST` and `KALTERM_TEST_CRED`) publishes, syncs by hash and runs them there as SYSTEM.
`KALTERM_TEST_SQL_SERVER` selects another server; to run them on the target server see BUILD-LOG 5.1 (self-contained publish with `-p:NoCoverage=true`, then re-run `dotnet restore`).

(Run the test assembly directly: `dotnet test --filter-class` would also hit the other test projects, which have no such class.)

CI fails if any production assembly is under 90% line coverage (95% for `KalTerm.Core` and `KalTerm.Data`), judged on the union of all test
projects' reports (coverlet.MTP's own threshold option is unsupported; `KalTerm.TestSupport` is test
infrastructure and not judged). To check locally after the test command above:

```powershell
powershell -NoProfile -File tools/ci/check-coverage.ps1 -Path TestResults -MinLinePercent 90
```
