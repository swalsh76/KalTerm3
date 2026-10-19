"""Generate golden vectors for KalTerm.Kalshi.OrderBook (the book builder) from the Python reference (kalshi_core.orderbook.BookTracker).

    python tools/vectors/generate_orderbook.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

Needs the reference's dependencies (pydantic, pydantic-settings, cryptography, httpx, websockets) in a throwaway venv outside the repo. The
generated JSON is committed, so CI never runs Python.

Each scenario is a list of steps fed to a fresh ``BookTracker``: a stream message, or one of the calls the feed makes around the tracker
(begin/end subscription, markets added or removed, a REST snapshot). After every step the vector records what the reference answered: the
events it returned, every book it holds and the set of stale markets. Prices are written with six decimals and quantities with two, the
fixed-point scales of the port, so the two sides compare as exact text.
"""

import argparse
import sys
from decimal import Decimal
from pathlib import Path

from common import default_output, write_vectors

P = Decimal("0.000001")
Q = Decimal("0.01")


def price(value: Decimal) -> str:
    return format(value.quantize(P), "f")


def quantity(value: Decimal) -> str:
    return format(value.quantize(Q), "f")


def snap(sid, seq, ticker, yes, no, **extra):
    return {"type": "orderbook_snapshot", "sid": sid, "seq": seq, "msg": {"market_ticker": ticker, "market_id": "m-" + ticker, "yes_dollars_fp": yes, "no_dollars_fp": no, **extra}}


def delta(sid, seq, ticker, side, at, change):
    return {"type": "orderbook_delta", "sid": sid, "seq": seq, "msg": {"market_ticker": ticker, "price_dollars": at, "delta_fp": change, "side": side, "ts_ms": 1791334891978}}


def control(sid, seq):
    return {"type": "control", "sid": sid, "seq": seq}


RECONNECTED = {"type": "reconnected", "msg": {"resubscribed": [{"channel": "orderbook_delta", "sid": 7, "old_sid": 1}], "failed": [], "reason": "connection_lost", "dropped": 3}}
TICKER = {"type": "ticker", "sid": 2, "seq": 9, "msg": {"market_ticker": "A", "price_dollars": "0.5600"}}


def message(m):
    return {"op": "message", "message": m}


def begin(sid, tickers):
    return {"op": "begin_subscription", "sid": sid, "tickers": tickers}


def end(sid):
    return {"op": "end_subscription", "sid": sid}


def add_markets(sid, tickers):
    return {"op": "begin_markets", "sid": sid, "tickers": tickers}


def forget(sid, tickers):
    return {"op": "forget_markets", "sid": sid, "tickers": tickers}


def rest(ticker, yes, no):
    return {"op": "rest_snapshot", "ticker": ticker, "yes": yes, "no": no}


A_YES = [["0.1400", "5.50"], ["0.1500", "100.00"], ["0.1300", "40.00"]]
A_NO = [["0.8000", "1.00"], ["0.8100", "12.25"]]

SCENARIOS = {
    "a snapshot builds a book and its derived asks": [
        message(snap(1, 1, "A", A_YES, A_NO)),
    ],
    "deltas add, grow, shrink and remove levels": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(delta(1, 2, "A", "yes", "0.1600", "10.00")),
        message(delta(1, 3, "A", "yes", "0.1500", "-25.50")),
        message(delta(1, 4, "A", "yes", "0.1500", "25.50")),
        message(delta(1, 5, "A", "yes", "0.1500", "-100.00")),
        message(delta(1, 6, "A", "no", "0.8200", "3.00")),
        message(delta(1, 7, "A", "no", "0.8200", "-3.00")),
    ],
    "zero quantity levels in a snapshot are dropped and empty sides are fine": [
        message(snap(1, 1, "A", [["0.1400", "0.00"], ["0.1500", "2.00"]], [])),
        message(snap(1, 2, "B", [], [])),
    ],
    "a snapshot replaces the book": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "A", [["0.2000", "1.00"]], [["0.7000", "2.00"]])),
    ],
    "two markets share one subscription and one sequence": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "B", [["0.5000", "7.00"]], [["0.4000", "8.00"]])),
        message(delta(1, 3, "B", "yes", "0.5100", "1.00")),
        message(delta(1, 4, "A", "yes", "0.1400", "-5.50")),
    ],
    "a skipped sequence number marks every market of the subscription stale until a fresh snapshot": [
        begin(1, ["A", "B"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "B", [["0.5000", "7.00"]], [["0.4000", "8.00"]])),
        message(delta(1, 3, "A", "yes", "0.1600", "1.00")),
        message(delta(1, 5, "A", "yes", "0.1700", "1.00")),
        message(delta(1, 6, "B", "yes", "0.5200", "1.00")),
        message(snap(1, 7, "A", [["0.9000", "1.00"]], [])),
        message(delta(1, 8, "A", "yes", "0.9100", "2.00")),
        message(delta(1, 9, "B", "yes", "0.5300", "2.00")),
        message(snap(1, 10, "B", [["0.5500", "3.00"]], [])),
    ],
    "a delta that would make a quantity negative is a gap and the book goes stale": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(delta(1, 2, "A", "yes", "0.1400", "-5.51")),
        message(delta(1, 3, "A", "yes", "0.1400", "1.00")),
        message(snap(1, 4, "A", A_YES, A_NO)),
        message(delta(1, 5, "A", "yes", "0.1400", "-5.50")),
    ],
    "a negative delta on a missing level is a gap too": [
        message(snap(1, 1, "A", [], [])),
        message(delta(1, 2, "A", "no", "0.5000", "-1.00")),
    ],
    "command replies use sequence numbers and control markers keep the count": [
        begin(1, ["A"]),
        message(control(1, 1)),
        message(snap(1, 2, "A", A_YES, A_NO)),
        message(control(1, 3)),
        message(delta(1, 4, "A", "yes", "0.1600", "1.00")),
    ],
    "a control marker out of step is a gap": [
        begin(1, ["A", "B"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "B", [["0.5000", "7.00"]], [])),
        message(control(1, 5)),
        message(delta(1, 6, "A", "yes", "0.1600", "1.00")),
    ],
    "a control marker for a subscription nobody declared is ignored": [
        message(control(9, 4)),
        message(snap(1, 1, "A", A_YES, A_NO)),
    ],
    "a reconnect clears every book and the sequence starts again": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(delta(1, 2, "A", "yes", "0.1600", "1.00")),
        message(RECONNECTED),
        message(snap(7, 1, "A", [["0.3000", "4.00"]], [])),
        message(delta(7, 2, "A", "yes", "0.3100", "1.00")),
    ],
    "messages of other types pass through untouched and do not move the book sequence": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(TICKER),
        message(delta(1, 2, "A", "yes", "0.1600", "1.00")),
    ],
    "a delta for a market with no book is ignored but counted": [
        message(delta(1, 1, "A", "yes", "0.1600", "1.00")),
        message(snap(1, 2, "A", A_YES, A_NO)),
        message(delta(1, 3, "A", "yes", "0.1600", "1.00")),
    ],
    "subscriptions have independent sequences": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(2, 1, "B", [["0.5000", "7.00"]], [])),
        message(delta(1, 2, "A", "yes", "0.1600", "1.00")),
        message(delta(2, 2, "B", "yes", "0.5100", "1.00")),
        message(delta(2, 4, "B", "yes", "0.5200", "1.00")),
        message(delta(1, 3, "A", "yes", "0.1700", "1.00")),
    ],
    "a declared subscription marks its markets stale until they have snapshots": [
        begin(1, ["A", "B"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(delta(1, 2, "B", "yes", "0.5000", "1.00")),
        message(snap(1, 3, "B", [["0.5000", "7.00"]], [])),
    ],
    "messages that overtake the declaration keep what they established": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        begin(1, ["A", "B"]),
        message(delta(1, 2, "A", "yes", "0.1600", "1.00")),
    ],
    "markets added to a live subscription are stale until their snapshot": [
        begin(1, ["A"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        add_markets(1, ["B"]),
        message(delta(1, 2, "B", "yes", "0.5000", "1.00")),
        message(snap(1, 3, "B", [["0.5000", "7.00"]], [])),
        message(delta(1, 4, "B", "yes", "0.5100", "1.00")),
    ],
    "a removed market drops its book and its late messages count in the sequence but are ignored": [
        begin(1, ["A", "B"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "B", [["0.5000", "7.00"]], [])),
        forget(1, ["B"]),
        message(delta(1, 3, "B", "yes", "0.5100", "1.00")),
        message(delta(1, 4, "A", "yes", "0.1600", "1.00")),
    ],
    "a removed market can be added back": [
        begin(1, ["A", "B"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(snap(1, 2, "B", [["0.5000", "7.00"]], [])),
        forget(1, ["B"]),
        add_markets(1, ["B"]),
        message(snap(1, 3, "B", [["0.6000", "1.00"]], [])),
    ],
    "ending a subscription forgets its sequence": [
        begin(1, ["A"]),
        message(snap(1, 1, "A", A_YES, A_NO)),
        end(1),
        message(snap(1, 5, "A", [["0.2000", "1.00"]], [])),
    ],
    "a REST snapshot is approximate and clears a stale mark": [
        message(snap(1, 1, "A", A_YES, A_NO)),
        message(delta(1, 3, "A", "yes", "0.1600", "1.00")),
        rest("A", [["0.1400", "5.50"], ["0.1700", "9.00"]], [["0.8000", "1.00"]]),
        message(snap(1, 4, "A", A_YES, A_NO)),
    ],
    "a message without a sequence number is out of step": [
        message({"type": "orderbook_snapshot", "sid": 1, "msg": {"market_ticker": "A", "yes_dollars_fp": [["0.1000", "1.00"]], "no_dollars_fp": []}}),
        message(delta(1, 1, "A", "yes", "0.1100", "1.00")),
    ],
    "a stream that does not start at one is a gap on its first message only when declared": [
        begin(1, ["A"]),
        message(snap(1, 3, "A", A_YES, A_NO)),
        message(delta(1, 4, "A", "yes", "0.1600", "1.00")),
    ],
    "prices and quantities are exact at the finest scale": [
        message(snap(1, 1, "A", [["0.000001", "0.01"], ["0.999999", "123456789.99"]], [["0.500000", "0.01"]])),
        message(delta(1, 2, "A", "yes", "0.000001", "0.01")),
        message(delta(1, 3, "A", "yes", "0.999999", "-123456789.99")),
    ],
}


def book_json(book) -> dict:
    return {
        "yes": [[price(p), quantity(q)] for p, q in book.bids("yes")],
        "no": [[price(p), quantity(q)] for p, q in book.bids("no")],
        "approximate": bool(book.approximate),
    }


def event_json(event) -> dict:
    return {
        "kind": event.kind,
        "ticker": event.ticker,
        "tickers": list(event.tickers),
        "detail": event.detail,
        "book": book_json(event.book) if event.book is not None else None,
        "messageType": event.message.type if event.message is not None else None,
    }


def run(orderbook, ws_models, models, steps: list) -> list:
    tracker = orderbook.BookTracker()
    out = []
    for step in steps:
        events = []
        op = step["op"]
        if op == "message":
            events = tracker.process(ws_models.WsMessage.model_validate(step["message"]))
        elif op == "begin_subscription":
            tracker.begin_subscription(step["sid"], list(step["tickers"]))
        elif op == "end_subscription":
            tracker.end_subscription(step["sid"])
        elif op == "begin_markets":
            tracker.begin_markets(step["sid"], list(step["tickers"]))
        elif op == "forget_markets":
            tracker.forget_markets(step["sid"], list(step["tickers"]))
        elif op == "rest_snapshot":
            book = models.OrderBook.from_response({"orderbook_fp": {"yes_dollars": step["yes"], "no_dollars": step["no"]}})
            events = [tracker.apply_rest_snapshot(step["ticker"], book)]
        else:
            raise AssertionError(op)
        out.append(
            {
                "events": [event_json(e) for e in events],
                "books": {t: book_json(b) for t, b in sorted(tracker.books.items())},
                "stale": sorted(tracker.stale),
            }
        )
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--reference", type=Path, required=True, help="path to the KalshiTerm2 clone")
    parser.add_argument("--output", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.reference / "packages" / "kalshi-core" / "src"))
    from kalshi_core import models, orderbook, ws_models

    scenarios = [{"name": name, "steps": steps, "expect": run(orderbook, ws_models, models, steps)} for name, steps in SCENARIOS.items()]
    assert len({s["name"] for s in scenarios}) == len(scenarios)
    output = args.output or default_output(__file__, "orderbook.json", "KalTerm.Kalshi.Tests")
    write_vectors(output, "tools/vectors/generate_orderbook.py", args.reference, {"scenarios": scenarios})
    steps = sum(len(s["steps"]) for s in scenarios)
    print(f"wrote {output} ({len(scenarios)} scenarios, {steps} steps)")


if __name__ == "__main__":
    main()
