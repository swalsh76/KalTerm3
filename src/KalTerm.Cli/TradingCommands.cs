using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Kalshi;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Account;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Orders;
using KalTerm.Trading.Risk;
using KalTerm.Trading.Session;
using KalTerm.Trading.Transport;
using Spectre.Console;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>
/// <c>buy</c>, <c>sell</c>, <c>amend</c>, <c>cancel</c>, <c>kill</c>, <c>resume</c>, the account views and <c>trading-key</c>. Trading talks to Kalshi directly; none of this touches the KalTerm server. Every order goes through the
/// order service (so through the risk layer); what would happen is always printed first, including any shard funding and every reason for a refusal. <c>--dry-run</c> stops after printing and can send nothing. In demo an order asks
/// y/N (<c>--yes</c> skips it). In production the order is only sent after it is typed out (ticker, buy or sell, yes or no, count, price) and <c>--yes</c> does not skip that; with no input available nothing is sent. Cancels, <c>kill</c> and
/// <c>resume</c> are not risk-checked: nothing may stand between you and getting out. Exit status: 0 done, 1 refused or failed (nothing happened that you did not see), 2 a write whose outcome is unknown (<c>UNKNOWN:</c>, naming the id to look for).
/// </summary>
internal static class TradingCommands
{
    public const int Unknown = 2;

    private static readonly string[] Environments = ["demo", "production"];

    public static void Add(RootCommand root, CliEnvironment env, Option<bool> json)
    {
        foreach (var (name, action, verb) in new[] { ("buy", TradeAction.Buy, "Buy"), ("sell", TradeAction.Sell, "Sell") })
        {
            root.Subcommands.Add(PlaceCommand(env, json, name, action, verb));
        }

        root.Subcommands.Add(AmendCommand(env, json));
        root.Subcommands.Add(CancelCommand(env, json));
        root.Subcommands.Add(KillCommand(env, json));
        root.Subcommands.Add(ResumeCommand(env));
        root.Subcommands.Add(BalanceCommand(env, json));
        root.Subcommands.Add(PositionsCommand(env, json));
        root.Subcommands.Add(OrdersCommand(env, json));
        root.Subcommands.Add(FillsCommand(env, json));
        root.Subcommands.Add(KeyCommand(env));
    }

    // ------------------------------------------------------------------ placing orders

    private static Command PlaceCommand(CliEnvironment env, Option<bool> json, string name, TradeAction action, string verb)
    {
        var ticker = new Argument<string>("ticker") { Description = "Market ticker" };
        var count = new Argument<string>("count") { Description = "Contracts, for example 2 or 0.5" };
        var price = new Argument<string>("price") { Description = $"Limit price in dollars of the side you {name}" };
        var side = new Option<string>("--side") { Description = $"Which side to {name}", DefaultValueFactory = _ => "yes" };
        side.AcceptOnlyFromAmong("yes", "no");
        var ioc = new Option<bool>("--ioc") { Description = "Immediate-or-cancel" };
        var postOnly = new Option<bool>("--post-only") { Description = "Never take liquidity" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would happen; send nothing" };
        var yes = new Option<bool>("--yes") { Description = "Skip the y/N question (never the production typed check)" };
        var command = new Command(name, $"{verb} contracts at a limit price (checked against your risk limits first)");
        command.Arguments.Add(ticker);
        command.Arguments.Add(count);
        command.Arguments.Add(price);
        foreach (var option in new Option[] { side, ioc, postOnly, dryRun, yes })
        {
            command.Options.Add(option);
        }

        command.SetAction((parsed, ct) =>
        {
            if (!ParseCount(parsed.GetRequiredValue(count), out var countE2, out var problem) || !ParsePrice(parsed.GetRequiredValue(price), out var priceE6, out problem))
            {
                return Task.FromResult(CliApp.Fail(env, problem!));
            }

            var request = new TradeRequest(
                parsed.GetRequiredValue(ticker),
                action,
                parsed.GetRequiredValue(side) == "no" ? TradeOutcome.No : TradeOutcome.Yes,
                countE2,
                priceE6,
                parsed.GetValue(ioc) ? TimeInForce.ImmediateOrCancel : TimeInForce.GoodTillCanceled,
                parsed.GetValue(postOnly));
            var dry = parsed.GetValue(dryRun);
            var asJson = parsed.GetValue(json);
            return Run(env, dry ? TradingMode.Preview : TradingMode.Trade, async session =>
            {
                var prepared = await session.Service!.PrepareAsync(request, ct).ConfigureAwait(false);
                ShowPlan(env, asJson, prepared, null);
                if (!prepared.Verdict.Ok)
                {
                    return CliApp.Failed;
                }

                if (dry)
                {
                    Note(env, asJson, "dry run: nothing was sent");
                    return CliApp.Ok;
                }

                if (!Confirm(env, session.Production, prepared.Request, parsed.GetValue(yes), out var refusal))
                {
                    return CliApp.Fail(env, refusal!);
                }

                var placement = await session.Service.ExecuteAsync(prepared, ct).ConfigureAwait(false);
                Lines(env, TradingViews.PlacementLines(placement));
                return CliApp.Ok;
            }, ct);
        });
        return command;
    }

    private static Command AmendCommand(CliEnvironment env, Option<bool> json)
    {
        var orderId = new Argument<string>("order") { Description = "The resting order to change" };
        var price = new Option<string?>("--price") { Description = "New YES price in dollars, as `kterm orders` shows it" };
        var count = new Option<string?>("--count") { Description = "New TOTAL count" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would happen; send nothing" };
        var yes = new Option<bool>("--yes") { Description = "Skip the y/N question (never the production typed check)" };
        var command = new Command("amend", "Change a resting order's price and/or count (checked like a new order)");
        command.Arguments.Add(orderId);
        foreach (var option in new Option[] { price, count, dryRun, yes })
        {
            command.Options.Add(option);
        }

        command.SetAction((parsed, ct) =>
        {
            long? newPrice = null;
            long? newCount = null;
            string? problem = null;
            if (parsed.GetValue(price) is { } p)
            {
                if (!ParsePrice(p, out var priceE6, out problem))
                {
                    return Task.FromResult(CliApp.Fail(env, problem!));
                }

                newPrice = priceE6;
            }

            if (parsed.GetValue(count) is { } c)
            {
                if (!ParseCount(c, out var countE2, out problem))
                {
                    return Task.FromResult(CliApp.Fail(env, problem!));
                }

                newCount = countE2;
            }

            var dry = parsed.GetValue(dryRun);
            var asJson = parsed.GetValue(json);
            return Run(env, dry ? TradingMode.Preview : TradingMode.Trade, async session =>
            {
                Prepared prepared;
                try
                {
                    prepared = await session.Service!.PrepareAmendAsync(parsed.GetRequiredValue(orderId), newPrice, newCount, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    return CliApp.Fail(env, ex.Message);
                }

                ShowPlan(env, asJson, prepared, $"amend order {parsed.GetRequiredValue(orderId)}:");
                if (!prepared.Verdict.Ok)
                {
                    return CliApp.Failed;
                }

                if (dry)
                {
                    Note(env, asJson, "dry run: nothing was sent");
                    return CliApp.Ok;
                }

                if (!Confirm(env, session.Production, prepared.Request, parsed.GetValue(yes), out var refusal))
                {
                    return CliApp.Fail(env, refusal!);
                }

                var amendment = await session.Service.ExecuteAmendAsync(prepared, ct).ConfigureAwait(false);
                Lines(env, TradingViews.AmendmentLines(amendment));
                return CliApp.Ok;
            }, ct);
        });
        return command;
    }

    // ------------------------------------------------------------------ getting out

    private static Command CancelCommand(CliEnvironment env, Option<bool> json)
    {
        var ids = new Argument<string[]>("orders") { Description = "Order ids (see kterm orders)", Arity = ArgumentArity.OneOrMore };
        var command = new Command("cancel", "Cancel resting orders. Never refused by the risk layer; works while halted");
        command.Arguments.Add(ids);
        command.SetAction((parsed, ct) => Run(env, TradingMode.Exit, async session =>
        {
            var outcomes = await OrderService.CancelByIdsAsync(session.Markets, session.Trading!, parsed.GetRequiredValue(ids), ct).ConfigureAwait(false);
            if (parsed.GetValue(json))
            {
                Json(env, outcomes.Select(o => new { order_id = o.OrderId, ok = o.Ok, message = o.Message }));
            }
            else
            {
                Lines(env, outcomes.Select(o => $"{o.OrderId}: {o.Message}"));
            }

            return outcomes.All(o => o.Ok) ? CliApp.Ok : CliApp.Failed;
        }, ct));
        return command;
    }

    private static Command KillCommand(CliEnvironment env, Option<bool> json)
    {
        var command = new Command("kill", "Halt new orders and cancel every open order, then check until none remain");
        command.SetAction((parsed, ct) => Run(env, TradingMode.Exit, async session =>
        {
            var report = await session.Service!.KillAsync("kill switch (kterm kill)", ct).ConfigureAwait(false);
            if (parsed.GetValue(json))
            {
                Json(env, new { cancelled = report.Cancelled, remaining = report.Remaining, errors = report.Errors, clean = report.Clean });
            }
            else
            {
                Lines(env, ["trading is HALTED (new orders refused until `kterm resume`)", $"cancelled {report.Cancelled.Count} order(s) in the follow-up passes"]);
                if (report.Remaining.Count > 0)
                {
                    env.Error.MarkupLine($"[red]STILL RESTING:[/] {Escape(string.Join(", ", report.Remaining))}");
                }

                foreach (var error in report.Errors)
                {
                    env.Error.MarkupLine($"[yellow]problem:[/] {Escape(error)}");
                }

                if (report.Clean)
                {
                    Lines(env, ["no orders are resting"]);
                }
            }

            return report.Clean ? CliApp.Ok : CliApp.Failed;
        }, ct));
        return command;
    }

    private static Command ResumeCommand(CliEnvironment env)
    {
        var yes = new Option<bool>("--yes") { Description = "Do not ask" };
        var command = new Command("resume", "Clear the halt left by the kill switch so new orders are allowed again");
        command.Options.Add(yes);
        command.SetAction(parsed =>
        {
            try
            {
                var state = env.LoadTradingState();
                if (!state.Halted)
                {
                    Lines(env, ["trading is not halted"]);
                    return CliApp.Ok;
                }

                Lines(env, [$"trading is halted: {(state.HaltReason.Length == 0 ? "kill switch" : state.HaltReason)}"]);
                if (!parsed.GetValue(yes) && !Ask(env, "Allow new orders again? [y/N] "))
                {
                    return CliApp.Fail(env, "still halted");
                }

                state.ClearHalt();
                Lines(env, ["halt cleared"]);
                return CliApp.Ok;
            }
            catch (TradingStateException ex)
            {
                return CliApp.Fail(env, ex.Message);
            }
        });
        return command;
    }

    // ------------------------------------------------------------------ looking

    private static Command BalanceCommand(CliEnvironment env, Option<bool> json)
    {
        var command = new Command("balance", "Your balance on each exchange shard");
        command.SetAction((parsed, ct) => Run(env, TradingMode.Read, async session =>
        {
            var balance = await session.Reads.BalanceAsync(ct).ConfigureAwait(false);
            if (parsed.GetValue(json))
            {
                Json(env, new
                {
                    balance = TradingViews.Money(balance.BalanceDollars.Micros),
                    portfolio_value = TradingViews.Money(balance.PortfolioValue * 10_000),
                    shards = (balance.BalanceBreakdown ?? []).Select(s => new { shard = s.ExchangeIndex, available = TradingViews.Money(s.Balance.Micros) }),
                    halted = session.State.Halted,
                });
            }
            else
            {
                env.Out.Write(TradingViews.Balance(balance, session.State.Halted ? session.State.HaltReason : null));
            }

            return CliApp.Ok;
        }, ct));
        return command;
    }

    private static Command PositionsCommand(CliEnvironment env, Option<bool> json)
    {
        var all = new Option<bool>("--all") { Description = "Include flat and settled markets" };
        var command = new Command("positions", "Your market positions");
        command.Options.Add(all);
        command.SetAction((parsed, ct) => Run(env, TradingMode.Read, async session =>
        {
            var found = await session.Reads.PositionsAsync(parsed.GetValue(all), ct).ToListAsync(ct).ConfigureAwait(false);
            var shown = parsed.GetValue(all) ? found : found.Where(p => p.PositionFp.Hundredths != 0).ToList();
            if (parsed.GetValue(json))
            {
                Json(env, shown.Select(p => new { ticker = p.Ticker, shard = p.ExchangeIndex, position = RiskChecker.CountText(p.PositionFp.Hundredths), exposure = TradingViews.Money(p.MarketExposureDollars), realized = TradingViews.Money(p.RealizedPnlDollars), fees = TradingViews.Money(p.FeesPaidDollars) }));
            }
            else
            {
                env.Out.Write(TradingViews.Positions(shown));
            }

            return CliApp.Ok;
        }, ct));
        return command;
    }

    private static Command OrdersCommand(CliEnvironment env, Option<bool> json)
    {
        var status = new Option<string>("--status") { Description = "resting, canceled, executed or all", DefaultValueFactory = _ => "resting" };
        status.AcceptOnlyFromAmong("resting", "canceled", "executed", "all");
        var limit = new Option<int>("--limit") { Description = "Most recent N", DefaultValueFactory = _ => 50 };
        var command = new Command("orders", "Your orders (resting ones by default)");
        command.Options.Add(status);
        command.Options.Add(limit);
        command.SetAction((parsed, ct) => Run(env, TradingMode.Read, async session =>
        {
            var wanted = parsed.GetRequiredValue(status);
            var found = await session.Reads.OrdersAsync(wanted == "all" ? null : wanted, ct).ToListAsync(ct).ConfigureAwait(false);
            var shown = found.OrderByDescending(o => o.CreatedTime).Take(Math.Clamp(parsed.GetValue(limit), 1, 1000)).ToList();
            if (parsed.GetValue(json))
            {
                Json(env, shown.Select(o => new { order_id = o.OrderId, ticker = o.Ticker, book_side = o.BookSide, yes_price = TradingViews.Money(o.YesPriceDollars.Micros), no_price = TradingViews.Money(o.NoPriceDollars.Micros), filled = RiskChecker.CountText(o.FillCountFp.Hundredths), left = RiskChecker.CountText(o.RemainingCountFp.Hundredths), status = o.Status, created = o.CreatedTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }));
            }
            else
            {
                env.Out.Write(TradingViews.Orders(shown, env.Out.Profile.Width));
                if (shown.Count > 0)
                {
                    Lines(env, [TradingViews.OrdersNote]);
                }
            }

            return CliApp.Ok;
        }, ct));
        return command;
    }

    private static Command FillsCommand(CliEnvironment env, Option<bool> json)
    {
        var limit = new Option<int>("--limit") { Description = "Most recent N", DefaultValueFactory = _ => 20 };
        var command = new Command("fills", "Your recent fills");
        command.Options.Add(limit);
        command.SetAction((parsed, ct) => Run(env, TradingMode.Read, async session =>
        {
            var found = await session.Reads.FillsAsync(ct).ToListAsync(ct).ConfigureAwait(false);
            var shown = found.OrderByDescending(f => f.Ts ?? 0).Take(Math.Clamp(parsed.GetValue(limit), 1, 1000)).ToList();
            if (parsed.GetValue(json))
            {
                Json(env, shown.Select(f => new { fill_id = f.FillId, order_id = f.OrderId, ticker = f.Ticker, book_side = f.BookSide, count = RiskChecker.CountText(f.CountFp.Hundredths), yes_price = TradingViews.Money(f.YesPriceDollars.Micros), no_price = TradingViews.Money(f.NoPriceDollars.Micros), fee = TradingViews.Money(f.FeeCost.Micros), taker = f.IsTaker, created = f.CreatedTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }));
            }
            else
            {
                env.Out.Write(TradingViews.Fills(shown, env.Out.Profile.Width));
                if (shown.Count > 0)
                {
                    Lines(env, [TradingViews.OrdersNote]);
                }
            }

            return CliApp.Ok;
        }, ct));
        return command;
    }

    // ------------------------------------------------------------------ the trading key

    private static Command KeyCommand(CliEnvironment env)
    {
        var environment = new Option<string>("--env") { Description = "demo or production", DefaultValueFactory = _ => "demo" };
        environment.AcceptOnlyFromAmong("demo", "production");
        var keyId = new Option<string>("--key-id") { Description = "The key id Kalshi issued", Required = true };
        var pemFile = new Option<string>("--pem-file") { Description = "The private key file (unencrypted Ed25519 PKCS#8 PEM)", Required = true };
        var root = new Command("trading-key", "The trading key, kept in this Windows account's Credential Manager (never on the server, never in a file of the repository)");

        var set = new Command("set", "Store (or replace) the trading key for an environment. Delete the PEM file afterwards");
        set.Options.Add(environment);
        set.Options.Add(keyId);
        set.Options.Add(pemFile);
        set.SetAction(parsed =>
        {
            try
            {
                var path = parsed.GetRequiredValue(pemFile);
                if (!File.Exists(path))
                {
                    return CliApp.Fail(env, $"there is no file {path}");
                }

                TradingKeyLoader.Store(env.OpenVault(), parsed.GetRequiredValue(environment), parsed.GetRequiredValue(keyId), File.ReadAllText(path), path);
                Lines(env, [$"stored the {parsed.GetRequiredValue(environment)} trading key (id ends {Tail(parsed.GetRequiredValue(keyId))}); delete {path} now"]);
                return CliApp.Ok;
            }
            catch (Exception ex) when (ex is AuthException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
            {
                return CliApp.Fail(env, ex.Message);
            }
        });
        root.Subcommands.Add(set);

        var show = new Command("show", "Which environments have a trading key (the key itself is never shown)");
        show.SetAction(_ =>
        {
            try
            {
                var vault = env.OpenVault();
                Lines(env, Environments.Select(name => vault.Load(name) is { } entry ? $"{name}: stored (id ends {Tail(entry.KeyId)})" : $"{name}: none"));
                return CliApp.Ok;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                return CliApp.Fail(env, ex.Message);
            }
        });
        root.Subcommands.Add(show);

        var delete = new Command("delete", "Forget the trading key of an environment");
        delete.Options.Add(environment);
        delete.SetAction(parsed =>
        {
            try
            {
                var name = parsed.GetRequiredValue(environment);
                Lines(env, [env.OpenVault().Delete(name) ? $"deleted the {name} trading key" : $"there was no {name} trading key"]);
                return CliApp.Ok;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                return CliApp.Fail(env, ex.Message);
            }
        });
        root.Subcommands.Add(delete);
        return root;
    }

    private static string Tail(string keyId) => keyId.Length <= 4 ? "…" : "…" + keyId[^4..];

    // ------------------------------------------------------------------ plumbing

    /// <summary>Opens the session for a mode, runs the work, and turns every problem a person can act on into one clear line and an exit status.</summary>
    private static async Task<int> Run(CliEnvironment env, TradingMode mode, Func<TradingSession, Task<int>> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var session = await env.OpenTrading(mode, cancellationToken).ConfigureAwait(false);
            env.Error.MarkupLine(session.Production ? "[bold red]PRODUCTION:[/] real money on your Kalshi account" : $"[grey]environment: {Escape(session.Settings.Environment.ToString().ToLowerInvariant())}[/]");
            return await work(session).ConfigureAwait(false);
        }
        catch (OrderRefusedException ex)
        {
            return CliApp.Fail(env, ex.Message);
        }
        catch (PlanChangedException)
        {
            return CliApp.Fail(env, "the cost or funding changed while you were deciding; run the command again");
        }
        catch (TransferFailedException ex)
        {
            return CliApp.Fail(env, $"{ex.Message}; the order was not placed");
        }
        catch (Exception ex) when (ex is TransferUnknownException or FundingShortException)
        {
            return CliApp.Fail(env, ex.Message);
        }
        catch (WriteOutcomeUnknownException ex)
        {
            env.Error.MarkupLine($"[bold red]UNKNOWN:[/] {Escape(ex.Message)}. Look for {Escape(ex.Reference)} in `kterm orders --status all` and `kterm fills` before trying again; do not repeat the command blindly.");
            return Unknown;
        }
        catch (TradingRefusedException ex)
        {
            return CliApp.Fail(env, $"{ex.Message} (production orders need KALSHI_ENV=production and \"ProductionEnabled\": true in risk.json)");
        }
        catch (KalshiApiException ex)
        {
            return CliApp.Fail(env, $"Kalshi said {ex.StatusCode}: {(ex.Body.Length > 300 ? ex.Body[..300] : ex.Body)}");
        }
        catch (HttpRequestException ex)
        {
            return CliApp.Fail(env, $"cannot reach Kalshi: {ex.GetType().Name}");
        }
        catch (Exception ex) when (ex is AuthException or TradingStateException or KalshiConfigurationException or RiskConfigException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            return CliApp.Fail(env, ex.Message);
        }
    }

    private static void ShowPlan(CliEnvironment env, bool asJson, Prepared prepared, string? heading)
    {
        if (asJson)
        {
            var v = prepared.Verdict;
            Json(env, new
            {
                approved = v.Ok,
                shard = prepared.Market.ExchangeIndex,
                fee_e6 = v.FeeE6,
                worst_case_cost_e6 = v.WorstCaseCostE6,
                funding = v.Funding.Select(t => new { source = t.Source, destination = t.Destination, amount_e6 = t.AmountE6 }),
                refusals = v.Refusals.Select(r => new { code = RiskCodes.Name(r.Code), message = r.Message }),
                lines = OrderService.Describe(prepared),
            });
            return;
        }

        if (heading is not null)
        {
            Lines(env, [heading]);
        }

        Lines(env, OrderService.Describe(prepared));
    }

    /// <summary>Asks before sending. Production needs the order typed out; demo needs y/N (or <c>--yes</c>). With no input available nothing is sent.</summary>
    private static bool Confirm(CliEnvironment env, bool production, TradeRequest request, bool yes, out string? refusal)
    {
        refusal = null;
        if (production)
        {
            if (yes)
            {
                env.Error.MarkupLine("[grey]note: --yes does not skip the production check[/]");
            }

            env.Error.MarkupLine("Type the order to send it:  TICKER buy|sell yes|no COUNT PRICE");
            var typed = env.InteractiveInput ? env.ReadLine() : null;
            if (typed is null)
            {
                refusal = "no confirmation given; nothing was sent";
                return false;
            }

            if (!OrderService.TypedPhraseMatches(typed, request))
            {
                refusal = "that is not the order shown above; nothing was sent";
                return false;
            }

            return true;
        }

        if (yes || Ask(env, "Send this order? [y/N] "))
        {
            return true;
        }

        refusal = "not sent";
        return false;
    }

    private static bool Ask(CliEnvironment env, string question)
    {
        env.Error.Markup(Escape(question));
        var answer = env.InteractiveInput ? env.ReadLine() : null;
        return answer is not null && answer.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static bool ParseCount(string text, out long hundredths, out string? problem)
    {
        hundredths = 0;
        problem = null;
        if (!KalTerm.Core.Money.Contracts.TryParse(text, out var parsed) || parsed.Hundredths <= 0)
        {
            problem = $"count: '{text}' is not a positive number of contracts with at most two decimals";
            return false;
        }

        hundredths = parsed.Hundredths;
        return true;
    }

    private static bool ParsePrice(string text, out long micros, out string? problem)
    {
        micros = 0;
        problem = null;
        if (!KalTerm.Core.Money.Dollars.TryParse(text, out var parsed) || parsed.Micros <= 0 || parsed.Micros >= KalTerm.Core.Money.Dollars.Scale)
        {
            problem = $"price: '{text}' is not a price strictly between $0 and $1";
            return false;
        }

        micros = parsed.Micros;
        return true;
    }

    private static void Lines(CliEnvironment env, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            env.Out.MarkupLine(Escape(line));
        }
    }

    private static void Note(CliEnvironment env, bool asJson, string line)
    {
        if (!asJson)
        {
            Lines(env, [line]);
        }
    }

    private static void Json(CliEnvironment env, object value) =>
        env.Out.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
