"""Generate golden vectors for KalTerm.Contracts.ApiFormat from the Python reference.

The reference's API formatting module (``kalshiterm_server/api/format.py``) is the oracle. It is loaded by path
(no install; it needs only ``kalshi_core.fixedpoint``, which is standard library). The output is committed, so CI
never needs Python; rerun this only when the reference changes.

    python tools/vectors/generate_api_format.py --reference %USERPROFILE%\\dev\\KalshiTerm2

Case kinds
  dollars, count   stored integer (or null) -> the decimal string the API sends
  moments          an aware UTC time as ISO text -> the text the API sends (microsecond precision, +00:00)
  cursors          text -> the opaque cursor; and cursor -> the text inside it, or null when it is not one of ours
"""

import argparse
import importlib.util
import sys
from datetime import datetime
from pathlib import Path

from common import default_output, load_reference_module, write_vectors

DOLLAR_INPUTS = [
    None, 0, 1, -1, 9, 10, 99, 100, 999, 1000, 560000, -560000, 999999, 1000000, -1000000, 1000001, 1234567,
    41000, 410000, 59000, 123456789012, -123456789012, 9223372036854775807, -9223372036854775807,
]
COUNT_INPUTS = [
    None, 0, 1, -1, 9, 10, 99, 100, 101, -101, 1850, 123400, 9900, 10000, 1000000, 9223372036854775807,
    -9223372036854775807,
]
MOMENTS = [
    "2026-10-18T22:23:48.123000+00:00",
    "2026-10-18T22:23:48+00:00",
    "2026-01-01T00:00:00+00:00",
    "2026-12-31T23:59:59.999000+00:00",
    "2026-10-18T22:23:48.000001+00:00",
    "2000-02-29T12:00:00.500000+00:00",
    "2026-10-18T16:23:48.123000-06:00",
]
CURSOR_TEXTS = [
    "A", "KXHIGHNY-26OCT18-T70", "2026-10-18T22:23:48.123000+00:00|0000-trade", "a|b|c", "ü", "market with spaces",
    "x" * 50, "?>~", "\u00e9\u00e8",
]
# Cursors to decode: some ours, some not. Characters outside the URL-safe alphabet are left out on purpose:
# the reference's decoder also accepts the standard alphabet's '+' and '/', which this API never produces.
CURSOR_INPUTS = [
    "QQ", "QQ==", "S1hISUdI", "YnxjfGQ", "w7w", "!!!", "====", "A", "AA", "QUJD", "QUJDRA", "QUJDRA==", "*", "",
    "-_-_", "AAAA", "gA", "_w", "wA",
]


def load_format(reference: Path):
    load_reference_module(reference, "fixedpoint")  # puts kalshi-core on sys.path
    path = reference / "packages" / "kalshiterm-server" / "src" / "kalshiterm_server" / "api" / "format.py"
    spec = importlib.util.spec_from_file_location("reference_api_format", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["reference_api_format"] = module
    spec.loader.exec_module(module)
    return module


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--reference", type=Path, required=True, help="path to the KalshiTerm2 clone")
    parser.add_argument("--output", type=Path, default=None)
    args = parser.parse_args()
    fmt = load_format(args.reference)

    dollars = [{"input": None if v is None else str(v), "expected": fmt.dollars(v)} for v in DOLLAR_INPUTS]
    counts = [{"input": None if v is None else str(v), "expected": fmt.count(v)} for v in COUNT_INPUTS]
    moments = [{"input": m, "expected": fmt.moment(datetime.fromisoformat(m))} for m in MOMENTS]
    encoded = [{"text": t, "cursor": fmt.encode_cursor(t)} for t in CURSOR_TEXTS]
    decoded = [{"cursor": c, "text": fmt.decode_cursor(c)} for c in CURSOR_INPUTS]

    output = args.output or default_output(__file__, "api_format.json", "KalTerm.Contracts.Tests")
    write_vectors(
        output,
        "tools/vectors/generate_api_format.py",
        args.reference,
        {"dollars": dollars, "counts": counts, "moments": moments, "cursorEncodings": encoded, "cursorDecodings": decoded},
    )
    print(f"wrote {output} ({len(dollars)} dollars, {len(counts)} counts, {len(moments)} moments, "
          f"{len(encoded)} encodings, {len(decoded)} decodings)")


if __name__ == "__main__":
    main()
