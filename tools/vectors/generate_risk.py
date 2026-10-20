"""Generate golden vectors for KalTerm.Trading's risk layer from the Python reference (kalshiterm_client.risk).

    python tools/vectors/generate_risk.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

Each case is a full scenario (policy, account snapshot, order, production flag, seconds the snapshot is older than
``now``) with the reference's verdict: whether it was approved, every refusal code and message in order, the worst-case
fee and cost, the funding transfers of an approval, and whether the daily-loss limit tripped the kill switch. The C# port
must agree on all of them, messages included.
"""

import argparse
import importlib.util
import json
import random
import sys
from datetime import UTC, datetime, timedelta
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

NOW = datetime(2026, 10, 13, 12, 0, tzinfo=UTC)
M1 = 1_000_000


def load_risk(reference: Path):
    """The reference's risk module, loaded by path (the client package pulls in more than the risk layer needs)."""
    load_reference_module(reference, "fees")
    load_reference_module(reference, "fixedpoint")
    path = reference / "packages" / "kalshiterm-client" / "src" / "kalshiterm_client" / "risk.py"
    spec = importlib.util.spec_from_file_location("reference_risk", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["reference_risk"] = module
    spec.loader.exec_module(module)
    return module


def scenario(risk, policy: dict, shards: list[dict], markets: dict, intent: dict, *, open_orders: int = 0,
             exposure: int = 0, pnl: int = 0, transferred: int = 0, stream_ok: bool = True, halted: bool = False,
             age_seconds: int = 0, production: bool = False) -> dict:
    """One case: the inputs as plain data, and the reference's verdict on them."""
    pol = risk.RiskPolicy(**policy)
    snapshot = risk.AccountSnapshot(
        as_of=NOW - timedelta(seconds=age_seconds),
        shards={s["index"]: risk.ShardState(s["index"], s["balance"], s["trading"], s["transfers"]) for s in shards},
        markets={t: risk.MarketState(*m) for t, m in markets.items()},
        open_orders=open_orders,
        exposure_e6=exposure,
        realized_pnl_today_e6=pnl,
        transferred_today_e6=transferred,
        stream_ok=stream_ok,
        halted=halted,
    )
    order = risk.OrderIntent(
        intent["ticker"], intent["shard"], risk.Action(intent["action"]), risk.Outcome(intent["outcome"]),
        intent["count"], intent["price"], intent["fee"],
    )
    verdict = risk.check(order, snapshot, pol, now=NOW, production=production)
    return {
        "policy": policy,
        "shards": shards,
        "markets": {t: list(m) for t, m in markets.items()},
        "openOrders": open_orders,
        "exposure": exposure,
        "pnl": pnl,
        "transferred": transferred,
        "streamOk": stream_ok,
        "halted": halted,
        "ageSeconds": age_seconds,
        "production": production,
        "intent": intent,
        "expect": {
            "ok": verdict.ok,
            "refusals": [[r.code.value, r.message] for r in verdict.refusals],
            "fee": verdict.fee_e6,
            "cost": verdict.worst_case_cost_e6,
            "funding": [[t.source, t.destination, t.amount_e6] for t in verdict.funding],
            "tripKill": verdict.trip_kill,
        },
    }


def shards(*balances: int, active: bool = True) -> list[dict]:
    return [{"index": i, "balance": b, "trading": active, "transfers": True} for i, b in enumerate(balances)]


def order(**kw) -> dict:
    base = {"ticker": "KXA-1", "shard": 0, "action": "buy", "outcome": "yes", "count": 100, "price": 500_000, "fee": M1}
    base.update(kw)
    return base


def hand_cases(risk) -> list[dict]:
    s = shards(5_000_000, 0, 0, 0)
    held = {"KXA-1": (300, 100, 0)}
    return [
        scenario(risk, {}, s, {}, order()),
        scenario(risk, {}, s, {}, order(price=900_000)),
        scenario(risk, {}, s, {}, order(action="sell", price=100_000)),
        scenario(risk, {}, s, {}, order(fee=None)),
        scenario(risk, {}, s, {"KXA-1": (300, 0, 0)}, order(action="sell", count=300, price=600_000)),
        scenario(risk, {}, s, {"KXA-1": (100, 0, 0)}, order(action="sell", count=300, price=600_000)),
        scenario(risk, {}, s, {"KXA-1": (-500, 0, 0)}, order(action="sell", count=300, price=600_000)),
        scenario(risk, {}, s, {"KXA-1": (-100, 0, 0)}, order(action="sell", outcome="no", count=100, price=300_000)),
        scenario(risk, {}, s, {}, order(count=600)),
        scenario(risk, {}, s, {}, order(count=300, price=900_000)),
        scenario(risk, {}, s, {}, order(), open_orders=3),
        scenario(risk, {}, s, {}, order(), exposure=2_900_000),
        scenario(risk, {}, s, {}, order(), pnl=-2_000_000),
        scenario(risk, {}, s, {}, order(), pnl=-3_000_000),
        scenario(risk, {}, s, {}, order(), pnl=-1_999_999),
        scenario(risk, {}, s, {}, order(), stream_ok=False),
        scenario(risk, {}, s, {}, order(), halted=True),
        scenario(risk, {}, s, {}, order(), age_seconds=11),
        scenario(risk, {}, s, {}, order(), age_seconds=10),
        scenario(risk, {}, s, {}, order(), age_seconds=-60),
        scenario(risk, {}, s, {}, order(), age_seconds=-5),
        scenario(risk, {}, s, {}, order(shard=9)),
        scenario(risk, {}, shards(5_000_000, 0, 0, 0, active=False), {}, order()),
        scenario(risk, {}, s, {}, order(count=500, price=100_000)),
        scenario(risk, {}, s, {}, order(), open_orders=2),
        scenario(risk, {"max_position_e2": 500, "max_order_notional_e6": 5_000_000}, s, held, order(count=100)),
        scenario(risk, {"max_position_e2": 500, "max_order_notional_e6": 5_000_000}, s, held, order(count=101)),
        scenario(risk, {"max_position_e2": 500, "max_order_notional_e6": 5_000_000}, s, held, order(outcome="no", count=300)),
        scenario(risk, {"max_position_e2": 500, "max_order_notional_e6": 5_000_000}, s, held, order(outcome="no", count=900)),
        scenario(risk, {"max_position_e2": 500, "max_order_notional_e6": 5_000_000}, s, held, order(action="sell", outcome="no", count=101)),
        scenario(risk, {}, s, {}, order(), production=True),
        scenario(risk, {"production_enabled": True}, s, {}, order(), production=True),
        scenario(risk, {}, shards(3_000_000, 0, 1_000_000, 0), {}, order(shard=3)),
        scenario(risk, {}, shards(300_000, 0, 250_000, 0), {}, order(shard=3)),
        scenario(risk, {}, shards(900_000, 5_000_000, 0, 0), {}, order(shard=0)),
        scenario(risk, {}, shards(5_000_000, 0, 0, 0), {}, order(shard=1, count=1, price=333_333)),
        scenario(risk, {}, shards(200_000, 100_000, 0, 0), {}, order(shard=3)),
        scenario(risk, {"max_transfer_per_order_e6": 400_000, "max_transfer_per_day_e6": 1_000_000}, s, {}, order(shard=3)),
        scenario(risk, {"max_transfer_per_order_e6": 600_000, "max_transfer_per_day_e6": 1_000_000}, s, {}, order(shard=3)),
        scenario(risk, {"max_transfer_per_order_e6": 600_000, "max_transfer_per_day_e6": 1_000_000}, s, {}, order(shard=3), transferred=600_000),
        scenario(risk, {}, [{"index": 0, "balance": 5_000_000, "trading": True, "transfers": False}] + shards(5_000_000, 0, 0, 0)[1:], {}, order(shard=3)),
        scenario(risk, {}, shards(5_000_000, 0, 0, 0)[:3] + [{"index": 3, "balance": 0, "trading": True, "transfers": False}], {}, order(shard=3)),
        scenario(risk, {}, shards(150_050, 99_999, 0, 0), {}, order(shard=3, count=260)),
        scenario(risk, {}, s, {}, order(count=600), open_orders=3, stream_ok=False),
    ]


def random_cases(risk, count: int) -> list[dict]:
    rng = random.Random(6)
    cases = []
    for _ in range(count):
        policy = {
            "max_order_count_e2": rng.choice([300, 500, 2_000]),
            "max_position_e2": rng.choice([800, 2_000]),
            "max_order_notional_e6": rng.choice([1_000_000, 5_000_000, 20_000_000]),
            "max_open_exposure_e6": rng.choice([1_000_000, 3_000_000]),
            "max_daily_loss_e6": rng.choice([1_000_000, 2_000_000]),
            "max_open_orders": rng.choice([2, 3, 5]),
            "max_transfer_per_order_e6": rng.choice([500_000, 2_000_000]),
            "max_transfer_per_day_e6": rng.choice([1_000_000, 5_000_000]),
            "production_enabled": rng.random() < 0.8,
        }
        sh = [
            {"index": i, "balance": rng.randrange(0, 4_000_000, 100), "trading": rng.random() < 0.97, "transfers": rng.random() < 0.95}
            for i in range(4)
        ]
        markets = {t: (rng.randint(-400, 400), rng.randint(0, 200), rng.randint(0, 200)) for t in ("T1", "T2")}
        intent = {
            "ticker": rng.choice(["T1", "T2", "T3"]),
            "shard": rng.choice([0, 1, 2, 3, 3, 0, 1, 2, 4]),
            "action": rng.choice(["buy", "sell"]),
            "outcome": rng.choice(["yes", "no"]),
            "count": rng.choice([rng.randint(1, 120), rng.randint(1, 120), rng.randint(1, 300), rng.randint(1, 2_500)]),
            "price": rng.randint(1, 999_999),
            "fee": rng.choice([M1, M1, M1, M1, M1, 2 * M1, 0, None]),
        }
        cases.append(
            scenario(
                risk, policy, sh, markets, intent,
                open_orders=rng.randint(0, 3),
                exposure=rng.randrange(0, 1_500_000, 100),
                pnl=rng.randint(-2_200_000, 500_000),
                transferred=rng.randrange(0, 2_000_000, 100),
                stream_ok=rng.random() < 0.97,
                halted=rng.random() < 0.02,
                age_seconds=rng.randint(-6, 11),
                production=rng.random() < 0.2,
            )
        )
    return cases


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    risk = load_risk(args.reference)
    cases = hand_cases(risk) + random_cases(risk, 1_500)
    approved = sum(1 for c in cases if c["expect"]["ok"])
    out = args.out or default_output(__file__, "risk.json", "KalTerm.Trading.Tests")
    write_vectors(out, "tools/vectors/generate_risk.py", args.reference, {"now": NOW.isoformat(), "cases": cases})
    print(f"wrote {len(cases)} cases ({approved} approved, {len(cases) - approved} refused) to {out}")


if __name__ == "__main__":
    main()
