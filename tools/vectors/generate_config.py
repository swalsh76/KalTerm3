"""Generate golden vectors for KalTerm.Kalshi.Configuration from the Python reference (kalshi_core.config).

    python tools/vectors/generate_config.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

The reference's ``config.py`` needs pydantic to import, so it is read statically with ``ast`` instead: the
URL maps, the field defaults, the constraints and the variable prefix are extracted from the source itself,
so a typo in the C# port's URLs (demo versus production is a safety matter) is caught against the original.
The budget table applies the reference's own formula, ``read_bucket``: ``tokens_per_second * rate_limit_margin``.
"""

import argparse
import ast
from pathlib import Path

from common import default_output, write_vectors

BUDGETS = [(200, 0.9), (600, 0.9), (100, 0.9), (200, 1), (150, 0.85), (50.5, 0.5), (1000, 0.1)]


def config_tree(reference: Path) -> ast.Module:
    path = reference / "packages" / "kalshi-core" / "src" / "kalshi_core" / "config.py"
    return ast.parse(path.read_text(encoding="utf-8"))


def find_class(tree: ast.Module, name: str) -> ast.ClassDef:
    return next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == name)


def environment_members(tree: ast.Module) -> dict[str, str]:
    """``DEMO = "demo"`` inside ``class Environment`` -> {"DEMO": "demo"}."""
    members = {}
    for stmt in find_class(tree, "Environment").body:
        if isinstance(stmt, ast.Assign) and isinstance(stmt.targets[0], ast.Name):
            members[stmt.targets[0].id] = ast.literal_eval(stmt.value)
    return members


def resolve_environment(node: ast.expr, members: dict[str, str]) -> str:
    assert isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "Environment"
    return members[node.attr]


def url_map(tree: ast.Module, name: str, members: dict[str, str]) -> dict[str, str]:
    for node in tree.body:
        target = node.target if isinstance(node, ast.AnnAssign) else (node.targets[0] if isinstance(node, ast.Assign) else None)
        if isinstance(target, ast.Name) and target.id == name:
            assert isinstance(node.value, ast.Dict)
            return {resolve_environment(k, members): ast.literal_eval(v) for k, v in zip(node.value.keys, node.value.values)}
    raise LookupError(name)


def field_info(call: ast.Call) -> dict[str, str]:
    """``Field(default=200, gt=0)`` -> {"default": "200", "gt": "0"} (as written in the source)."""
    return {kw.arg: repr(ast.literal_eval(kw.value)) for kw in call.keywords}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    tree = config_tree(args.reference)
    members = environment_members(tree)
    rest, ws = url_map(tree, "REST_URLS", members), url_map(tree, "WS_URLS", members)
    assert set(rest) == set(ws) == set(members.values())

    settings = find_class(tree, "KalshiSettings")
    prefix = None
    fields: dict[str, ast.expr] = {}
    for stmt in settings.body:
        if isinstance(stmt, ast.Assign) and isinstance(stmt.targets[0], ast.Name) and stmt.targets[0].id == "model_config":
            prefix = ast.literal_eval(next(k.value for k in stmt.value.keywords if k.arg == "env_prefix"))
        elif isinstance(stmt, ast.AnnAssign) and isinstance(stmt.target, ast.Name):
            fields[stmt.target.id] = stmt.value

    defaults = {"env": resolve_environment(fields["env"], members)}
    constraints = {}
    for name in ("read_tokens_per_second", "write_tokens_per_second", "rate_limit_margin"):
        info = field_info(fields[name])
        defaults[name] = info["default"]
        constraints[name] = {k: v for k, v in info.items() if k != "default"}

    budgets = [{"tokens": repr(t), "margin": repr(m), "budget": repr(t * m)} for t, m in BUDGETS]

    out = args.out or default_output(__file__, "config.json", "KalTerm.Kalshi.Tests")
    write_vectors(out, "tools/vectors/generate_config.py", args.reference, {
        "variablePrefix": prefix,
        "environments": {env: {"rest": rest[env], "webSocket": ws[env]} for env in sorted(rest)},
        "defaults": defaults,
        "constraints": constraints,
        "budgets": budgets,
    })
    print(f"wrote {out}: {len(rest)} environments, {len(defaults)} defaults, {len(budgets)} budget cases, prefix {prefix}")


if __name__ == "__main__":
    main()
