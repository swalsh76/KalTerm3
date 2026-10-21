"""Generate golden vectors for KalTerm.Core.Money from the Python reference.

The reference's own ``fixedpoint.py`` is the oracle: it is loaded by path (no install, no
dependencies beyond the standard library) and every expectation is computed by calling it.
The output is committed, so CI never needs Python; rerun this only when the reference changes.

    python tools/vectors/generate_fixedpoint.py --reference %USERPROFILE%\\dev\\KalshiTerm2

Case kinds
  cases          decimal text -> scaled integer, or "refuse". ``csharp`` is set where the C# parser is
                 deliberately stricter than Python's Decimal (whitespace, underscores, non-ASCII digits).
  roundTrips     scaled integer -> exact decimal text from the reference's ``from_e6`` / ``from_e2``.
  unsupported    inputs the reference accepts that KalTerm refuses by design (floating point).
"""

import argparse
import re
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

ASCII_NUMBER = re.compile(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", re.ASCII)

# Text inputs, tried at both scales. Grouped so a failure points at what was being exercised.
TEXT_INPUTS = [
    # plain values, signs, zeros
    "0", "1", "-1", "+1", "0.0", "00.00", "-0", "-0.000000", "+0.5", "0.4100", "0.41", ".5", "1.", "-.5",
    "000123.4500", "7", "104.24", "0.03", "118.7499", "-3.5", "100", "99.99", "0.99", "0.01",
    # the finest ticks
    "0.0001", "0.000001", "0.01", "0.000010", "0.010000",
    # finer than either scale -> refuse
    "0.1234567", "0.0000001", "0.001", "0.123", "1.0000001", "-0.0000001",
    # integer boundaries at e6 and e2
    "9223372036854.775807", "9223372036854.775808", "-9223372036854.775807", "-9223372036854.775808",
    "92233720368547758.07", "92233720368547758.08", "-92233720368547758.07", "-92233720368547758.08",
    "9000000000000", "10000000000000", "9223372036854775807", "99999999999999999999",
    # many digits that are all trailing zeros
    "1.000000000000000000000000000", "0.100000000000000000000000000", "123456789012345678.00",
    "0.000000000000000000000000000000000001",
    # exponent forms
    "1e2", "1E2", "1e-6", "1e-7", "5e-1", "12345e-3", "1.5e1", "-2.5e-1", "1e0", "0e5", "0e-5", "+1e+2",
    "1e30", "1e-30", "1e999999999", "-1e999999999", "1e-999999999", "0e999999999", "0e-999999999",
    "9.223372036854775807e12", "9.223372036854775808e12",
    # not numbers
    "", " ", "abc", "NaN", "nan", "Infinity", "-Infinity", "inf", "-inf", "sNaN", "1.2.3", "--1", "+-1",
    "1e", "e5", "1e+", ".", "+", "-", "0x10", "1,000", "1 000", "$1", "1$", "1f", "1d",
    # Python's Decimal accepts these; the C# parser refuses them (stricter on purpose)
    " 1", "1 ", " 1.5 ", "1_000", "1_0.5", "\u0661\u0662", "\uff11\uff12.\uff15",
]

# Inputs where the reference's answer is wrong by its own rule ("refuse, never round"), so the C# port
# deliberately differs. Python's Decimal underflows at extreme exponents, so a nonzero value times the
# scale quietly becomes 0 and passes the exactness check. Found by this differential test.
REFERENCE_QUIRKS = {
    "1e-999999999": "reference returns 0: Decimal underflows, a nonzero value silently becomes zero",
}

SCALED_INTS = [
    0, 1, -1, 99, 100, 101, 410_000, 999_999, 1_000_000, 1_000_001, -1_000_000, 118_749_900,
    9_223_372_036_854_775_807, -9_223_372_036_854_775_807, 10_424, 3, 700,
]


def oracle(fn, text: str) -> str:
    """The reference's answer as a string: the scaled integer, or ``refuse``."""
    try:
        return str(fn(text))
    except Exception:  # PrecisionError, and anything else the reference cannot represent
        return "refuse"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    ref = load_reference_module(args.reference, "fixedpoint")

    cases = []
    for scale, fn in (("e6", ref.to_e6), ("e2", ref.to_e2)):
        for text in TEXT_INPUTS:
            expect = oracle(fn, text)
            entry = {"scale": scale, "input": text, "expect": expect}
            strict = ASCII_NUMBER.fullmatch(text) is None
            if strict and expect != "refuse":
                entry["csharp"] = "refuse"  # Python accepts; KalTerm is stricter by design
            if text in REFERENCE_QUIRKS:
                entry["csharp"] = "refuse"
                entry["note"] = REFERENCE_QUIRKS[text]
            cases.append(entry)

    round_trips = []
    for scale, to_fn, from_fn in (("e6", ref.to_e6, ref.from_e6), ("e2", ref.to_e2, ref.from_e2)):
        for scaled in SCALED_INTS:
            round_trips.append({"scale": scale, "scaled": str(scaled), "decimal": format(from_fn(scaled), "f")})

    unsupported = []
    for value in (0.41, 0.1 + 0.2, float("inf"), float("nan"), 118.7499, -3.5):
        for scale, fn in (("e6", ref.to_e6), ("e2", ref.to_e2)):
            unsupported.append(
                {"scale": scale, "input": repr(value), "pythonExpect": oracle(lambda v, f=fn: f(value), "")}
            )

    out = args.out or default_output(__file__, "fixedpoint.json")
    write_vectors(out, "tools/vectors/generate_fixedpoint.py", args.reference, {
        "cases": cases,
        "roundTrips": round_trips,
        "unsupported": unsupported,
    })
    refused = sum(1 for c in cases if c["expect"] == "refuse")
    print(f"wrote {out}: {len(cases)} cases ({refused} refused), {len(round_trips)} round trips, "
          f"{len(unsupported)} unsupported-by-design")


if __name__ == "__main__":
    main()
