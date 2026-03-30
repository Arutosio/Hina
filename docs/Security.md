# Security

This document describes Hina's security model in detail, including manifest signing, chunk integrity verification, the threat model, and best practices for secure deployments.

---

## Overview

Hina provides two layers of integrity protection:

1. **Manifest signing** -- Ed25519 digital signatures ensure the manifest has not been tampered with since it was built.
2. **Chunk and file hashing** -- SHA256 hashes verify that every chunk and every reconstructed file matches the original build output.

Together, these mechanisms protect against man-in-the-middle attacks, corrupted downloads, and partial update failures.

---

## Ed25519 Manifest Signing

### How It Works

Hina uses Ed25519, a high-performance elliptic curve signature scheme from the Edwards-curve Digital Signature Algorithm family. The implementation is provided by the [NSec.Cryptography](https://github.com/ektrah/nsec) library (libsodium-based).

The signing process during a build:

1. The builder constructs the complete manifest (version, build ID, base URL, file list with all chunk hashes).
2. The manifest is serialized to canonical JSON **without the signature field** (the `Signature` property is set to `null` and excluded via `JsonIgnoreCondition.WhenWritingNull`).
3. The canonical JSON bytes are signed with the Ed25519 private key.
4. The signature, algorithm identifier, and public key are attached to the manifest as the `Signature` object.
5. The signed manifest is written to disk.

### What Is Signed

The signed payload is a JSON serialization of the manifest with the following properties:

| Field | Included | Notes |
|-------|----------|-------|
| `Version` | Yes | Release version string |
| `BuildId` | Yes | UTC build timestamp |
| `BaseUrl` | Yes | Base URL for chunk retrieval |
| `Files` | Yes | Complete file list with all chunk metadata |
| `Signature` | **No** | Excluded from the signed payload |

The canonical form uses compact JSON (no indentation) with null properties omitted. This ensures the exact same byte sequence is produced during both signing and verification.

### Manifest Signature Object

```json
{
  "signature": {
    "algorithm": "ed25519",
    "signature": "BASE64_ENCODED_SIGNATURE",
    "publicKey": "BASE64_ENCODED_PUBLIC_KEY"
  }
}
```

| Field | Description |
|-------|-------------|
| `algorithm` | Always `"ed25519"`. Reserved for future algorithm support. |
| `signature` | Base64-encoded 64-byte Ed25519 signature. |
| `publicKey` | Base64-encoded 32-byte Ed25519 public key that produced this signature. |

---

## Key Generation and Management

### Generating Keys

Use the builder's `keygen` command:

```shell
hina-builder keygen --out ./keys --name production
```

This produces two files:

| File | Contents | Size |
|------|----------|------|
| `production.key.b64` | Base64-encoded Ed25519 private key | 44 characters (32 bytes encoded) |
| `production.pub.b64` | Base64-encoded Ed25519 public key | 44 characters (32 bytes encoded) |

### Key File Format

Both files contain a single line of Base64 text with no headers, no PEM wrapping, and no metadata. Example:

```
# production.key.b64 (KEEP SECRET)
kG7wQf3N...base64...==

# production.pub.b64 (distribute to clients)
Ry4jF2m8...base64...==
```

### Signing a Build

Pass the private key file to the builder:

```shell
hina-builder build \
  --input ./game-client \
  --out ./build-output \
  --base https://patch.example.com/ \
  --sign-key ./keys/production.key.b64 \
  --version 1.2.0
```

The builder reads the private key, signs the manifest, and embeds the signature in the output `manifest.json`.

---

## Signature Verification Flow

On the client side, verification happens automatically when `TrustedPublicKey` is configured:

```
Client starts CheckAsync / PatchAsync / VerifyAsync
  |
  v
Download manifest.json from server
  |
  v
Is TrustedPublicKey configured?
  |--- No  --> Skip verification, proceed
  |--- Yes --> Verify signature
                  |
                  v
                Serialize manifest without Signature field (canonical JSON)
                  |
                  v
                Verify Ed25519 signature against canonical bytes
                using the trusted public key
                  |
                  |--- Valid   --> Proceed with operation
                  |--- Invalid --> Throw InvalidDataException
                  |--- Missing --> Throw InvalidDataException (no signature present)
```

The client uses the **trusted public key from its configuration**, not the public key embedded in the manifest. The embedded public key exists for informational purposes (e.g., identifying which key signed a manifest) but is never used for verification decisions.

### Configuring the Trusted Public Key

**CLI:**

```shell
hina patch --dir ./game --base https://patch.example.com/ \
  --pubkey "Ry4jF2m8...base64...=="
```

**Config file:**

```json
{
  "baseUrl": "https://patch.example.com/",
  "trustedPublicKey": "Ry4jF2m8...base64...=="
}
```

**Programmatic:**

```csharp
var config = new PatcherConfig
{
    BaseUrl = new Uri("https://patch.example.com/"),
    TrustedPublicKey = "Ry4jF2m8...base64...=="
};
```

---

## Chunk Integrity

### Per-Chunk Strong Hash

Every chunk in the manifest includes a SHA256 strong hash (prefixed with `sha256:`). When the client downloads a chunk from the server, the chunk is identified by this hash in the URL path:

```
chunks/<first-2-chars-of-hash>/<full-hash>.chunk.br
```

This is a content-addressed storage scheme. The chunk's name is its hash, so any corruption or tampering changes the hash and produces a file-not-found error or a hash mismatch.

### File Integrity

After reconstructing a file from its chunks (mixing locally matched chunks with downloaded ones), the client computes the SHA256 hash of the complete file and compares it against the `fileHash` in the manifest.

This verification is controlled by the `verify` config property (default: `true`). If the hash does not match, the client throws an `InvalidDataException` and initiates rollback.

### Verification Pipeline

```
For each file in manifest:
  |
  v
  Rsync-match local chunks (weak hash -> strong hash confirmation)
  |
  v
  Download missing chunks from server (Brotli-compressed)
  |
  v
  Reconstruct file from chunks into temp file (.hina.tmp)
  |
  v
  Compute SHA256 of reconstructed file
  |
  v
  Compare against manifest fileHash
  |--- Match    --> Replace original with reconstructed file
  |--- Mismatch --> Throw InvalidDataException, trigger rollback
```

---

## Threat Model

### Attacks Hina Protects Against

| Threat | Protection Mechanism |
|--------|---------------------|
| **MITM manifest tampering** | Ed25519 signature verification rejects modified manifests when a trusted public key is configured. |
| **MITM chunk tampering** | Content-addressed chunk URLs and SHA256 file verification detect any modification to chunk data. |
| **Chunk corruption in transit** | SHA256 file hash verification catches corrupted downloads. Retry policy re-downloads failed chunks. |
| **Partial/interrupted updates** | Journaled patch sessions detect incomplete updates and roll back automatically on next run. |
| **Replay attacks (old manifest)** | Version field and BuildId timestamp allow clients to detect stale manifests. Application-level version checks can reject downgrades. |
| **Directory traversal** | Path normalization (`PathUtils.ToOsPath`) prevents chunks or manifest entries from escaping the root directory. |

### Attacks Hina Does NOT Protect Against

| Threat | Why | Mitigation |
|--------|-----|------------|
| **Compromised build server** | If an attacker controls the build pipeline, they can produce a validly signed malicious manifest using the legitimate private key. | Secure your CI/CD pipeline. Use hardware security modules (HSMs) for key storage. Implement code review and build reproducibility. |
| **Private key theft** | A stolen private key allows an attacker to sign arbitrary manifests. | Store keys in HSMs or encrypted vaults. Rotate keys periodically. Limit key access to the build system. |
| **Compromised client binary** | If the attacker replaces the patcher itself, all verification is bypassed. | Code-sign the patcher binary. Distribute the patcher through trusted channels. |
| **Denial of service** | An attacker can block access to the patch server. | Use CDNs with DDoS protection. Implement client-side retry logic (built-in). |
| **Local file system attacks** | A local attacker with write access to the game directory can modify files after patching. | OS-level file permissions. Full-disk encryption. Run verification before launch. |

---

## Best Practices

### Key Rotation

1. Generate a new key pair with `hina-builder keygen`.
2. Update your build pipeline to sign with the new private key.
3. Deploy a client update that includes the new public key in its configuration.
4. Retire the old key pair.

Plan key rotations in advance. Since clients must be updated to trust the new key, coordinate the rollout with a client update cycle.

### Secure Key Storage

| Environment | Recommendation |
|-------------|----------------|
| Development | Key files on disk with restricted permissions (`chmod 600`). |
| CI/CD | Encrypted secrets (GitHub Actions secrets, Azure Key Vault, AWS Secrets Manager). Inject the key as an environment variable or temporary file. |
| Production | Hardware Security Module (HSM) or cloud KMS for highest security. |

Never commit private keys to version control. Add `*.key.b64` to your `.gitignore`.

### TLS for Transport

Hina's signing and hashing protect integrity and authenticity, but not confidentiality. Always serve patch files over HTTPS in production to prevent:

- Eavesdropping on update traffic patterns
- Network-level interference with downloads
- ISP or proxy injection into HTTP responses

### Signature Enforcement

In production, always set `TrustedPublicKey` in the client configuration. Without it, the client accepts any manifest regardless of signature, which negates the entire signing mechanism.

### Verify Flag

Keep `verify: true` (the default) in production. The post-patch file hash verification is the last line of defense against corrupted or incomplete reconstructions. The performance cost is minimal compared to the safety benefit.

### Backup and Rollback

Keep `backup: true` (the default) in production. This ensures that a failed patch can be rolled back to the previous state rather than leaving the client in a broken half-patched state.
