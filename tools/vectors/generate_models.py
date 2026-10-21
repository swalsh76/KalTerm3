"""Generate golden vectors for KalTerm.Kalshi.Models from the Python reference (kalshi_core.models).

    python tools/vectors/generate_models.py --reference %USERPROFILE%\\dev\\KalshiTerm2

Needs pydantic (the reference models are pydantic models): ``pip install pydantic`` in a throwaway venv. The generated
JSON is committed, so CI never runs Python.

Every case is a JSON payload run through the reference model; ``expect`` is the model dumped as JSON (``model_dump(mode=
"json")``) or ``"error"``. The C# port is stricter than pydantic's coercing mode, and it keeps prices, counts, strikes and
fee multipliers exact at their scales; where it refuses something pydantic accepts, the case carries ``port: "error"`` and a
``why``. Each such case is a deliberate difference, recorded as data, not an accident hidden in a test.
"""

import argparse
import copy
import json
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

LAX = "pydantic coerces this type; the port requires the JSON type Kalshi actually sends"
SCALE = "finer than the fixed-point scale (10^-6 for prices and strikes, 10^-2 for counts): refused, never rounded"
NEGFEE = "a negative fee multiplier cannot exist"
INFNAN = "not a finite number"

BASE_MARKET = {
    "ticker": "KXBTCD-26OCT1617-T100000", "event_ticker": "KXBTCD-26OCT1617", "market_type": "binary", "status": "active",
    "yes_sub_title": "$100,000 or above", "no_sub_title": "Below $100,000",
    "created_time": "2026-10-01T14:00:00Z", "updated_time": "2026-10-16T21:22:01.978691Z",
    "open_time": "2026-10-16T14:00:00Z", "close_time": "2026-10-17T21:00:00Z",
    "latest_expiration_time": "2026-10-17T21:00:00Z",
    "yes_bid_dollars": "0.5600", "yes_ask_dollars": "0.5700", "no_bid_dollars": "0.4300", "no_ask_dollars": "0.4400",
    "yes_bid_size_fp": "120.00", "yes_ask_size_fp": "75.50", "last_price_dollars": "0.5600",
    "volume_fp": "10234.00", "volume_24h_fp": "512.25", "open_interest_fp": "4400.00",
    "result": "", "rules_primary": "Resolves yes if the price is at or above the strike.", "rules_secondary": "",
    "strike_type": "greater_or_equal", "floor_strike": 100000, "is_provisional": False, "exchange_index": 0,
}
REFERENCE_TEST_MARKET = {
    "ticker": "A", "event_ticker": "EV-1", "market_type": "binary", "status": "active", "yes_bid_dollars": "0.5600",
    "volume_fp": "10.00", "result": "", "unknown_future_field": 1,
}
MVE_MARKET = {  # shapes copied from live production responses (the reference's test_multivariate)
    "ticker": "KXMVECROSSCATEGORY-S20264B622355CC-ABC", "event_ticker": "KXMVECROSSCATEGORY-S20264B622355CC",
    "market_type": "binary", "status": "active", "mve_collection_ticker": "KXMVECROSSCATEGORY-R",
    "mve_selected_legs": [
        {"event_ticker": "KXATPMATCH-26OCT07COPTSI", "market_ticker": "KXATPMATCH-26OCT07COPTSI-TSI", "side": "yes"},
        {"event_ticker": "KXCONCACAFNLGAME-26OCT06ANTARU", "market_ticker": "KXCONCACAFNLGAME-26OCT06ANTARU-ANT",
         "side": "no", "yes_settlement_value_dollars": "1.0000"},
    ],
    "is_provisional": False, "exchange_index": 1,
}
PLAIN_MARKET = {"ticker": "KXNHL2PTOTAL-26OCT06NYINYR-6", "event_ticker": "KXNHL2PTOTAL-26OCT06NYINYR",
                "market_type": "binary", "status": "active"}
BASE_EVENT = {"event_ticker": "EV-1", "series_ticker": "S", "title": "t", "mutually_exclusive": True}
BASE_SERIES = {"ticker": "S", "title": "t", "frequency": "daily", "category": "x"}
BASE_TRADE = {
    "trade_id": "t1", "ticker": "A", "yes_price_dollars": "0.5900", "no_price_dollars": "0.4100", "count_fp": "149.83",
    "taker_side": "yes", "taker_book_side": "bid", "is_block_trade": False, "created_time": "2026-10-07T21:22:01.978691Z",
}
BASE_STATUS = {"exchange_active": True, "trading_active": False}

cases: list[dict] = []
_names: set[tuple[str, str]] = set()


def lab(value) -> str:
    """A case-name label that tells ``1`` from ``"1"`` from ``1.0`` from ``True``."""
    return f"{type(value).__name__}-{value}"


def add(model: str, name: str, payload, port: str | None = None, why: str | None = None) -> None:
    assert (model, name) not in _names, f"duplicate case name {model}/{name}"
    _names.add((model, name))
    entry = {"model": model, "name": name, "payload": payload}
    if port:
        entry["port"], entry["why"] = port, why
    cases.append(entry)


def variant(base: dict, **changes) -> dict:
    out = copy.deepcopy(base)
    for key, value in changes.items():
        if value is DELETE:
            out.pop(key, None)
        else:
            out[key] = value
    return out


DELETE = object()


def build_corpus() -> None:
    # ---- Market -------------------------------------------------------------------------------------------------------
    for name, payload in [("reference-test-market", REFERENCE_TEST_MARKET), ("multivariate-live-shape", MVE_MARKET),
                          ("plain-minimal", PLAIN_MARKET), ("realistic-full", BASE_MARKET)]:
        add("Market", name, payload)
    add("Market", "unknown-fields-everywhere", variant(BASE_MARKET, brand_new={"nested": [1, 2]}, another=None))

    # prices: exact strings in every shape
    for value in ["0.5600", "0.56", "1", "0", "1.0000", "0.000001", "-0.5", "1e-3", "12345.678901"]:
        add("Market", f"price-string-{value}", variant(BASE_MARKET, yes_bid_dollars=value))
    for value, why, tag in [(0.56, None, None), (1, None, None), (0, None, None), (0.5, None, None),
                            (0.1234567, SCALE, "error"), (1e-7, SCALE, "error")]:
        add("Market", f"price-number-{lab(value)}", variant(BASE_MARKET, yes_bid_dollars=value), tag, why)
    for value in ["0.1234567", "0.0000001", "123456789012345678901234567890.5"]:
        add("Market", f"price-too-fine-or-huge-{value[:12]}", variant(BASE_MARKET, yes_bid_dollars=value), "error", SCALE)
    for value in ["", "abc", "0.5.0", "--1", "1,000", "$1", "0x10"]:
        add("Market", f"price-garbage-{value or 'empty'}", variant(BASE_MARKET, yes_bid_dollars=value))
    for value in ["NaN", "Infinity", "-Infinity"]:
        add("Market", f"price-{value}", variant(BASE_MARKET, yes_bid_dollars=value))
    add("Market", "price-null", variant(BASE_MARKET, yes_bid_dollars=None))
    add("Market", "price-absent", variant(BASE_MARKET, yes_bid_dollars=DELETE))
    add("Market", "price-whitespace", variant(BASE_MARKET, yes_bid_dollars=" 0.5 "), "error", "surrounding whitespace is not a number")
    add("Market", "price-bool", variant(BASE_MARKET, yes_bid_dollars=True))
    add("Market", "price-array", variant(BASE_MARKET, yes_bid_dollars=["0.5"]))
    add("Market", "price-object", variant(BASE_MARKET, yes_bid_dollars={"v": "0.5"}))

    # counts
    for value in ["10.00", "10", "0.01", "0", "-1.50", "92233720368547758.07"]:
        add("Market", f"count-string-{value}", variant(BASE_MARKET, volume_fp=value))
    for value in ["10.001", "0.005", "1.234"]:
        add("Market", f"count-too-fine-{value}", variant(BASE_MARKET, volume_fp=value), "error", SCALE)
    add("Market", "count-number-10", variant(BASE_MARKET, volume_fp=10))
    add("Market", "count-number-10.5", variant(BASE_MARKET, volume_fp=10.5))
    add("Market", "count-empty", variant(BASE_MARKET, volume_fp=""))

    # strikes (floats in the reference)
    for value in [100000, 118.7499, 0, -3.5, 1e3, 118]:
        add("Market", f"strike-number-{lab(value)}", variant(BASE_MARKET, floor_strike=value, cap_strike=value))
    add("Market", "strike-string", variant(BASE_MARKET, floor_strike="118.7499"))   # both accept text; the port reads it exactly
    add("Market", "strike-null", variant(BASE_MARKET, floor_strike=None, cap_strike=None))
    add("Market", "strike-garbage", variant(BASE_MARKET, floor_strike="abc"))
    add("Market", "strike-too-fine", variant(BASE_MARKET, floor_strike=1e-7), "error", SCALE)
    add("Market", "strike-bool", variant(BASE_MARKET, floor_strike=True), "error", LAX)

    # datetimes
    for value in ["2026-10-07T21:22:01Z", "2026-10-07T21:22:01.978691Z", "2026-10-07T21:22:01.5Z", "2026-10-07T21:22:01.123456789Z",
                  "2026-10-07T21:22:01+00:00", "2026-10-07T23:22:01+02:00", "2026-10-07T21:22:01-05:00"]:
        add("Market", f"time-{value}", variant(BASE_MARKET, close_time=value))
    for value, tag, why in [("2026-10-07T21:22:01", "error", "no UTC offset: ambiguous, so refused"),
                            ("2026-10-07 21:22:01Z", "error", "not ISO 8601 with a T separator"),
                            ("2026-10-07", "error", "a date without a time"),
                            (1791334891, "error", "epoch seconds as a number"), (1791334891.5, "error", "epoch seconds as a number"),
                            ("1791334891", "error", "epoch seconds as text")]:
        add("Market", f"time-odd-{lab(value)}", variant(BASE_MARKET, close_time=value), tag, why)
    for value in ["not a date", "", "2026-13-40T00:00:00Z", "2026-02-30T00:00:00Z"]:
        add("Market", f"time-bad-{value or 'empty'}", variant(BASE_MARKET, close_time=value))
    add("Market", "time-null", variant(BASE_MARKET, close_time=None))

    # required fields and wrong types
    for field in ["ticker", "event_ticker", "market_type", "status"]:
        add("Market", f"missing-{field}", variant(BASE_MARKET, **{field: DELETE}))
        add("Market", f"null-{field}", variant(BASE_MARKET, **{field: None}))
    add("Market", "ticker-is-number", variant(BASE_MARKET, ticker=5))
    add("Market", "result-null", variant(BASE_MARKET, result=None))
    add("Market", "result-number", variant(BASE_MARKET, result=1))
    add("Market", "wrong-case-key", variant(BASE_MARKET, Ticker="X", ticker=DELETE))
    for value, tag in [(True, None), (False, None), (None, None), ("true", "error"), ("false", "error"), (1, "error"), (0, "error"),
                       ("yes", "error"), ("maybe", None)]:
        add("Market", f"is-provisional-{lab(value)}", variant(BASE_MARKET, is_provisional=value), tag, LAX if tag else None)
    for value, tag in [(1, None), (0, None), (None, None), ("1", "error"), (1.0, "error"), (1.5, None), ("x", None), (True, "error")]:
        add("Market", f"exchange-index-{lab(value)}", variant(BASE_MARKET, exchange_index=value), tag, LAX if tag else None)
    # multivariate legs
    add("Market", "legs-empty", variant(MVE_MARKET, mve_selected_legs=[]))
    add("Market", "legs-null", variant(MVE_MARKET, mve_selected_legs=None))
    add("Market", "leg-missing-side", variant(MVE_MARKET, mve_selected_legs=[{"event_ticker": "E", "market_ticker": "M"}]))
    add("Market", "leg-settlement-bad", variant(MVE_MARKET, mve_selected_legs=[
        {"event_ticker": "E", "market_ticker": "M", "side": "yes", "yes_settlement_value_dollars": "abc"}]))
    add("Market", "legs-not-a-list", variant(MVE_MARKET, mve_selected_legs={"a": 1}))
    add("Market", "mve-without-legs", variant(PLAIN_MARKET, mve_collection_ticker="C"))

    # ---- Event --------------------------------------------------------------------------------------------------------
    add("Event", "reference-test-event", BASE_EVENT)
    add("Event", "with-nested-markets", variant(BASE_EVENT, markets=[MVE_MARKET, PLAIN_MARKET], sub_title="s", strike_period="day"))
    add("Event", "nested-market-invalid", variant(BASE_EVENT, markets=[{"ticker": "X"}]))
    add("Event", "nested-markets-null", variant(BASE_EVENT, markets=None))
    add("Event", "multivariate-event-live-shape", {"event_ticker": "E1", "series_ticker": "KXMVECROSSCATEGORY-SHARD1",
        "title": "Arsenal vs Coventry", "sub_title": "", "collateral_return_type": "", "mutually_exclusive": False})
    for field in ["event_ticker", "series_ticker", "title", "mutually_exclusive"]:
        add("Event", f"missing-{field}", variant(BASE_EVENT, **{field: DELETE}))
    for value, tag in [(True, None), (False, None), ("true", "error"), (1, "error"), (None, None)]:
        add("Event", f"mutually-exclusive-{lab(value)}", variant(BASE_EVENT, mutually_exclusive=value), tag, LAX if tag else None)
    for value in ["2026-10-07T21:22:01Z", "2026-10-07T21:22:01.5+00:00", "bad"]:
        add("Event", f"strike-date-{value}", variant(BASE_EVENT, strike_date=value, last_updated_ts=value))

    # ---- Series -------------------------------------------------------------------------------------------------------
    add("Series", "reference-test-series", BASE_SERIES)
    add("Series", "with-quadratic-fee", variant(BASE_SERIES, fee_type="quadratic", fee_multiplier=0.07, tags=["a", "b"],
                                                  volume_fp="1234.56", last_updated_ts="2026-10-16T00:00:00Z"))
    for value, tag, why in [(0.07, None, None), (1, None, None), (0, None, None), (0.5, None, None), (2, None, None),
                            ("0.07", None, None), (-1, "error", NEGFEE), (-0.5, "error", NEGFEE), (1e-7, "error", SCALE),
                            (0.0700000001, "error", SCALE), (None, None, None)]:
        add("Series", f"fee-multiplier-{lab(value)}", variant(BASE_SERIES, fee_type="quadratic", fee_multiplier=value), tag, why)
    add("Series", "fee-type-null", variant(BASE_SERIES, fee_type=None))
    add("Series", "tags-null", variant(BASE_SERIES, tags=None))
    add("Series", "tags-empty", variant(BASE_SERIES, tags=[]))
    add("Series", "tags-wrong-type", variant(BASE_SERIES, tags="a"))
    add("Series", "tags-with-number", variant(BASE_SERIES, tags=["a", 1]))
    for field in ["ticker", "title", "frequency", "category"]:
        add("Series", f"missing-{field}", variant(BASE_SERIES, **{field: DELETE}))
    add("Series", "volume-too-fine", variant(BASE_SERIES, volume_fp="1.234"), "error", SCALE)

    # ---- Trade --------------------------------------------------------------------------------------------------------
    add("Trade", "reference-test-trade", BASE_TRADE)
    add("Trade", "defaults", {k: v for k, v in BASE_TRADE.items() if k not in ("taker_side", "is_block_trade", "taker_book_side")})
    for field in ["trade_id", "ticker", "yes_price_dollars", "no_price_dollars", "count_fp", "created_time"]:
        add("Trade", f"missing-{field}", variant(BASE_TRADE, **{field: DELETE}))
        add("Trade", f"null-{field}", variant(BASE_TRADE, **{field: None}))
    add("Trade", "numbers-for-prices", variant(BASE_TRADE, yes_price_dollars=0.59, no_price_dollars=0.41, count_fp=149.83))
    add("Trade", "price-empty", variant(BASE_TRADE, yes_price_dollars=""))
    add("Trade", "count-too-fine", variant(BASE_TRADE, count_fp="149.835"), "error", SCALE)
    add("Trade", "taker-side-null", variant(BASE_TRADE, taker_side=None))
    add("Trade", "block-trade-true", variant(BASE_TRADE, is_block_trade=True))
    add("Trade", "block-trade-string", variant(BASE_TRADE, is_block_trade="false"), "error", LAX)
    add("Trade", "created-bad", variant(BASE_TRADE, created_time="yesterday"))
    add("Trade", "created-micro", variant(BASE_TRADE, created_time="2026-10-07T21:22:01.000001Z"))

    # ---- Pages and lists ------------------------------------------------------------------------------------------------
    add("MarketsPage", "empty", {"markets": [], "cursor": ""})
    add("MarketsPage", "two-markets-with-cursor", {"markets": [REFERENCE_TEST_MARKET, PLAIN_MARKET], "cursor": "c1"})
    add("MarketsPage", "cursor-absent", {"markets": [PLAIN_MARKET]})
    add("MarketsPage", "cursor-null", {"markets": [], "cursor": None})
    add("MarketsPage", "cursor-number", {"markets": [], "cursor": 5})
    add("MarketsPage", "markets-absent", {"cursor": ""})
    add("MarketsPage", "markets-null", {"markets": None, "cursor": ""})
    add("MarketsPage", "one-bad-market", {"markets": [PLAIN_MARKET, {"ticker": "x"}], "cursor": ""})
    add("MarketsPage", "not-an-object", [])
    add("TradesPage", "two-trades", {"trades": [BASE_TRADE, variant(BASE_TRADE, trade_id="t2")], "cursor": "c1"})
    add("TradesPage", "empty-no-cursor", {"trades": []})
    add("TradesPage", "bad-trade", {"trades": [{"trade_id": "x"}], "cursor": ""})
    add("EventsPage", "two-events", {"events": [BASE_EVENT, variant(BASE_EVENT, event_ticker="E2")], "cursor": "c9"})
    add("EventsPage", "empty", {"events": [], "cursor": ""})
    add("EventsPage", "bad-event", {"events": [{"event_ticker": "x"}], "cursor": ""})
    add("SeriesList", "one", {"series": [BASE_SERIES]})
    add("SeriesList", "empty", {"series": []})
    add("SeriesList", "absent", {})
    add("SeriesList", "bad-series", {"series": [{"ticker": "x"}]})

    # ---- Exchange status ----------------------------------------------------------------------------------------------
    add("ExchangeStatus", "reference-test", BASE_STATUS)
    add("ExchangeStatus", "with-resume-time", variant(BASE_STATUS, exchange_estimated_resume_time="2026-10-17T14:00:00Z"))
    add("ExchangeStatus", "with-index-statuses", variant(BASE_STATUS, exchange_index_statuses=[
        {"exchange_index": 0, "description": "main", "exchange_active": True, "trading_active": True},
        {"exchange_index": 1, "description": "shard", "exchange_active": True, "trading_active": False,
         "intra_exchange_transfers_active": True}]))
    add("ExchangeStatus", "index-status-missing-field", variant(BASE_STATUS, exchange_index_statuses=[{"exchange_index": 0}]))
    add("ExchangeStatus", "index-statuses-null", variant(BASE_STATUS, exchange_index_statuses=None))
    for field in ["exchange_active", "trading_active"]:
        add("ExchangeStatus", f"missing-{field}", variant(BASE_STATUS, **{field: DELETE}))
        add("ExchangeStatus", f"null-{field}", variant(BASE_STATUS, **{field: None}))
    for value, tag in [("true", "error"), (1, "error"), ("no", "error"), (0, "error")]:
        add("ExchangeStatus", f"exchange-active-{lab(value)}", variant(BASE_STATUS, exchange_active=value), tag, LAX)
    add("ExchangeStatus", "resume-time-bad", variant(BASE_STATUS, exchange_estimated_resume_time="soon"))

    # ---- Order book (the response shape; the reference parses it with OrderBook.from_response) --------------------------
    add("OrderBook", "reference-test", {"orderbook_fp": {"yes_dollars": [["0.1500", "100.00"]], "no_dollars": []}})
    add("OrderBook", "both-sides", {"orderbook_fp": {"yes_dollars": [["0.1500", "100.00"], ["0.1400", "5.50"]],
                                                     "no_dollars": [["0.8000", "1.00"]]}})
    add("OrderBook", "empty-book", {"orderbook_fp": {"yes_dollars": [], "no_dollars": []}})
    add("OrderBook", "sides-absent", {"orderbook_fp": {}})
    add("OrderBook", "side-null", {"orderbook_fp": {"yes_dollars": None, "no_dollars": None}})
    add("OrderBook", "missing-orderbook-fp", {"orderbook": {}})
    add("OrderBook", "orderbook-fp-null", {"orderbook_fp": None})
    add("OrderBook", "orderbook-fp-list", {"orderbook_fp": []})
    add("OrderBook", "not-an-object", [])
    add("OrderBook", "row-of-three", {"orderbook_fp": {"yes_dollars": [["0.15", "1", "x"]]}})
    add("OrderBook", "row-of-one", {"orderbook_fp": {"yes_dollars": [["0.15"]]}})
    add("OrderBook", "row-is-object", {"orderbook_fp": {"yes_dollars": [{"price": "0.15", "quantity": "1"}]}})
    add("OrderBook", "side-is-object", {"orderbook_fp": {"yes_dollars": {"a": 1}}})
    add("OrderBook", "price-garbage", {"orderbook_fp": {"yes_dollars": [["abc", "1"]]}})
    add("OrderBook", "price-too-fine", {"orderbook_fp": {"yes_dollars": [["0.1234567", "1"]]}}, "error", SCALE)
    add("OrderBook", "quantity-too-fine", {"orderbook_fp": {"yes_dollars": [["0.15", "1.234"]]}}, "error", SCALE)
    add("OrderBook", "numbers-exact-in-binary", {"orderbook_fp": {"yes_dollars": [[0.5, 100]]}})
    # Not in the corpus on purpose: a JSON number such as 0.15. The reference's Decimal(0.15) is the float's binary expansion
    # (0.1499999999999999944...), so there is no meaningful value to compare; the port reads the number's own text, 0.15
    # (see OrderBookTests).
    add("OrderBook", "negative-quantity", {"orderbook_fp": {"yes_dollars": [["0.15", "-1.00"]]}})
    add("OrderBook", "price-above-one", {"orderbook_fp": {"yes_dollars": [["1.50", "1.00"]]}})


def reference_outcome(models, entry: dict):
    model, payload = entry["model"], entry["payload"]
    try:
        if model == "OrderBook":
            return models.OrderBook.from_response(payload).model_dump(mode="json")
        return getattr(models, model).model_validate(payload).model_dump(mode="json")
    except Exception:  # ValidationError, KeyError, AssertionError, TypeError, decimal.InvalidOperation ...
        return "error"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    models = load_reference_module(args.reference, "models")
    build_corpus()
    for entry in cases:
        entry["expect"] = reference_outcome(models, entry)
        # A deliberately stricter port only differs where the reference succeeds.
        if entry.get("port") == "error" and entry["expect"] == "error":
            del entry["port"], entry["why"]

    out = args.out or default_output(__file__, "models.json", "KalTerm.Kalshi.Tests")
    write_vectors(out, "tools/vectors/generate_models.py", args.reference, {"cases": cases})
    by_model: dict[str, list[int]] = {}
    for entry in cases:
        bucket = by_model.setdefault(entry["model"], [0, 0])
        bucket[0 if entry["expect"] != "error" else 1] += 1
    summary = ", ".join(f"{m} {ok}ok/{err}err" for m, (ok, err) in by_model.items())
    deviations = sum(1 for e in cases if "port" in e)
    print(f"wrote {out}: {len(cases)} cases ({summary}); {deviations} deliberate port differences")


if __name__ == "__main__":
    main()
