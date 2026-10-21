# The KalTerm terminal

The desktop terminal is a Windows (WPF) program for one person's workstation. It shows what the KalTerm server has collected, watches Kalshi live through the
server's push streams, tells you when an alert fires, and, only when you ask, trades on your own Kalshi account **directly from this computer** (never through the server).

```powershell
dotnet run --project src/KalTerm.Terminal -c Release
```

## First start

The first start opens the connect dialog. Type the server's address (`host:port`), compare the certificate fingerprint it shows with the one the operator of the server
gave you (`kalterm-admin cert show` on the server), choose **Trust this certificate**, then paste your API token (`kalterm-admin token create` on the server). The pin
and the token are stored for this Windows user only and are shared with the `kterm` command line. After that the terminal connects by itself.

## The window

- **Top bar:** the server and its state, **Connect…** and **Retry**, the **Layout** box with **Save as…** and **Delete** (see Layouts), and the **Time zone** box. Every time on screen
  is shown in the zone you choose; the choice is stored on the server. The box shows the offset in effect now ("Mountain Time (US & Canada), now UTC-06:00").
- **Pages** (the list on the left): Status, Markets, Alerts, Trading.

## Status

What `kterm status` shows: problems first, then ingest, streams, storage, markets and gaps. Downtime is lost data for tickers and order books, so the page says so.

## Markets

A grid of the server's screener, 50 markets at a time, with live prices for the rows on show.

- **Filters:** search (ticker, name, yes label; two characters or more), status, minimum volume, and **List** (all markets, or one of your watchlists). **Clear filters** puts them back.
  Choosing a list shows every status; going back to all markets restores the status you had.
- **Ranking:** the **Rank by** box and the order button, or click the header of Last, Spread, 24h change, 24h volume or Open interest (a second click flips the direction). The
  server ranks the whole list, not just the 50 rows on show. **Previous** and **Next** page through it.
- **A market:** double-click a row or press Enter. The detail has three tabs: **Overview** (what it is, its metrics, recent ticks and trades), **Chart** (candles with volume, the interval and
  range boxes, a colour legend, hover for exact figures) and **Book** (the live order book, 10, 20 or 50 levels).
- **Right-click a row:** add the market to a watchlist, make a new watchlist, or create an alert. The detail has the same through its **Watchlists ▾**, **Alert…** and **Trade…** buttons.

## Watchlists

Your own lists of markets, stored on the server and visible only to you (up to 20 lists of up to 500 markets). The **Lists** column shows which lists each market is in. **New…**, **Delete**
(asks first) and **Reread lists** (after a change made from another computer or `kterm`) are next to the List box.

## Alerts

Tell me when a market's price, spread, volume or open interest does something.

- **Make one:** the form on the Alerts page, or **Alert…** on a market. Kinds: price above, price below, spread above (dollars), volume spike (a whole factor, 2 to 1000, of the average 15 minutes of the
  day before) and open interest change (contracts). The form says what a usable threshold is and checks it as you type. An alert fires when its condition *becomes* true, not for what was already true when you
  made it; the cooldown (10 to 86,400 seconds, 60 by default) is the least time between two fires.
- **When one fires:** a Windows toast appears (a click brings the terminal forward and opens the market, also when the click starts the terminal). The first three fires of a five-second burst each get a toast and
  the rest become one "N more alerts fired". The **Sound** box silences them. When the terminal starts it says in one toast how many alerts fired since the last one it saw. Toasts only appear while
  the terminal is running; the fires list on the Alerts page (the latest 100) is always there, and the page's **Reread** button refreshes it.

## Trading

Trades go from this computer straight to Kalshi. The trading key lives in this Windows account's Credential Manager (`kterm trading-key set`); the server never sees it.

- **Environment:** demo is the default. To trade the real account start the terminal from a PowerShell window with the variable set for that one process only, never in your user environment:

  ```powershell
  $env:KALSHI_ENV='production'; dotnet run --project src/KalTerm.Terminal -c Release
  ```

  Production orders also need `"production_enabled": true` in `%APPDATA%\KalTerm\risk.json`. The banner says PRODUCTION (red) or DEMO.
- **The page:** the session with Kalshi opens by itself when the terminal starts; a failure (no key, no network) is shown on the page and stops nothing else, and **Connect** tries again. It shows the balance on each
  shard, meters for open exposure, open orders and today's result against the limits in `risk.json` (read-only here: change them only by editing that file), your resting orders (**Change…** and **Cancel**,
  which asks first), positions and fills. If the live account view is not in sync, new orders are refused until it is.
- **An order:** **New order…** (or **Trade…** on a market). **Check** looks the market up and asks the risk layer; it shows the shard, the worst-case cost with the fee, any collateral transfer between shards that
  would come first, and the verdict, and sends nothing. **Send** is possible only after an approved check of exactly what is on the ticket (change anything and the check is thrown away) and, in production,
  after you **type** the order as `TICKER buy|sell yes|no COUNT PRICE` (pasting is blocked). If the cost or funding changed while you were deciding, the ticket says so and you check again.
- **An unclear answer** ("UNKNOWN: …"): it is not known whether the order reached Kalshi. The ticket will not send it again; look for the client order id in Orders and Fills (or `kterm orders --status all`) before doing
  anything else.
- **Kill switch:** the red button asks first, then halts new orders and cancels every open order, checking until none rest. It works without a session too. **Resume** (asks first) allows new orders again.
- **Every write is logged** to `%APPDATA%\KalTerm\audit.jsonl` with its risk verdict and Kalshi's answer.

## Layouts

A layout is how you like the terminal laid out: the window (size, place, maximised), the page open, the Markets filters and ranking (not the search text), the Markets columns (widths and order), the chart
interval and range, the book depth and the Sound box. Layouts are named and stored on the server (up to 20), and the one last used on *this computer* is read when the terminal connects, so a laptop
and a desktop with different screens can each keep their own. The layout in use is saved a couple of seconds after any change and once more as the window closes.

The terminal never saves over a layout it did not read: if the layouts cannot be read, or a layout was saved by a newer KalTerm, a line under the top bar says so and nothing is saved until you use **Save as…**.

## Files

Everything is under `%APPDATA%\KalTerm\`: `terminal.json` (the last server, the newest alert fire seen and the layout in use, per server), `known-hosts.json` (pinned certificates), `credentials.bin` (API tokens, protected for
this Windows user), `risk.json` (the trading limits), `trading_state.json` and `audit.jsonl` (trading). None of them leaves this computer.
