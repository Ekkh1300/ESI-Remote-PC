#!/usr/bin/env python3
"""Generates the golden test vectors embedded in test/protocol_golden_test.dart.

This is a line-for-line transliteration of the Windows agent's
``src/ESI.RemotePc.Protocol/EsiCrypto.cs`` and ``ProtocolConstants.cs``:

  * ``HkdfExtract``  -> HMACSHA256.HashData(salt, ikm)   (salt is the HMAC *key*)
  * ``HkdfExpand``   -> T(i) = HMAC(PRK, T(i-1) | info | i)
  * ``BuildCanonicalTarget`` / ``BuildCanonicalString`` -> the 8 LF-joined lines
  * ``Sign``         -> base64(HMAC-SHA256(sessionKey, UTF8(canonicalString)))
  * ``Seal``         -> AES-256-GCM(nonce, aad) -> (nonce, ciphertext, tag)

Nothing here is invented: every value traces to a specific line of the C# file, HKDF is
pinned to the published RFC 5869 vectors, and the canonical-string output is additionally
asserted against the worked example in ``docs/PROTOCOL.md`` §4.2 from the Dart test.

Run:  python tool/generate_golden_vectors.py
"""
import base64
import hashlib
import hmac
import json
import sys

SIGNATURE_DOMAIN = "ESIV1"                 # ProtocolConstants.SignatureDomain
HKDF_SALT = "ESI-RemotePC/v1"               # ProtocolConstants.HkdfSalt
EMPTY_BODY_SHA256_B64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="


# --- EsiCrypto.cs --------------------------------------------------------------------

def hkdf_extract(salt: bytes, ikm: bytes) -> bytes:
    """EsiCrypto.HkdfExtract -> HMACSHA256.HashData(salt, ikm)."""
    return hmac.new(salt, ikm, hashlib.sha256).digest()


def hkdf_expand(prk: bytes, info: bytes, length: int) -> bytes:
    """EsiCrypto.HkdfExpand."""
    if length < 1 or length > 255 * 32:
        raise ValueError("HKDF output length must be 1..8160.")
    okm = b""
    previous = b""
    counter = 1
    while len(okm) < length:
        previous = hmac.new(prk, previous + info + bytes([counter]), hashlib.sha256).digest()
        okm += previous
        counter += 1
    return okm[:length]


def hkdf(ikm: bytes, salt: bytes, info: bytes, length: int = 32) -> bytes:
    return hkdf_expand(hkdf_extract(salt, ikm), info, length)


def derive_key(master_secret: bytes, label: str, length: int = 32) -> bytes:
    return hkdf(master_secret, HKDF_SALT.encode("utf-8"), label.encode("utf-8"), length)


def derive_session_key(master_secret, device_id, key_id):
    return derive_key(master_secret, "esi-session-v1|%s|%d" % (device_id, key_id))


def derive_wrap_key(master_secret, device_id):
    return derive_key(master_secret, "esi-wrap-v1|%s" % device_id)


def derive_audit_key(master_secret, device_id, key_id):
    return derive_key(master_secret, "esi-audit-v1|%s|%d" % (device_id, key_id))


def derive_provisioning_key(provisioning_secret, pairing_id):
    return derive_key(provisioning_secret, "esi-provision-v1|%s" % pairing_id)


def build_canonical_target(raw_path: str, raw_query) -> str:
    """EsiCrypto.BuildCanonicalTarget."""
    path = raw_path if raw_path else "/"
    if not raw_query:
        return path
    trimmed = raw_query[1:] if raw_query[0] == "?" else raw_query
    if not trimmed:
        return path
    segments = [s for s in trimmed.split("&") if s]
    # StringComparer.Ordinal sorts by UTF-16 code unit. Every segment the app signs is
    # ASCII (percent-encoded), so Python's default str sort is identical; the Dart test
    # re-checks the specific cases that matter.
    segments.sort()
    return path + "?" + "&".join(segments)


def build_canonical_string(method, canonical_target, timestamp, nonce,
                           content_sha256_b64, device_id, key_id) -> str:
    """EsiCrypto.BuildCanonicalString: 8 lines, LF separated, no trailing newline."""
    return "\n".join([
        SIGNATURE_DOMAIN,
        method.upper(),
        canonical_target,
        str(timestamp),
        nonce,
        content_sha256_b64,
        device_id.lower(),
        str(key_id),
    ])


def sign(session_key: bytes, canonical_string: str) -> str:
    """EsiCrypto.Sign."""
    mac = hmac.new(session_key, canonical_string.encode("utf-8"), hashlib.sha256).digest()
    return base64.b64encode(mac).decode("ascii")


def hash_content(body: bytes) -> str:
    """EsiCrypto.HashContent."""
    if not body:
        return EMPTY_BODY_SHA256_B64
    return base64.b64encode(hashlib.sha256(body).digest()).decode("ascii")


def base64url(data: bytes) -> str:
    """EsiCrypto.Base64Url: standard base64, padding stripped, +/ -> -_."""
    return base64.b64encode(data).decode("ascii").rstrip("=").replace("+", "-").replace("/", "_")


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


# --- RFC 5869 self-check -------------------------------------------------------------
# If HKDF ever diverged from the RFC, every derived key would still agree with this
# generator while disagreeing with the agent. These are the published A.1/A.2/A.3 vectors.
def rfc5869_selfcheck() -> None:
    # A.1 basic, SHA-256, L = 42.
    prk1 = hkdf_extract(bytes.fromhex("000102030405060708090a0b0c"),
                        bytes.fromhex("0b" * 22))
    assert prk1.hex() == (
        "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5"), prk1.hex()
    okm1 = hkdf_expand(prk1, bytes.fromhex("f0f1f2f3f4f5f6f7f8f9"), 42)
    assert okm1.hex() == (
        "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf"
        "34007208d5b887185865"), okm1.hex()

    # A.2 longer inputs, so the expand loop has to produce two blocks.
    prk2 = hkdf_extract(bytes(range(0x60, 0xb0)), bytes(range(0x00, 0x50)))
    assert prk2.hex() == (
        "06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244"), prk2.hex()
    okm2 = hkdf_expand(prk2, bytes(range(0xb0, 0x100)), 82)
    assert okm2.hex() == (
        "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c"
        "59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71"
        "cc30c58179ec3e87c14c01d5c1f3434f1d87"), okm2.hex()

    # A.3 zero-length salt and zero-length info.
    prk3 = hkdf_extract(b"", bytes.fromhex("0b" * 22))
    assert prk3.hex() == (
        "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04"), prk3.hex()
    okm3 = hkdf_expand(prk3, b"", 42)
    assert okm3.hex() == (
        "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d"
        "9d201395faa4b61a96c8"), okm3.hex()


def aes_gcm_seal(plaintext: bytes, key: bytes, aad: bytes, nonce: bytes):
    """EsiCrypto.Seal: 12-byte nonce, 16-byte tag, three separate base64 fields on the wire."""
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    blob = AESGCM(key).encrypt(nonce, plaintext, aad)
    return nonce, blob[:-16], blob[-16:]


# --- The vectors ---------------------------------------------------------------------
DEVICE_ID = "3f2a9c17d4b8e206"       # PROTOCOL.md §3.1 / §4.2
TIMESTAMP = 1756000000               # PROTOCOL.md §4.2
NONCE = "f3Q8Zl0aW1kJ7w3K"           # PROTOCOL.md §4.2

# 32 bytes 0x00..0x1f. Fixed so the derivation can be audited by hand.
MASTER_SECRET = bytes(range(0x00, 0x20))

# 32 bytes 0x20..0x3f.
PROVISIONING_SECRET = bytes(range(0x20, 0x40))

# 32 bytes 0x80..0x9f: the master secret the agent mints during a rotation.
ROTATED_SECRET = bytes(range(0x80, 0xa0))

PAIRING_ID = "b21f0c9d8e7a6b5c"


def main() -> int:
    rfc5869_selfcheck()

    session_key = derive_session_key(MASTER_SECRET, DEVICE_ID, 1)
    wrap_key = derive_wrap_key(MASTER_SECRET, DEVICE_ID)
    audit_key = derive_audit_key(MASTER_SECRET, DEVICE_ID, 1)

    # --- the PROTOCOL.md §4.2 worked example, byte for byte ---------------------------
    target = build_canonical_target("/api/v1/system/info", "detail=full")
    content = EMPTY_BODY_SHA256_B64
    canonical = build_canonical_string("GET", target, TIMESTAMP, NONCE, content, DEVICE_ID, 1)
    sig = sign(session_key, canonical)

    # --- unsorted query with duplicates, to pin the ordinal segment sort ---------------
    messy_target = build_canonical_target(
        "/api/v1/files/search", "?maxResults=50&root=C%3A%5C&query=a&a=2&a=1&")
    messy_canonical = build_canonical_string(
        "get", messy_target, TIMESTAMP, NONCE, content, DEVICE_ID, 12)
    messy_sig = sign(session_key, messy_canonical)

    # --- a request with a body ---------------------------------------------------------
    body = b'{"enabled":true,"profileId":"performance"}'
    body_hash = hash_content(body)
    body_target = build_canonical_target("/api/v1/gaming/mode", None)
    body_canonical = build_canonical_string(
        "POST", body_target, TIMESTAMP, NONCE, body_hash, DEVICE_ID, 1)
    body_sig = sign(session_key, body_canonical)

    # --- pairing unwrap, PROTOCOL.md §5.4 ---------------------------------------------
    pairing_plaintext = MASTER_SECRET + (1).to_bytes(4, "big")
    pairing_aad = ("esi-pair-v1|%s|%s" % (PAIRING_ID, DEVICE_ID)).encode("utf-8")
    prov_key = derive_provisioning_key(PROVISIONING_SECRET, PAIRING_ID)
    n, ct, tag = aes_gcm_seal(pairing_plaintext, prov_key, pairing_aad,
                              bytes(range(0x40, 0x4c)))

    # --- rotation unwrap, PROTOCOL.md §5.3 ---------------------------------------------
    rot_plaintext = ROTATED_SECRET + (2).to_bytes(4, "big")
    rot_aad = ("esi-rotate-v1|%s" % DEVICE_ID).encode("utf-8")
    rn, rct, rtag = aes_gcm_seal(rot_plaintext, wrap_key, rot_aad,
                                 bytes(range(0xa0, 0xac)))

    out = {
        "_comment": "Generated by tool/generate_golden_vectors.py, a transliteration of "
                    "windows-agent/src/ESI.RemotePc.Protocol/EsiCrypto.cs.",
        "deviceId": DEVICE_ID,
        "masterSecretB64": b64(MASTER_SECRET),
        "provisioningSecretB64": b64(PROVISIONING_SECRET),
        "emptyBodySha256B64": EMPTY_BODY_SHA256_B64,
        "sessionKeyB64": b64(session_key),
        "wrapKeyB64": b64(wrap_key),
        "auditKeyB64": b64(audit_key),
        "provisioningKeyB64": b64(prov_key),
        "simple": {
            "method": "GET",
            "path": "/api/v1/system/info",
            "rawQuery": "detail=full",
            "canonicalTarget": target,
            "timestamp": TIMESTAMP,
            "nonce": NONCE,
            "contentSha256B64": content,
            "keyId": 1,
            "canonical": canonical,
            "signatureB64": sig,
        },
        "messyQuery": {
            "method": "get",
            "path": "/api/v1/files/search",
            "rawQuery": "?maxResults=50&root=C%3A%5C&query=a&a=2&a=1&",
            "canonicalTarget": messy_target,
            "timestamp": TIMESTAMP,
            "nonce": NONCE,
            "contentSha256B64": content,
            "keyId": 12,
            "canonical": messy_canonical,
            "signatureB64": messy_sig,
        },
        "withBody": {
            "method": "POST",
            "path": "/api/v1/gaming/mode",
            "rawQuery": None,
            "canonicalTarget": body_target,
            "timestamp": TIMESTAMP,
            "nonce": NONCE,
            "bodyUtf8": body.decode("utf-8"),
            "contentSha256B64": body_hash,
            "keyId": 1,
            "canonical": body_canonical,
            "signatureB64": body_sig,
        },
        "pairingUnwrap": {
            "pairingId": PAIRING_ID,
            "aad": pairing_aad.decode("utf-8"),
            "plaintextB64": b64(pairing_plaintext),
            "nonceB64": b64(n),
            "ciphertextB64": b64(ct),
            "tagB64": b64(tag),
        },
        "rotationUnwrap": {
            "aad": rot_aad.decode("utf-8"),
            "plaintextB64": b64(rot_plaintext),
            "nonceB64": b64(rn),
            "ciphertextB64": b64(rct),
            "tagB64": b64(rtag),
        },
        "base64Url": {
            "allOnes16B64": b64(b"\xff" * 16),
            "allOnes16Base64Url": base64url(b"\xff" * 16),
            "mixedB64": b64(bytes([0xfb, 0xff, 0xbe, 0x00, 0x10])),
            "mixedBase64Url": base64url(bytes([0xfb, 0xff, 0xbe, 0x00, 0x10])),
        },
    }
    json.dump(out, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
