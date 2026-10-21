"""Generate golden vectors for KalTerm.Core.RateLimiting from the Python reference (kalshi_core.ratelimit).

    python tools/vectors/generate_ratelimit.py --reference %USERPROFILE%\\dev\\KalshiTerm2

Each scenario builds the reference TokenBucket under its deterministic fake clock (whose sleep advances time),
runs a list of operations and records the clock after every one. The reference computes in floats; the C#
port is exact to a 100 ns tick (a wait is rounded up), so the tests allow that rounding per wait.

Operations: ["acquire", cost], ["idle", seconds], ["concurrent", [costs...]] (started together, served in order).
``ratelimit.py`` imports pydantic through ``kalshi_core.config`` for its settings factories; the bucket itself
does not use it, so that one module is stubbed here to keep the generator standard-library-only.
"""

import argparse
import asyncio
import random
from pathlib import Path

from common import default_output, load_reference_module, stub_settings_module, write_vectors

# Settings-based factories are exercised in the KalTerm.Kalshi tests, not here.
stub_settings_module()


class FakeTime:
    """Deterministic clock whose sleep just advances time (the reference test's own helper)."""

    def __init__(self) -> None:
        self.now = 0.0

    def clock(self) -> float:
        return self.now

    async def sleep(self, seconds: float) -> None:
        self.now += seconds


async def run(ratelimit, rate: float, capacity: float, ops: list) -> list[str]:
    t = FakeTime()
    bucket = ratelimit.TokenBucket(rate, capacity, clock=t.clock, sleep=t.sleep)
    after = []
    for op in ops:
        if op[0] == "acquire":
            await bucket.acquire(op[1])
        elif op[0] == "idle":
            t.now += op[1]
        elif op[0] == "concurrent":
            await asyncio.gather(*(bucket.acquire(c) for c in op[1]))
        after.append(repr(t.now))
    return after


def reference_scenarios() -> list[dict]:
    """The reference's own tests as scenarios."""
    ten = [["acquire", 10]] * 10
    return [
        {"name": "burst-up-to-capacity-is-immediate", "rate": 100, "capacity": 100, "ops": ten},
        {"name": "waits-for-refill-when-empty", "rate": 100, "capacity": 100, "ops": ten + [["acquire", 10]]},
        {"name": "sustained-throughput-matches-rate", "rate": 100, "capacity": 100, "ops": [["acquire", 10]] * 100},
        {"name": "refill-is-capped-at-capacity", "rate": 100, "capacity": 100,
         "ops": [["acquire", 100], ["idle", 60], ["acquire", 100], ["acquire", 10]]},
        {"name": "waiters-served-in-order", "rate": 10, "capacity": 10,
         "ops": [["acquire", 10], ["concurrent", [10, 10, 10, 10, 10]]]},
    ]


def random_scenarios() -> list[dict]:
    rng = random.Random(8032)  # fixed seed: the output must be reproducible
    shapes = [(100, 100), (180, 180), (200, 200), (540, 540), (10, 10), (100, 500), (50.5, 50.5), (12.5, 100)]
    scenarios = []
    for index in range(40):
        rate, capacity = shapes[index % len(shapes)]
        affordable = lambda costs: [c for c in costs if c <= capacity]  # a cost above capacity is refused
        ops: list = []
        for _ in range(30):
            kind = rng.choices(["acquire", "idle", "concurrent"], weights=[6, 2, 2])[0]
            if kind == "acquire":
                ops.append(["acquire", rng.choice(affordable([1, 2, 2.5, 5, 10, 10, 10, 25, 50]) + [capacity])])
            elif kind == "idle":
                ops.append(["idle", rng.choice([0.01, 0.1, 0.5, 1, 2.5, 30])])
            else:
                ops.append(["concurrent", [rng.choice(affordable([5, 10, 10, 20])) for _ in range(rng.randint(2, 5))]])
        scenarios.append({"name": f"random-{index:02d}-rate{rate}-cap{capacity}", "rate": rate,
                          "capacity": capacity, "ops": ops})
    return scenarios


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    ratelimit = load_reference_module(args.reference, "ratelimit")

    scenarios = []
    for s in reference_scenarios() + random_scenarios():
        s["nowAfter"] = asyncio.run(run(ratelimit, s["rate"], s["capacity"], s["ops"]))
        scenarios.append(s)

    def refused(call) -> bool:
        try:
            asyncio.run(call())
        except ValueError:
            return True
        return False

    constructor = []
    for rate, capacity in [(0, 10), (10, 0), (-1, 10), (10, -1), (10, 10), (0.5, 0.5)]:
        constructor.append({"rate": rate, "capacity": capacity,
                            "expect": "refuse" if refused(lambda r=rate, c=capacity: _construct(ratelimit, r, c)) else "ok"})
    acquire = []
    for cost in [101, 100.5, 0, -5, 100, 10, 0.5]:
        acquire.append({"rate": 100, "capacity": 100, "cost": cost,
                        "expect": "refuse" if refused(lambda c=cost: _acquire(ratelimit, c)) else "ok"})

    out = args.out or default_output(__file__, "ratelimit.json")
    write_vectors(out, "tools/vectors/generate_ratelimit.py", args.reference, {
        "scenarios": scenarios,
        "constructor": constructor,
        "acquire": acquire,
    })
    print(f"wrote {out}: {len(scenarios)} scenarios, {len(constructor)} constructor cases, {len(acquire)} acquire cases")


async def _construct(ratelimit, rate, capacity) -> None:
    ratelimit.TokenBucket(rate, capacity)


async def _acquire(ratelimit, cost) -> None:
    t = FakeTime()
    await ratelimit.TokenBucket(100, 100, clock=t.clock, sleep=t.sleep).acquire(cost)


if __name__ == "__main__":
    main()
