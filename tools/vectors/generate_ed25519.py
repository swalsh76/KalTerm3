"""Generate golden vectors for KalTerm.Kalshi.Auth from the Python reference (kalshi_core.auth).

    python tools/vectors/generate_ed25519.py --reference C:\\Users\\stebe\\dev\\KalshiTerm2

Needs the ``cryptography`` package (the reference signs with it); the generated JSON is committed, so CI never
runs Python. Ed25519 (RFC 8032) is deterministic, so a fixed seed and message always give the same signature.

  rawSign   RFC 8032 section 7.1 and Kalshi-shaped messages: seed -> PKCS#8 PEM, public key, signature
            (the same 21 vectors as spike S-3).
  keyFiles  PEM texts run through the reference ``load_private_key``: loads (with its public key) or the error
            category it raises (``not-pem``, ``unsupported-type`` with the key family).
  headers   ``KalshiSigner.headers()`` from the reference over methods, paths (queries, non-ASCII, empty) and
            timestamps.
"""

import argparse
import base64
import re
import tempfile
from pathlib import Path

from common import default_output, load_reference_module, stub_settings_module, write_vectors
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec, rsa
from cryptography.hazmat.primitives.asymmetric.ed448 import Ed448PrivateKey
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey

stub_settings_module()

# RFC 8032 section 7.1: (seed, message hex, expected signature hex)
RFC8032 = [
    ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "",
     "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
    ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "72",
     "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
    ("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "af82",
     "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
]

TS = 1_700_000_000_000
RAW_MESSAGES = [
    ("get-balance", f"{TS}GET/trade-api/v2/portfolio/balance"),
    ("get-query-stripped", f"{TS}GET/trade-api/v2/markets"),
    ("post-order", f"{TS}POST/trade-api/v2/portfolio/orders"),
    ("delete-order", f"{TS}DELETE/trade-api/v2/portfolio/orders/abc-123"),
    ("empty-path", f"{TS}GET"),
    ("non-ascii", f"{TS}GET/trade-api/v2/markets/caf\u00e9"),
]
KALSHI_SEEDS = [bytes([i] * 32) for i in (1, 2, 255)]

METHODS = ["GET", "get", "POST", "DELETE"]
PATHS = [
    "/trade-api/v2/portfolio/balance",
    "/trade-api/v2/markets?limit=5&cursor=x",
    "/trade-api/v2/markets/caf\u00e9",
    "",
    "/",
    "?only=query",
    "/a?b?c",
]
TIMESTAMPS = [TS, 1]
KEY_ID = "key-id-123"


def pem_private(key, password: bytes | None = None) -> str:
    encryption = serialization.BestAvailableEncryption(password) if password else serialization.NoEncryption()
    return key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, encryption).decode()


def public_hex(key: Ed25519PrivateKey) -> str:
    return key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()


def raw_vector(name: str, seed: bytes, message: bytes, expected_hex: str | None = None) -> dict:
    key = Ed25519PrivateKey.from_private_bytes(seed)
    signature = key.sign(message)
    if expected_hex is not None:
        assert signature.hex() == expected_hex, f"{name}: library disagrees with RFC 8032"
    return {
        "name": name, "seedHex": seed.hex(), "publicHex": public_hex(key), "pkcs8Pem": pem_private(key),
        "messageHex": message.hex(), "signatureHex": signature.hex(),
        "signatureBase64": base64.b64encode(signature).decode(),
    }


def key_family(python_type: str) -> str:
    """The family name KalTerm reports for the reference's Python key class (class names vary by version)."""
    for marker, family in (("RSA", "RSA"), ("Ed448", "Ed448"), ("X25519", "X25519"), ("EC", "EC"), ("Elliptic", "EC")):
        if marker in python_type:
            return family
    raise LookupError(f"no family for {python_type}")


def classify(auth, text: str) -> dict:
    """Run PEM text through the reference loader and describe the outcome."""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "key.pem"
        path.write_text(text, encoding="utf-8", newline="")  # keep the exact line endings
        try:
            key = auth.load_private_key(path)
        except auth.AuthError as error:
            message = str(error)
            if "unsupported key type" in message:
                return {"expect": "unsupported-type", "pythonType": re.search(r"unsupported key type (\w+)", message).group(1)}
            if "is not an unencrypted PEM private key" in message:
                return {"expect": "not-pem"}
            raise
        return {"expect": "ok", "publicHex": public_hex(key)}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, type=Path, help="local clone of KalshiTerm2")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    auth = load_reference_module(args.reference, "auth")

    raw = [raw_vector(f"rfc8032-test{i}", bytes.fromhex(seed), bytes.fromhex(msg), sig)
           for i, (seed, msg, sig) in enumerate(RFC8032, start=1)]
    for seed in KALSHI_SEEDS:
        raw += [raw_vector(f"kalshi-{name}-seed{seed[0]}", seed, text.encode()) for name, text in RAW_MESSAGES]

    good = Ed25519PrivateKey.from_private_bytes(bytes([7] * 32))
    good_pem = pem_private(good)
    candidates = {
        "plain": good_pem,
        "crlf-line-endings": good_pem.replace("\n", "\r\n"),
        "leading-blank-lines": "\n\n" + good_pem,
        "text-before-the-block": "# my data key\n" + good_pem,
        "text-after-the-block": good_pem + "# end\n",
        "no-trailing-newline": good_pem.rstrip("\n"),
        "rsa": pem_private(rsa.generate_private_key(65537, 2048)),
        "ec-p256": pem_private(ec.generate_private_key(ec.SECP256R1())),
        "ed448": pem_private(Ed448PrivateKey.generate()),
        "x25519": pem_private(X25519PrivateKey.generate()),
        "encrypted-ed25519": pem_private(good, b"pw"),
        "garbage": "not a key",
        "empty": "",
        "whitespace-only": "  \n\t\n",
        "public-key-only": good.public_key().public_bytes(
            serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode(),
        "traditional-label": good_pem.replace("PRIVATE KEY", "RSA PRIVATE KEY"),
        "broken-base64": good_pem.replace("MC4CAQAwBQYDK2VwBCIEI", "!!!!!!!!!!!!!!!!!!!!!"),
        # An Ed25519 PEM body is one line; cut it short so the DER inside is incomplete.
        "truncated-body": "-----BEGIN PRIVATE KEY-----\n" + good_pem.splitlines()[1][:40] + "\n-----END PRIVATE KEY-----\n",
        "single-line-body": "\n".join(good_pem.splitlines()[:2] + ["-----END PRIVATE KEY-----"]) + "\n",
    }
    key_files = []
    for name, text in candidates.items():
        entry = {"name": name, "pem": text, **classify(auth, text)}
        if entry["expect"] == "unsupported-type":
            entry["family"] = key_family(entry["pythonType"])
        key_files.append(entry)

    signers = [{"name": f"seed{seed[0]}", "pkcs8Pem": pem_private(Ed25519PrivateKey.from_private_bytes(seed))}
               for seed in KALSHI_SEEDS]
    headers = []
    for seed, signer_entry in zip(KALSHI_SEEDS, signers):
        reference_signer = auth.KalshiSigner(KEY_ID, Ed25519PrivateKey.from_private_bytes(seed))
        for method in METHODS:
            for path in PATHS:
                for timestamp in TIMESTAMPS:
                    headers.append({
                        "signer": signer_entry["name"], "keyId": KEY_ID, "method": method, "path": path,
                        "timestamp": timestamp,
                        "headers": reference_signer.headers(method, path, timestamp_ms=timestamp),
                    })

    out = args.out or default_output(__file__, "ed25519.json", "KalTerm.Kalshi.Tests")
    write_vectors(out, "tools/vectors/generate_ed25519.py", args.reference, {
        "headerNames": [auth.HEADER_KEY, auth.HEADER_TIMESTAMP, auth.HEADER_SIGNATURE],
        "rawSign": raw,
        "keyFiles": key_files,
        "signers": signers,
        "headers": headers,
    })
    outcomes = {}
    for entry in key_files:
        outcomes[entry["expect"]] = outcomes.get(entry["expect"], 0) + 1
    print(f"wrote {out}: {len(raw)} raw signatures, {len(key_files)} key files {outcomes}, {len(headers)} header cases")


if __name__ == "__main__":
    main()
