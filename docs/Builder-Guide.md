# Builder Guide

The Hina Builder (`Hina.Builder`) generates a manifest and chunk store from a directory of application files. This document covers all builder commands, flags, chunking modes, output structure, manifest format, and CI/CD integration.

---

## Commands

The builder supports three commands: `init`, `build`, and `keygen`.

```
hina-builder init    [--input <dir>]
hina-builder build   --input <dir> --out <dir> --base <url> [options]
hina-builder keygen  [--out <dir>] [--name <prefix>]
hina-builder --help
```

---

## init Command (recommended starting point)

`init` is an interactive wizard: run it in (or point it at) your application folder and it
does the whole publisher setup for you — no need to hand-write `hina.app.json` or remember the
`build`/`keygen`/`sign-descriptor` sequence.

```shell
dotnet run --project Hina.Builder -- init --input ./build
```

What it does:

1. **Scans** the folder and detects executable candidates by magic bytes (PE → Windows,
   ELF → Linux, Mach-O / `.app` bundle → macOS), then asks you to confirm each one.
2. **Pre-fills every answer** with a smart `[default]` — just press Enter to accept. Defaults
   come from an existing `hina.app.json` (re-running `init` edits it) or, failing that, from
   your project files (`.csproj`, `package.json`, Unity `ProjectSettings.asset`, Godot
   `project.godot`).
3. Asks a few plain-language **sandbox** questions ("does it need internet?", "where does it
   save data?") and translates them into the descriptor's sandbox block.
4. Generates an **Ed25519 key pair** if you don't have one, then writes a **signed
   `hina.app.json`** into the app folder and runs **`build`** to produce the manifest + chunk
   store.

Output layout: the signed `hina.app.json` is written into the app folder (it's public and safe
to ship), while the **signing keys and the patch store are written to a separate folder OUTSIDE
the app folder** — the build manifest covers every file under `--input`, so keeping the private
key out of it is mandatory.

`init` is interactive and refuses to run with redirected stdin (CI). In a pipeline, use the
scriptable `build` + `hina dev sign-descriptor` commands instead.

---

## keygen Command

Generates an Ed25519 key pair for manifest signing.

### Flags

| Flag | Default | Description |
|------|---------|-------------|
| `--out` | `.` (current directory) | Output directory for key files |
| `--name` | `hina` | Base name for key files |

### Usage

```shell
dotnet run --project Hina.Builder -- keygen --out ./keys --name myapp
```

### Output Files

| File | Description |
|------|-------------|
| `<name>.key.b64` | Ed25519 private key, Base64-encoded (32 bytes raw). **Keep secret.** |
| `<name>.pub.b64` | Ed25519 public key, Base64-encoded (32 bytes raw). Distribute to clients. |

### Key Management Best Practices

- **Never commit private keys to version control.** Add `*.key.b64` to your `.gitignore`.
- **Store private keys in a secrets manager** (Azure Key Vault, AWS Secrets Manager, GitHub Actions secrets, etc.) for CI/CD builds.
- **Distribute public keys with your application.** Embed the public key content in `hina.config.json` as the `trustedPublicKey` value, or ship the `.pub.b64` file alongside the client.
- **Rotate keys** by generating a new pair and rebuilding. Clients must be updated with the new public key.
- **Use separate keys for staging and production** to prevent staging builds from being applied to production clients.

### Signing a Package-Manager Descriptor

The same Ed25519 key pair signs both the manifest (via `--sign-key` on `build`) and
the publisher's `hina.app.json` descriptor. After generating keys with `keygen`, sign
the descriptor with the CLI helper:

```shell
hina dev sign-descriptor --in hina.app.json --key ./keys/myapp.key.b64
```

This validates the descriptor, attaches an Ed25519 `descriptorSignature`, and rewrites
the file in place (or to `--out <path>`). See the
[Package Manager Guide](PackageManager-Guide.md) for the full publisher workflow.

---

## build Command

Scans a directory, chunks every file, and produces a manifest plus a chunk store.

### Flags

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--input` | Yes | -- | Path to the directory containing build artifacts (your game or application files) |
| `--out` | Yes | -- | Output directory for the manifest and chunk store |
| `--base` | Yes | -- | Base URL where the patch will be served (e.g., `https://patch.example.com/`) |
| `--version` | No | `0.0.0` | Version string for this build, stored in the manifest |
| `--chunk` | No | `65536` | Fixed chunk size in bytes (used in fixed chunking mode) |
| `--chunking` | No | `fixed` | Chunking mode: `fixed` or `cdc` |
| `--min-chunk` | No | `2048` | Minimum CDC chunk size in bytes |
| `--max-chunk` | No | `65536` | Maximum CDC chunk size in bytes |
| `--avg-chunk` | No | `8192` | Target average CDC chunk size in bytes |
| `--sign-key` | No | -- | Path to Ed25519 private key file (`.key.b64`) for signing the manifest |
| `-v`, `--verbose` | No | off | Enable debug-level logging output |

### Examples

**Basic build with defaults (fixed 64KB chunks, unsigned):**

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.0.0
```

**Signed build with custom chunk size:**

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.2.0 \
  --chunk 32768 \
  --sign-key ./keys/myapp.key.b64
```

**CDC build with custom parameters:**

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 2.0.0 \
  --chunking cdc \
  --min-chunk 4096 \
  --max-chunk 131072 \
  --avg-chunk 16384 \
  --sign-key ./keys/myapp.key.b64
```

**Verbose build for debugging:**

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.0.0 \
  --sign-key ./keys/myapp.key.b64 \
  -v
```

---

## Fixed vs CDC Chunking

### Fixed-Size Chunking (default)

Files are split into blocks of exactly `--chunk` bytes. The last block of each file may be smaller.

**Pros:**

- Simple and predictable chunk sizes.
- Lower computational overhead during build.
- Good rsync matching when files change in-place (e.g., a few bytes modified within a block).

**Cons:**

- Insertions or deletions shift all subsequent chunk boundaries, invalidating every chunk after the change point.
- Poor deduplication for files that grow, shrink, or have data inserted.

**Best for:** Files that change through in-place modification (config files, databases with fixed-size records, textures replaced wholesale).

### Content-Defined Chunking (CDC)

Files are split at boundaries determined by the file content using a Gear hash. Chunk sizes vary between `--min-chunk` and `--max-chunk`, averaging around `--avg-chunk`.

**Pros:**

- Insertions and deletions only invalidate chunks near the change point.
- Chunks elsewhere in the file retain their original boundaries and hashes.
- Significantly better deduplication for files with insertions or deletions.
- Better cross-file deduplication when different files share common content.

**Cons:**

- Higher computational overhead (Gear hash scanned byte-by-byte).
- Variable chunk sizes make transfer prediction less exact.
- Slightly more complex to reason about.

**Best for:** Large binary assets, archives, executable files, database files, any file where content is frequently inserted or removed.

### Example: Impact of a 1-byte Insertion

Consider a 1 MB file where 1 byte is inserted at offset 1000:

| Mode | Chunks invalidated | Chunks reused |
|------|-------------------|---------------|
| Fixed (64KB) | All 16 chunks (boundaries shifted) | 0 |
| CDC (avg 8KB) | ~1-2 chunks near offset 1000 | ~126 chunks |

---

## CDC Deep Dive

### How the Gear Hash Works

The Gear hash is a rolling hash designed for fast boundary detection. For each byte in the stream:

```
hash = (hash << 1) + GearTable[byte]
```

Where `GearTable` is a precomputed 256-entry lookup table of pseudorandom 64-bit values, generated deterministically using an xorshift64 PRNG seeded with `0x123456789ABCDEF0`.

### Boundary Detection

A chunk boundary is declared when:

1. At least `minSize` bytes have been consumed (enforces minimum chunk size).
2. `(hash & mask) == 0`, where `mask = (1 << log2(avgSize)) - 1`.
3. If `maxSize` bytes are consumed without finding a boundary, the chunk is cut at `maxSize`.

The mask controls the probability of a boundary at any given byte. With `avgSize = 8192`, the mask has 13 low bits set (`0x1FFF`), giving a 1/8192 probability of a boundary at each byte after the minimum.

### Chunk Size Distribution

With default parameters (`min=2048, max=65536, avg=8192`):

- Most chunks cluster around 8KB.
- No chunk is smaller than 2KB.
- No chunk is larger than 64KB.
- The distribution follows a geometric pattern, skewed toward the average.

---

## Build Output Structure

After a successful build, the output directory contains:

```
<out>/
  manifest.json
  chunks/
    00/
      00a1b2c3d4...chunk.br
      009f8e7d6c...chunk.br
    01/
      01f2e3d4c5...chunk.br
    ...
    ff/
      ffa1b2c3d4...chunk.br
```

### Hash Bucketing

Chunks are stored in subdirectories named by the first two hex characters of the SHA-256 hash (after removing the `sha256:` prefix). This creates up to 256 bucket directories, preventing any single directory from containing too many files.

Example: a chunk with hash `sha256:a3f7b2c1d4e5...` is stored at:

```
chunks/a3/a3f7b2c1d4e5...chunk.br
```

### Chunk Deduplication

If two files (or two versions of the same file) share identical chunks, the chunk is only stored once. The builder skips writing a chunk file if it already exists at the target path.

### Chunk Compression

Every chunk is Brotli-compressed at `CompressionLevel.Optimal` before being written to disk. The `.br` extension indicates Brotli compression. Clients decompress after download.

---

## Manifest Format Reference

The manifest is a JSON file describing the complete state of a release.

### Root Object

| Field | Type | Description |
|-------|------|-------------|
| `Version` | `string` | Version string set by `--version` |
| `BuildId` | `string` (ISO 8601) | UTC timestamp of when the manifest was built |
| `BaseUrl` | `string` | Base URL of the patch server |
| `Files` | `ManifestFile[]` | Array of file entries |
| `Signature` | `ManifestSignature?` | Optional Ed25519 signature (present when `--sign-key` is used) |

### ManifestFile Object

| Field | Type | Description |
|-------|------|-------------|
| `Path` | `string` | Relative file path using forward slashes (e.g., `data/levels/map01.bin`) |
| `Size` | `int64` | File size in bytes |
| `MTimeUtc` | `string` (ISO 8601) | Last modification time (UTC) |
| `FileHash` | `string` | Full-file SHA-256 hash in `sha256:<hex>` format |
| `ChunkSize` | `int` | Chunk size used when building this file |
| `Chunks` | `ManifestChunk[]` | Ordered array of chunk entries |

### ManifestChunk Object

| Field | Type | Description |
|-------|------|-------------|
| `Index` | `int` | Zero-based chunk index within the file |
| `Weak` | `uint` | Rolling checksum (Adler32-variant, 32-bit) |
| `Strong` | `string` | SHA-256 hash in `sha256:<hex>` format |
| `Size` | `int` | Chunk size in bytes |

### ManifestSignature Object

| Field | Type | Description |
|-------|------|-------------|
| `Algorithm` | `string` | Always `"ed25519"` |
| `Signature` | `string` | Base64-encoded Ed25519 signature of the canonical manifest bytes |
| `PublicKey` | `string` | Base64-encoded Ed25519 public key that produced the signature |

### Example Manifest

```json
{
  "Version": "1.0.0",
  "BuildId": "2026-03-30T12:00:00+00:00",
  "BaseUrl": "https://patch.example.com/",
  "Files": [
    {
      "Path": "game.exe",
      "Size": 524288,
      "MTimeUtc": "2026-03-30T11:30:00+00:00",
      "FileHash": "sha256:a3f7b2c1d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1",
      "ChunkSize": 65536,
      "Chunks": [
        {
          "Index": 0,
          "Weak": 2834710527,
          "Strong": "sha256:b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2",
          "Size": 65536
        },
        {
          "Index": 1,
          "Weak": 1923847561,
          "Strong": "sha256:c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3",
          "Size": 65536
        }
      ]
    }
  ],
  "Signature": {
    "Algorithm": "ed25519",
    "Signature": "BASE64_SIGNATURE_DATA",
    "PublicKey": "BASE64_PUBLIC_KEY"
  }
}
```

---

## CI/CD Integration

### GitHub Actions

```yaml
name: Build Patch

on:
  push:
    tags:
      - 'v*'

jobs:
  build-patch:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        run: dotnet restore Hina.sln

      - name: Build solution
        run: dotnet build Hina.sln --no-restore -c Release

      - name: Run tests
        run: dotnet test Hina.sln --no-build -c Release

      - name: Build application
        run: dotnet publish MyGame/MyGame.csproj -c Release -o ./build

      - name: Generate patch
        env:
          SIGN_KEY: ${{ secrets.HINA_SIGN_KEY }}
        run: |
          echo "$SIGN_KEY" > /tmp/sign.key.b64
          dotnet run --project Hina.Builder -c Release -- build \
            --input ./build \
            --out ./patch \
            --base https://patch.example.com/ \
            --version ${{ github.ref_name }} \
            --chunking cdc \
            --sign-key /tmp/sign.key.b64 \
            -v
          rm /tmp/sign.key.b64

      - name: Upload patch artifacts
        uses: actions/upload-artifact@v4
        with:
          name: patch-${{ github.ref_name }}
          path: ./patch/
```

### Azure Pipelines

```yaml
trigger:
  tags:
    include:
      - 'v*'

pool:
  vmImage: 'ubuntu-latest'

variables:
  buildConfiguration: 'Release'

steps:
  - task: UseDotNet@2
    inputs:
      packageType: 'sdk'
      version: '10.0.x'

  - script: dotnet restore Hina.sln
    displayName: 'Restore dependencies'

  - script: dotnet build Hina.sln --no-restore -c $(buildConfiguration)
    displayName: 'Build solution'

  - script: dotnet test Hina.sln --no-build -c $(buildConfiguration)
    displayName: 'Run tests'

  - script: dotnet publish MyGame/MyGame.csproj -c $(buildConfiguration) -o $(Build.ArtifactStagingDirectory)/build
    displayName: 'Build application'

  - script: |
      echo "$(HINA_SIGN_KEY)" > /tmp/sign.key.b64
      dotnet run --project Hina.Builder -c $(buildConfiguration) -- build \
        --input $(Build.ArtifactStagingDirectory)/build \
        --out $(Build.ArtifactStagingDirectory)/patch \
        --base https://patch.example.com/ \
        --version $(Build.BuildNumber) \
        --chunking cdc \
        --sign-key /tmp/sign.key.b64 \
        -v
      rm /tmp/sign.key.b64
    displayName: 'Generate patch'
    env:
      HINA_SIGN_KEY: $(HinaSignKey)

  - task: PublishBuildArtifacts@1
    inputs:
      PathtoPublish: '$(Build.ArtifactStagingDirectory)/patch'
      ArtifactName: 'patch'
```

### CI/CD Tips

- **Store the Ed25519 private key as a secret** in your CI system. Never hardcode it in pipeline files.
- **Write the key to a temporary file** during the build step and delete it immediately after.
- **Use `--version` with the Git tag or build number** to keep versions traceable.
- **Run tests before building the patch** to avoid shipping broken builds.
- **Upload the patch directory as a build artifact** and deploy it to your CDN or static host in a separate step.
- **Use CDC chunking in production** for better bandwidth efficiency across releases.
