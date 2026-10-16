"""Generate golden vectors for the WebSocket models in KalTerm.Kalshi from the Python reference (kalshi_core.ws_models).

    python tools/vectors/generate_ws_models.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

Needs pydantic (``pip install pydantic`` in a throwaway venv). The generated JSON is committed, so CI never runs Python.

Every case is a JSON payload run through the reference model; ``expect`` is the model dumped as JSON or ``"error"``. For the
``WsMessage`` envelope the expectation is the envelope plus ``payload`` (the typed payload from ``WsMessage.payload()``, or
null for a type that has none), and any payload error makes the whole case an error. The port is stricter than pydantic's
coercing mode and keeps prices and counts exact; where it refuses something pydantic accepts the case carries
``port: "error"`` and a ``why`` (see generate_models.py). The private channels (fill, user_order, market_position) are not
modelled until M6, so the corpus leaves them out.
"""

import argparse
import copy
import json
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

LAX = "pydantic coerces this type; the port requires the JSON type Kalshi actually sends"
SCALE = "finer than the fixed-point scale (10^-6 for prices, 10^-2 for counts): refused, never rounded"
HUGE = "does not fit a signed 64-bit integer (Python integers are unbounded)"

TICKER = {"market_ticker": "KXBTCD-26OCT1617-T100000", "market_id": "m-1", "price_dollars": "0.5600", "yes_bid_dollars": "0.5600",
          "yes_ask_dollars": "0.5700", "yes_bid_size_fp": "120.00", "yes_ask_size_fp": "75.50", "volume_fp": "10234.00",
          "open_interest_fp": "4400.00", "last_trade_size_fp": "1.25", "ts_ms": 1791334891978}
TRADE = {"trade_id": "t1", "market_ticker": "A", "yes_price_dollars": "0.5900", "no_price_dollars": "0.4100", "count_fp": "149.83",
         "taker_side": "yes", "is_block_trade": False, "ts_ms": 1791334891978}
LIFECYCLE = {"market_ticker": "A", "event_type": "settled", "open_ts": 1791300000, "close_ts": 1791400000, "determination_ts": 1791400100,
             "settled_ts": 1791400200, "result": "yes", "settlement_value": "1.0000", "is_deactivated": False, "exchange_index": 0,
             "price_level_structure": "linear_cent", "additional_metadata": {"name": "x", "n": [1, 2], "nested": {"a": None}}}
EVENT = {"event_ticker": "EV-1", "series_ticker": "S", "title": "t", "subtitle": "s", "collateral_return_type": "x", "exchange_index": 1}
SNAPSHOT = {"market_ticker": "A", "market_id": "m-1", "yes_dollars_fp": [["0.1400", "5.50"], ["0.1500", "100.00"]],
            "no_dollars_fp": [["0.8000", "1.00"]]}
DELTA = {"market_ticker": "A", "price_dollars": "0.1500", "delta_fp": "-25.50", "side": "yes", "ts_ms": 1791334891978,
         "client_order_id": "c-1"}

DELETE = object()
cases: list[dict] = []
_names: set[tuple[str, str]] = set()


def lab(value) -> str:
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


def envelope(type_: str, msg, **extra) -> dict:
    return {"type": type_, "sid": 3, "seq": 17, "msg": msg, **extra}


def required_and_types(model: str, base: dict, required: list[str], strings: list[str]) -> None:
    for field in required:
        add(model, f"missing-{field}", variant(base, **{field: DELETE}))
        add(model, f"null-{field}", variant(base, **{field: None}))
    for field in strings:
        add(model, f"number-for-{field}", variant(base, **{field: 5}))


def fixed_point(model: str, base: dict, prices: list[str], counts: list[str]) -> None:
    for field in prices:
        for value in ["0.5600", "0.56", "1", "0", "-0.5", "1e-3"]:
            add(model, f"{field}-string-{value}", variant(base, **{field: value}))
        add(model, f"{field}-number-0.5", variant(base, **{field: 0.5}))
        add(model, f"{field}-number-1", variant(base, **{field: 1}))
        add(model, f"{field}-too-fine", variant(base, **{field: "0.1234567"}), "error", SCALE)
        for value in ["", "abc", "NaN", "Infinity"]:
            add(model, f"{field}-bad-{value or 'empty'}", variant(base, **{field: value}))
        add(model, f"{field}-bool", variant(base, **{field: True}))
        add(model, f"{field}-array", variant(base, **{field: ["0.5"]}))
    for field in counts:
        for value in ["10.00", "10", "0.01", "-1.50"]:
            add(model, f"{field}-string-{value}", variant(base, **{field: value}))
        add(model, f"{field}-too-fine", variant(base, **{field: "10.001"}), "error", SCALE)
        add(model, f"{field}-number-10", variant(base, **{field: 10}))
        add(model, f"{field}-empty", variant(base, **{field: ""}))


def integers(model: str, base: dict, fields: list[str]) -> None:
    for field in fields:
        for value, tag in [(1, None), (0, None), (-1, None), (None, None), ("1", "error"), (1.0, "error"), (1.5, None), ("x", None),
                           (True, "error"), (10**30, "error")]:
            why = HUGE if value == 10**30 else LAX
            add(model, f"{field}-{lab(value)[:24]}", variant(base, **{field: value}), tag, why if tag else None)


def build_corpus() -> None:
    # ---- ticker -------------------------------------------------------------------------------------------------------
    add("TickerMsg", "full", TICKER)
    add("TickerMsg", "minimal", {"market_ticker": "A"})
    add("TickerMsg", "unknown-fields", variant(TICKER, brand_new={"nested": [1, 2]}, another=None))
    add("TickerMsg", "all-optionals-null", {"market_ticker": "A", **{k: None for k in TICKER if k != "market_ticker"}})
    required_and_types("TickerMsg", TICKER, ["market_ticker"], ["market_ticker", "market_id"])
    fixed_point("TickerMsg", TICKER, ["price_dollars", "yes_bid_dollars", "yes_ask_dollars"],
                ["yes_bid_size_fp", "yes_ask_size_fp", "volume_fp", "open_interest_fp", "last_trade_size_fp"])
    integers("TickerMsg", TICKER, ["ts_ms"])

    # ---- trade --------------------------------------------------------------------------------------------------------
    add("TradeMsg", "full", TRADE)
    add("TradeMsg", "defaults", {k: v for k, v in TRADE.items() if k not in ("taker_side", "is_block_trade", "ts_ms")})
    add("TradeMsg", "unknown-fields", variant(TRADE, brand_new=1))
    required_and_types("TradeMsg", TRADE, ["trade_id", "market_ticker", "yes_price_dollars", "no_price_dollars", "count_fp"],
                       ["trade_id", "market_ticker", "taker_side"])
    fixed_point("TradeMsg", TRADE, ["yes_price_dollars", "no_price_dollars"], ["count_fp"])
    integers("TradeMsg", TRADE, ["ts_ms"])
    for value, tag in [(True, None), (False, None), (None, None), ("true", "error"), ("false", "error"), (1, "error"), (0, "error"),
                       ("maybe", None)]:
        add("TradeMsg", f"is-block-trade-{lab(value)}", variant(TRADE, is_block_trade=value), tag, LAX if tag else None)
    add("TradeMsg", "taker-side-null", variant(TRADE, taker_side=None))

    # ---- market and multivariate lifecycle ----------------------------------------------------------------------------
    add("LifecycleMsg", "full", LIFECYCLE)
    add("LifecycleMsg", "minimal", {"event_type": "created"})
    add("LifecycleMsg", "unknown-fields", variant(LIFECYCLE, brand_new={"x": 1}))
    add("LifecycleMsg", "all-optionals-null", {"event_type": "created", **{k: None for k in LIFECYCLE if k != "event_type"}})
    required_and_types("LifecycleMsg", LIFECYCLE, ["event_type"], ["event_type", "market_ticker", "result", "settlement_value",
                                                                    "price_level_structure"])
    integers("LifecycleMsg", LIFECYCLE, ["open_ts", "close_ts", "determination_ts", "settled_ts", "exchange_index"])
    for value, tag in [(True, None), (False, None), (None, None), ("true", "error"), (1, "error"), (0, "error")]:
        add("LifecycleMsg", f"is-deactivated-{lab(value)}", variant(LIFECYCLE, is_deactivated=value), tag, LAX if tag else None)
    for value in [{}, {"a": 1}, {"a": {"b": [1, "2", None, True]}}, None, [], "x", 5, [1]]:
        add("LifecycleMsg", f"additional-metadata-{lab(json.dumps(value))[:30]}", variant(LIFECYCLE, additional_metadata=value))
    add("LifecycleMsg", "settlement-value-is-text", variant(LIFECYCLE, settlement_value="0.5000"))

    # ---- event lifecycle ----------------------------------------------------------------------------------------------
    add("EventLifecycleMsg", "full", EVENT)
    add("EventLifecycleMsg", "minimal", {"event_ticker": "E"})
    add("EventLifecycleMsg", "unknown-fields", variant(EVENT, brand_new=[1]))
    required_and_types("EventLifecycleMsg", EVENT, ["event_ticker"], ["event_ticker", "series_ticker", "title", "subtitle",
                                                                        "collateral_return_type"])
    for field in ["title", "subtitle", "collateral_return_type"]:
        add("EventLifecycleMsg", f"{field}-null", variant(EVENT, **{field: None}))
    integers("EventLifecycleMsg", EVENT, ["exchange_index"])

    # ---- order book snapshot ------------------------------------------------------------------------------------------
    add("OrderbookSnapshotMsg", "both-sides", SNAPSHOT)
    add("OrderbookSnapshotMsg", "minimal", {"market_ticker": "A"})
    add("OrderbookSnapshotMsg", "empty-sides", {"market_ticker": "A", "yes_dollars_fp": [], "no_dollars_fp": []})
    add("OrderbookSnapshotMsg", "unknown-fields", variant(SNAPSHOT, brand_new=1))
    required_and_types("OrderbookSnapshotMsg", SNAPSHOT, ["market_ticker"], ["market_ticker", "market_id"])
    for field in ["yes_dollars_fp", "no_dollars_fp"]:
        add("OrderbookSnapshotMsg", f"{field}-null", variant(SNAPSHOT, **{field: None}))
        add("OrderbookSnapshotMsg", f"{field}-object", variant(SNAPSHOT, **{field: {"a": 1}}))
        add("OrderbookSnapshotMsg", f"{field}-row-of-three", variant(SNAPSHOT, **{field: [["0.15", "1", "x"]]}))
        add("OrderbookSnapshotMsg", f"{field}-row-of-one", variant(SNAPSHOT, **{field: [["0.15"]]}))
        add("OrderbookSnapshotMsg", f"{field}-row-is-object", variant(SNAPSHOT, **{field: [{"price": "0.15", "quantity": "1"}]}))
        add("OrderbookSnapshotMsg", f"{field}-price-garbage", variant(SNAPSHOT, **{field: [["abc", "1"]]}))
        add("OrderbookSnapshotMsg", f"{field}-price-too-fine", variant(SNAPSHOT, **{field: [["0.1234567", "1"]]}), "error", SCALE)
        add("OrderbookSnapshotMsg", f"{field}-quantity-too-fine", variant(SNAPSHOT, **{field: [["0.15", "1.234"]]}), "error", SCALE)
        add("OrderbookSnapshotMsg", f"{field}-numbers", variant(SNAPSHOT, **{field: [[0.5, 100]]}))
        add("OrderbookSnapshotMsg", f"{field}-negative-quantity", variant(SNAPSHOT, **{field: [["0.15", "-1.00"]]}))

    # ---- order book delta ---------------------------------------------------------------------------------------------
    add("OrderbookDeltaMsg", "full", DELTA)
    add("OrderbookDeltaMsg", "minimal", {k: v for k, v in DELTA.items() if k not in ("ts_ms", "client_order_id")})
    add("OrderbookDeltaMsg", "positive-delta", variant(DELTA, delta_fp="3.00", side="no"))
    add("OrderbookDeltaMsg", "unknown-fields", variant(DELTA, brand_new=1))
    required_and_types("OrderbookDeltaMsg", DELTA, ["market_ticker", "price_dollars", "delta_fp", "side"],
                       ["market_ticker", "side", "client_order_id"])
    fixed_point("OrderbookDeltaMsg", DELTA, ["price_dollars"], ["delta_fp"])
    integers("OrderbookDeltaMsg", DELTA, ["ts_ms"])
    add("OrderbookDeltaMsg", "client-order-id-null", variant(DELTA, client_order_id=None))

    # ---- the envelope and its typed payload ---------------------------------------------------------------------------
    add("WsMessage", "ticker", envelope("ticker", TICKER, id=None, sending_ts_ms=1791334891999))
    add("WsMessage", "trade", envelope("trade", TRADE))
    add("WsMessage", "market-lifecycle", envelope("market_lifecycle_v2", LIFECYCLE))
    add("WsMessage", "multivariate-lifecycle", envelope("multivariate_market_lifecycle", LIFECYCLE))
    add("WsMessage", "event-lifecycle", envelope("event_lifecycle", EVENT))
    add("WsMessage", "orderbook-snapshot", envelope("orderbook_snapshot", SNAPSHOT))
    add("WsMessage", "orderbook-delta", envelope("orderbook_delta", DELTA))
    add("WsMessage", "subscribed-has-no-typed-payload", {"id": 1, "type": "subscribed", "msg": {"channel": "ticker", "sid": 3}})
    add("WsMessage", "ok-with-markets", {"id": 2, "sid": 3, "type": "ok", "seq": 4, "msg": {"market_tickers": ["A", "B"]}})
    add("WsMessage", "error-reply", {"id": 3, "type": "error", "msg": {"code": 6, "msg": "Already subscribed"}})
    add("WsMessage", "unknown-type-keeps-msg", {"type": "brand_new_type", "msg": {"a": [1, 2], "b": None}})
    add("WsMessage", "no-msg", {"type": "subscribed"})
    add("WsMessage", "msg-null", {"type": "subscribed", "msg": None})
    add("WsMessage", "msg-is-a-list-of-an-unknown-type", {"type": "weird", "msg": [1, 2]})
    add("WsMessage", "unknown-fields", variant(envelope("ticker", TICKER), brand_new=1))
    add("WsMessage", "missing-type", {"msg": {}})
    add("WsMessage", "null-type", {"type": None, "msg": {}})
    add("WsMessage", "type-is-number", {"type": 5, "msg": {}})
    for field in ["id", "sid", "seq", "sending_ts_ms"]:
        for value, tag in [(1, None), (None, None), ("1", "error"), (1.5, None), ("x", None), (True, "error")]:
            add("WsMessage", f"{field}-{lab(value)}", envelope("subscribed", {}, **{field: value}), tag, LAX if tag else None)
    add("WsMessage", "sid-too-big-for-int", envelope("subscribed", {}, sid=2**40), "error", "sid is a 32-bit integer in the port")
    add("WsMessage", "seq-is-64-bit", envelope("subscribed", {}, seq=2**40))
    for kind in ["ticker", "trade", "market_lifecycle_v2", "event_lifecycle", "orderbook_snapshot", "orderbook_delta"]:
        add("WsMessage", f"{kind}-payload-missing", {"type": kind})
        add("WsMessage", f"{kind}-payload-null", {"type": kind, "msg": None})
        add("WsMessage", f"{kind}-payload-empty-object", {"type": kind, "msg": {}})
        add("WsMessage", f"{kind}-payload-a-list", {"type": kind, "msg": []})
    add("WsMessage", "ticker-price-too-fine", envelope("ticker", variant(TICKER, price_dollars="0.1234567")), "error", SCALE)


def reference_outcome(ws, entry: dict):
    model, payload = entry["model"], entry["payload"]
    try:
        if model == "WsMessage":
            message = ws.WsMessage.model_validate(payload)
            typed = message.payload()
            out = message.model_dump(mode="json", exclude={"received_at"})
            out["payload"] = None if typed is None else typed.model_dump(mode="json")
            return out
        return getattr(ws, model).model_validate(payload).model_dump(mode="json")
    except Exception:  # ValidationError, TypeError ...
        return "error"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    ws = load_reference_module(args.reference, "ws_models")
    build_corpus()
    for entry in cases:
        entry["expect"] = reference_outcome(ws, entry)
        # A deliberately stricter port only differs where the reference succeeds.
        if entry.get("port") == "error" and entry["expect"] == "error":
            del entry["port"], entry["why"]

    out = args.out or default_output(__file__, "ws_models.json", "KalTerm.Kalshi.Tests")
    write_vectors(out, "tools/vectors/generate_ws_models.py", args.reference, {"cases": cases})
    by_model: dict[str, list[int]] = {}
    for entry in cases:
        bucket = by_model.setdefault(entry["model"], [0, 0])
        bucket[0 if entry["expect"] != "error" else 1] += 1
    summary = ", ".join(f"{m} {ok}ok/{err}err" for m, (ok, err) in by_model.items())
    deviations = sum(1 for e in cases if "port" in e)
    print(f"wrote {out}: {len(cases)} cases ({summary}); {deviations} deliberate port differences")


if __name__ == "__main__":
    main()
