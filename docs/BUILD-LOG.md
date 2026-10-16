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
