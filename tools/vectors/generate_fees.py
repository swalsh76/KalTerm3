"""Generate golden vectors for KalTerm.Core.Fees from the Python reference (kalshi_core.fees).

    python tools/vectors/generate_fees.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

Expectation strings: an integer; ``refuse`` (outside the domain: ValueError in the reference); or
``overflow`` (Python's unbounded integer result does not fit a signed 64-bit long, so the C# port
raises OverflowException instead of returning a value).
"""

import argparse
import random
from pathlib import Path

from common import INT64_MAX, default_output, load_reference_module, outcome, write_vectors

MULTIPLIERS = [0, 1, 7, 1_000, 500_000, 1_000_000, 1_234_567, 2_000_000, 10_000_000]
COUNTS = [0, 1, 2, 3, 7, 99, 100, 101, 777, 2_550, 10_000, 1_000_000, 10**9]
PRICES = [0, 1, 2, 10_000, 55_000, 123_456, 333_333, 500_000, 666_667, 999_998, 999_999, 1_000_000]

# Outside the domain: negative anything, price above one dollar.
REFUSED = [
    (1_000_000, 100, -1), (1_000_000, 100, 1_000_001), (1_000_000, -1, 5), (-1, 1, 5),
    (-1, -1, -1), (1_000_000, 100, -1_000_000), (1_000_000, 100, 2_000_000),
]

# Large enough that Python's answer exceeds a signed 64-bit long (or the 128-bit working range).
EXTREMES = [
    (10**12, 10**12, 500_000), (10**9, 10**9, 500_000), (10**13, 10**9, 500_000),
    (INT64_MAX, INT64_MAX, 500_000), (INT64_MAX, 1, 500_000), (1, INT64_MAX, 500_000),
    (INT64_MAX, INT64_MAX, 1), (10**18, 10**18, 250_000),
]

FEE_TYPES = [
    "quadratic", "quadratic_with_maker_fees", "quadratic_with_combo_maker_fees",
    "flat", "margin_market_maker_program_fees", "mystery", "", "Quadratic", None,
]
MULTIPLIER_VALUES = [None, -1, 0, 1, 500_000, 1_000_000, 123_456_789]

ROUND_VALUES = [0, 1, 99, 100, 101, 3_639, 17_500, 20_000, 999_999, 1_000_000, 10**9, INT64_MAX - 1,
                INT64_MAX, -1, -99, -100, -101, -17_500, -INT64_MAX]
ROUND_UNITS = [1, 2, 3, 100, 10_000, 1_000_000, 0, -1, -10_000]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    fees = load_reference_module(args.reference, "fees")

    triples = [(m, c, p) for m in MULTIPLIERS for c in COUNTS for p in PRICES]
    rng = random.Random(8032)  # fixed seed: the output must be reproducible
    triples += [(rng.randrange(0, 5_000_000), rng.randrange(0, 2_000_000), rng.randrange(0, 1_000_001))
                for _ in range(400)]
    triples += REFUSED + EXTREMES
    taker_fee = [
        {"multiplier": m, "count": c, "price": p, "expect": outcome(fees.taker_fee_e6, m, c, p)}
        for m, c, p in triples
    ]

    round_up = [
        {"value": v, "unit": u, "expect": outcome(fees.round_up, v, u)}
        for v in ROUND_VALUES for u in ROUND_UNITS
    ]

    multipliers = []
    for fee_type in FEE_TYPES:
        for value in MULTIPLIER_VALUES:
            result = fees.taker_multiplier(fee_type, value)
            multipliers.append({"feeType": fee_type, "multiplier": value,
                                "expect": "null" if result is None else str(result)})

    out = args.out or default_output(__file__, "fees.json")
    write_vectors(out, "tools/vectors/generate_fees.py", args.reference, {
        "quadraticFamily": sorted(fees.QUADRATIC_FAMILY),
        "takerFee": taker_fee,
        "roundUp": round_up,
        "takerMultiplier": multipliers,
    })
    counts = {k: sum(1 for t in taker_fee if t["expect"] == k) for k in ("refuse", "overflow")}
    print(f"wrote {out}: {len(taker_fee)} fee cases ({counts['refuse']} refused, {counts['overflow']} overflow), "
          f"{len(round_up)} round-up cases, {len(multipliers)} multiplier cases")


if __name__ == "__main__":
    main()
