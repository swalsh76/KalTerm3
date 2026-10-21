# KalTerm

A Windows-native trading and analytics terminal for [Kalshi](https://kalshi.com), written in C# on
.NET 10 with SQL Server 2025. Windows only, by design.

**Status:** milestones M0 to M7 are built: an ingest service that records Kalshi's feed into SQL Server, a
read-only API with per-user tokens, pinned TLS, watchlists, alerts and layouts, the `kterm` command line, a
trading layer (risk limits, kill switch, audit log) that trades from the workstation straight to Kalshi, and a
WPF desktop terminal. A review of the whole codebase and then the host layer (installer, services, power-cut
tests, a 48-hour soak) come next. See [docs/PLAN.md](docs/PLAN.md) for the plan and
[docs/BUILD-LOG.md](docs/BUILD-LOG.md) for how each slice was built and verified, and
[docs/TERMINAL.md](docs/TERMINAL.md) for how to use the terminal.

KalTerm is the third attempt at this project. The Python/Postgres predecessor
(KalshiTerm2) is the reference implementation for behavior, fixtures and tests.

No real order is ever sent without the owner's go-ahead, and demo is the default environment.

License: MIT.
