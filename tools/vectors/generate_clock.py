"""Generate golden vectors for KalTerm.Core.Clock from the Python reference (kalshi_core.clock).

    python tools/vectors/generate_clock.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

``clock.py`` is standard-library only, so it is imported and called directly as the oracle.

  estimates   sample sets (built like the reference test's ``simulate``) -> offset, interval, consistency, status.
              The reference computes in floats (about 0.2 microseconds of noise at today's epoch); the port is exact
              in 100 ns ticks and the tests allow 2 microseconds. Cases where noise could legitimately flip a status
              (an interval edge within 10 microseconds of a threshold), or tip a two-decimal rounding tie in the
              description, are dropped here and counted in ``dropped``.
  describe    ``ClockSkew(...).describe()`` for hand-built values.
  httpDates   ``parse_http_date`` over valid and invalid strings. Python's parser is more lenient than the three
              formats HTTP requires receivers to accept; where the port is stricter the entry has ``csharp``.
"""

import argparse
import math
import random
import re
from email.utils import formatdate
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

THRESHOLDS = [2.0, 1.0, 5.0]
OFFSETS = [0.0, 0.3, -0.8, 3.7, -4.2, 60.0, 2.0, -2.0, 1.5, -1.5, 2.5, -2.5, 0.05, -0.05, 10.0, -10.0]
# Round-trip times chosen so the half-width (1 s + rtt) / 2 never lands on a two-decimal rounding tie
# (0.05 would give exactly 0.525, a tie, and such cases are dropped).
RTTS = [0.04, 0.06, 0.2, 0.002, 1.0]
BOUNDARY_MARGIN = 1e-5   # seconds: drop a case if an interval edge is this close to a threshold
ROUNDING_MARGIN = 1e-4   # drop a case if a two-decimal rounding sits this close to a tie


# The shapes the C# parser accepts, mirrored here so deviations from Python are recorded as data. They are the
# three formats HTTP requires receivers to take (RFC 7231 section 7.1.1.1), matched case-insensitively with a
# valid weekday name (not cross-checked against the date, as Python does not either), GMT only, no surrounding
# whitespace.
_DAY = r"(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)"
_LONG_DAY = r"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)"
_MONTH = r"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)"
_TIME = r"\d{2}:\d{2}:\d{2}"
PORT_FORMATS = [
    re.compile(rf"^{_DAY}, \d{{1,2}} {_MONTH} \d{{4}} {_TIME} GMT$", re.IGNORECASE),        # IMF-fixdate
    re.compile(rf"^{_LONG_DAY}, \d{{1,2}}-{_MONTH}-\d{{2}} {_TIME} GMT$", re.IGNORECASE),   # RFC 850
    re.compile(rf"^{_DAY} {_MONTH} +\d{{1,2}} {_TIME} \d{{4}}$", re.IGNORECASE),            # asctime
]


def port_accepts(text: str) -> bool:
    return any(pattern.fullmatch(text) for pattern in PORT_FORMATS)


def build_samples(clock, offset: float, phases: list[float], rtt: float):
    """Samples against a server whose clock is ``offset`` seconds behind ours (the reference test's helper)."""
    out = []
    for phase in phases:
        sent = 1_700_000_000 + phase
        received = sent + rtt
        server_at_stamp = (sent + received) / 2 - offset
        out.append(clock.ClockSample(sent, received, math.floor(server_at_stamp)))  # Date truncates
    return out


def ambiguous(skew, threshold: float) -> str | None:
    """Why this result could differ between float and exact arithmetic, or None if it cannot."""
    for edge in (skew.low, skew.high):
        for bound in (threshold, -threshold):
            if abs(edge - bound) < BOUNDARY_MARGIN:
                return "status"
    for value in (abs(skew.offset), skew.uncertainty):
        if abs((value * 100) % 1 - 0.5) < ROUNDING_MARGIN:
            return "rounding"
    return None


def sample_entry(sample) -> dict:
    return {"sent": repr(sample.sent), "received": repr(sample.received), "server": repr(sample.server)}


def estimate_entry(clock, name: str, samples, threshold: float, dropped: dict) -> dict | None:
    skew = clock.estimate_skew(samples, threshold)
    reason = ambiguous(skew, threshold)
    if reason:
        dropped[reason] = dropped.get(reason, 0) + 1
        return None
    return {
        "name": name, "threshold": repr(threshold), "samples": [sample_entry(s) for s in samples],
        "offset": repr(skew.offset), "uncertainty": repr(skew.uncertainty), "low": repr(skew.low),
        "high": repr(skew.high), "consistent": skew.consistent, "status": skew.status, "describe": skew.describe(),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    clock = load_reference_module(args.reference, "clock")
    rng = random.Random(8032)  # fixed seed: the output must be reproducible
    dropped: dict = {}
    estimates = []

    def add(name, samples, threshold=2.0):
        entry = estimate_entry(clock, name, samples, threshold, dropped)
        if entry:
            estimates.append(entry)

    # The reference test's own scenarios.
    for offset in OFFSETS[:6]:
        for threshold in (2.0,):
            add(f"five-spaced-offset{offset}", build_samples(clock, offset, [0.0, 0.25, 0.5, 0.75, 1.0], 0.05), threshold)
    add("one-sample-0.3", build_samples(clock, 0.3, [0.1], 0.05))
    add("near-threshold-uncertain", build_samples(clock, 2.0, [0.1], 0.05))

    # Seeded variety: offsets x round-trip times x thresholds x sample counts and spacings.
    for index in range(260):
        offset = rng.choice(OFFSETS) + rng.uniform(-0.3, 0.3)
        rtt = rng.choice(RTTS)
        count = rng.choice([1, 2, 3, 5, 8])
        phases = sorted(rng.uniform(0, 10) for _ in range(count))
        add(f"random-{index:03d}", build_samples(clock, offset, phases, rtt), rng.choice(THRESHOLDS))

    # Samples that disagree (for example a cached reply): the reference falls back to the median.
    for index in range(40):
        good = build_samples(clock, rng.choice(OFFSETS), sorted(rng.uniform(0, 10) for _ in range(rng.randint(1, 4))), rng.choice(RTTS))
        stale = clock.ClockSample(good[0].sent + 1, good[0].received + 1, good[0].server - rng.choice([5, 10, 30]))
        add(f"inconsistent-{index:02d}", good + [stale], rng.choice(THRESHOLDS))

    errors = []
    try:
        clock.estimate_skew([])
    except clock.ClockCheckError:
        errors.append({"name": "no-samples", "expect": "error"})

    describe = []
    for args_ in [(3.0, 0.2, 2.8, 3.2, 5, True, 2.0), (-1.0, 0.2, -1.2, -0.8, 5, True, 2.0),
                  (0.0, 0.5, -0.5, 0.5, 1, True, 2.0), (0.3, 1.5, -1.2, 1.8, 3, False, 2.0),
                  (-60.0, 0.01, -60.01, -59.99, 12, True, 2.0), (2.0, 0.3, 1.7, 2.3, 2, True, 2.0)]:
        skew = clock.ClockSkew(*args_)
        describe.append({"offset": repr(args_[0]), "uncertainty": repr(args_[1]), "low": repr(args_[2]),
                         "high": repr(args_[3]), "samples": args_[4], "consistent": args_[5],
                         "threshold": repr(args_[6]), "status": skew.status, "text": skew.describe()})

    http_dates = []
    for _ in range(40):
        epoch = rng.randrange(1_500_000_000, 2_100_000_000)
        http_dates.append(formatdate(epoch, usegmt=True))
    candidates = http_dates + [
        "Wed, 07 Oct 2026 01:01:31 GMT",                  # the reference test's value
        "Sun, 06 Nov 1994 08:49:37 GMT",                  # RFC 7231 example (preferred form)
        "Sunday, 06-Nov-94 08:49:37 GMT",                 # RFC 7231 example (obsolete RFC 850)
        "Sun Nov  6 08:49:37 1994",                       # RFC 7231 example (obsolete asctime)
        "Thu, 01 Jan 1970 00:00:00 GMT",
        "Wed, 07 Oct 2026 01:01:31 +0000",                # numeric zone: lenient Python only
        "Wed, 07 Oct 2026 03:01:31 +0200",
        "Wed, 07 Oct 2026 01:01:31 -0000",
        "Wed, 07 Oct 2026 01:01:31",                      # no zone
        "Mon, 07 Oct 2026 01:01:31 GMT",                  # wrong weekday (7 Oct 2026 is a Wednesday)
        "7 Oct 2026 01:01:31 GMT",                        # no weekday
        "2026-10-07T01:01:31Z",                           # ISO 8601, not an HTTP date
        "not a date", "", "   ", "Wed, 32 Oct 2026 01:01:31 GMT", "Wed, 07 Foo 2026 01:01:31 GMT",
        "Wed, 07 Oct 2026 25:01:31 GMT", " Wed, 07 Oct 2026 01:01:31 GMT ",
        "Foo, 07 Oct 2026 01:01:31 GMT",                  # not a weekday name: Python skips any word ending in a comma
        "Wed, 7 Oct 2026 01:01:31 GMT",                   # single-digit day
        "wed, 07 oct 2026 01:01:31 gmt",                  # lower case
        "Wed, 07 Oct 2026 01:01:31 UTC",                  # HTTP dates are GMT
        "Sun, 06 Nov 94 08:49:37 GMT",                    # two-digit year in the standard form
        "Sunday, 06-Nov-68 08:49:37 GMT",                 # RFC 850 two-digit years: 68 -> 2068 ...
        "Sunday, 06-Nov-69 08:49:37 GMT",                 # ... 69 -> 1969
        "Sunday, 06-Nov-1994 08:49:37 GMT",               # RFC 850 with a four-digit year
        "Sun Nov 6 08:49:37 1994",                        # asctime, unpadded day
        "Sun Nov  6 08:49:37 1994 ",                      # trailing space
        "Wed, 07 Oct 2026 23:59:60 GMT",                  # a leap second
    ]
    date_entries = []
    for text in candidates:
        try:
            epoch = clock.parse_http_date(text)
            entry = {"text": text, "expect": repr(epoch)}
            if not port_accepts(text):
                entry["csharp"] = "error"   # Python accepts it; the port takes only the three HTTP formats
        except clock.ClockCheckError:
            entry = {"text": text, "expect": "error"}
        date_entries.append(entry)

    out = args.out or default_output(__file__, "clock.json")
    write_vectors(out, "tools/vectors/generate_clock.py", args.reference, {
        "estimates": estimates, "dropped": dropped, "errors": errors, "describe": describe, "httpDates": date_entries,
    })
    kinds = {}
    for e in estimates:
        key = e["status"] + ("" if e["consistent"] else "/inconsistent")
        kinds[key] = kinds.get(key, 0) + 1
    print(f"wrote {out}: {len(estimates)} estimates {kinds}, dropped {dropped}, {len(describe)} describe, "
          f"{len(date_entries)} http dates")


if __name__ == "__main__":
    main()
