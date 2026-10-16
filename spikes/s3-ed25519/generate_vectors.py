"""Generate Ed25519 golden vectors for spike S-3.

Run with a Python that has `cryptography` installed. Ed25519 (RFC 8032) is deterministic, so a fixed
seed and message always give the same signature. The vectors cover the RFC 8032 section 7.1 tests
and Kalshi-shaped messages (timestamp + METHOD + path, query string stripped), plus negative key
files the loader must reject.
"""

import base64
import json
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec, rsa
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

# RFC 8032 section 7.1: (seed, message hex, expected signature hex)
RFC8032 = [
    (
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
    ),
    (
        "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00",
    ),
    (
        "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
        "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a",
    ),
]

TS = 1_700_000_000_000
KALSHI_MESSAGES = [
    ("get-balance", f"{TS}GET/trade-api/v2/portfolio/balance"),
    ("get-query-stripped", f"{TS}GET/trade-api/v2/markets"),  # path given as /markets?limit=5
    ("post-order", f"{TS}POST/trade-api/v2/portfolio/orders"),
    ("delete-order", f"{TS}DELETE/trade-api/v2/portfolio/orders/abc-123"),
    ("empty-path", f"{TS}GET"),
    ("non-ascii", f"{TS}GET/trade-api/v2/markets/café"),
]
KALSHI_SEEDS = [bytes([i] * 32) for i in (1, 2, 255)]


def pem_private(key: object, password: bytes | None = None) -> str:
    enc = (
        serialization.BestAvailableEncryption(password)
        if password
        else serialization.NoEncryption()
    )
    return key.private_bytes(  # type: ignore[attr-defined]
        serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, enc
    ).decode()


def public_hex(key: Ed25519PrivateKey) -> str:
    return key.public_key().public_bytes(
        serialization.Encoding.Raw, serialization.PublicFormat.Raw
    ).hex()


def vector(name: str, seed: bytes, message: bytes, expected_hex: str | None = None) -> dict:
    key = Ed25519PrivateKey.from_private_bytes(seed)
    sig = key.sign(message)
    if expected_hex is not None:
        assert sig.hex() == expected_hex, f"{name}: library disagrees with RFC 8032 vector"
    return {
        "name": name,
        "seedHex": seed.hex(),
        "publicHex": public_hex(key),
        "pkcs8Pem": pem_private(key),
        "messageHex": message.hex(),
        "signatureHex": sig.hex(),
        "signatureBase64": base64.b64encode(sig).decode(),
    }


def main() -> None:
    vectors = []
    for i, (seed, msg, sig) in enumerate(RFC8032, start=1):
        vectors.append(vector(f"rfc8032-test{i}", bytes.fromhex(seed), bytes.fromhex(msg), sig))
    for seed in KALSHI_SEEDS:
        for name, text in KALSHI_MESSAGES:
            vectors.append(vector(f"kalshi-{name}-seed{seed[0]}", seed, text.encode()))

    ed = Ed25519PrivateKey.from_private_bytes(bytes([7] * 32))
    negatives = [
        {"name": "rsa-key", "pem": pem_private(rsa.generate_private_key(65537, 2048))},
        {"name": "ec-key", "pem": pem_private(ec.generate_private_key(ec.SECP256R1()))},
        {"name": "encrypted-ed25519", "pem": pem_private(ed, b"pw")},
        {"name": "garbage", "pem": "not a key"},
        {"name": "empty", "pem": ""},
        {
            "name": "public-key-only",
            "pem": ed.public_key()
            .public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)
            .decode(),
        },
    ]

    out = Path(__file__).with_name("vectors.json")
    out.write_text(json.dumps({"vectors": vectors, "negatives": negatives}, indent=2) + "\n")
    print(f"wrote {out} ({len(vectors)} vectors, {len(negatives)} negatives)")


if __name__ == "__main__":
    main()
