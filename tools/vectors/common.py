"""Helpers shared by the vector generators: load the Python reference and write a vector file."""

import importlib
import json
import subprocess
import sys
from pathlib import Path

INT64_MAX = 2**63 - 1


def load_reference_module(reference: Path, name: str):
    """Import ``kalshi_core.<name>`` from the local KalshiTerm2 clone (read-only, standard library only)."""
    source = reference / "packages" / "kalshi-core" / "src"
    if str(source) not in sys.path:
        sys.path.insert(0, str(source))
    return importlib.import_module(f"kalshi_core.{name}")


def reference_commit(reference: Path) -> str:
    return subprocess.run(
        ["git", "-C", str(reference), "rev-parse", "HEAD"], capture_output=True, text=True, check=True
    ).stdout.strip()


def default_output(script: str, filename: str, project: str = "KalTerm.Core.Tests") -> Path:
    """tests/<project>/Vectors/<filename>, relative to the repository root."""
    root = Path(script).resolve().parents[2]
    return root / "tests" / project / "Vectors" / filename


def write_vectors(path: Path, generator: str, reference: Path, body: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    document = {
        "generator": generator,
        "referenceCommit": reference_commit(reference),
        "python": sys.version.split()[0],
        **body,
    }
    path.write_text(json.dumps(document, indent=1, ensure_ascii=True) + "\n", encoding="utf-8")


def outcome(fn, *args) -> str:
    """The reference's answer as a string: the integer; ``refuse`` for ValueError (out of domain);
    ``overflow`` when the integer does not fit a signed 64-bit long (Python is unbounded)."""
    try:
        result = fn(*args)
    except ValueError:
        return "refuse"
    if isinstance(result, int) and not -INT64_MAX <= result <= INT64_MAX:
        return "overflow"
    return str(result)
