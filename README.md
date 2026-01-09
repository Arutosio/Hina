# Hina

![Hina Logo](img/Hina_Logo.png)

Hina is an open-source, rsync-like patcher designed for game clients and desktop apps.
It focuses on fast updates, integrity verification, and easy integration with both
standalone tools and embedded client software.

This README explains how Hina works, why to use it, and how to build, configure,
and integrate it.

## Why Hina instead of other patchers
- Efficient bandwidth: rsync-like rolling checksum finds matching local blocks.
- Reliable updates: per-chunk verification plus full-file hash validation.
- Security-ready: Ed25519 manifest signing with optional trusted public key.
- Simple hosting: static files via the included host or any CDN/Nginx.
- Open source end-to-end: no proprietary dependencies or services.
- Integration-friendly: a core library plus CLI and builder.

## How it works
Hina produces a manifest and a chunk store from a build. Clients download only
missing chunks, rebuild files locally, and verify integrity.

Core steps:
1) Builder scans a build folder and generates:
   - manifest.json (file list, hashes, chunk map)
   - chunks/ (Brotli-compressed blocks stored by hash)
2) Host serves manifest and chunks over HTTP.
3) Client loads manifest, compares local files, and downloads missing chunks.
4) Client rebuilds each file in order and verifies the result.

## Flow diagrams

Build pipeline:
```
Build artifacts
    |
    v
Hina.Builder
    |  -> manifest.json
    |  -> chunks/ (hash bucketed)
    v
Static host or CDN (Hina.Host or Nginx)
```

Client patch pipeline:
```
Client startup
    |
    v
Fetch manifest
    |
    v
Rolling checksum scan of local files
    |
    v
Download missing chunks
    |
    v
Rebuild file -> Verify hash -> Swap in
    |
    v
Success or rollback
```

## Project layout
- `Hina.Core`    - patch logic, rsync matching, manifest, hashing, signing
- `Hina.CLI`     - command line patcher for clients
- `Hina.Builder` - creates manifests and chunk stores
- `Hina.Host`    - static server for manifests and chunks

## Build and test
Requirements: .NET SDK 10.x (preview at time of writing).

Build everything:
```shell
dotnet build Hina.sln
```

Publish the CLI for Windows and Linux:
```shell
pwsh ./scripts/publish-cli.ps1
```

Run all tests:
```shell
dotnet test Hina.sln
```

## Builder usage (create patches)
1) Generate signing keys (optional but recommended):
```shell
dotnet run --project Hina.Builder -- keygen --out ./keys --name hina
```
This creates:
- `./keys/hina.key.b64` (private key for signing)
- `./keys/hina.pub.b64` (public key for verification)

2) Build manifest and chunk store:
```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/game1/ \
  --version 1.0.0 \
  --chunk 65536 \
  --sign-key ./keys/hina.key.b64
```

Outputs:
- `./patch/manifest.json`
- `./patch/chunks/`

## Host usage (serve patches)
Host the patch directory using the included server:
```shell
dotnet run --project Hina.Host
```

Default root is `./patch`. You can configure it via JSON:
`hina.host.json`
```json
{ "root": "patch" }
```

Or pass a config file:
```shell
dotnet run --project Hina.Host -- --config ./hina.host.json
```

## CLI usage (client patching)
Patch a local client folder:
```shell
dotnet run --project Hina.CLI -- patch --dir ./client --base https://patch.example.com/game1/
```

Check only:
```shell
dotnet run --project Hina.CLI -- check --dir ./client --base https://patch.example.com/game1/
```

Verify integrity:
```shell
dotnet run --project Hina.CLI -- verify --dir ./client --base https://patch.example.com/game1/
```

Rollback (restores backups if a patch failed):
```shell
dotnet run --project Hina.CLI -- rollback --dir ./client --base https://patch.example.com/game1/
```

Cleanup leftover temp/backup files:
```shell
dotnet run --project Hina.CLI -- cleanup --dir ./client --base https://patch.example.com/game1/
```

## Configuration
The CLI reads `hina.config.json` from the working directory or `--config <file>`.

Example `hina.config.json`:
```json
{
  "baseUrl": "https://patch.example.com/game1/",
  "channel": "stable",
  "concurrency": 4,
  "chunkSize": 65536,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_PUBKEY"
}
```

Notes:
- `trustedPublicKey` enables manifest signature verification.
- `chunkSize` must match the size used by the builder.

## Integrating into your application
You can use `Hina.Core` directly.

Example (C#):
```csharp
using Hina.Core.Configuration;
using Hina.Core.Patching;

var config = new PatcherConfig
{
    BaseUrl = new Uri("https://patch.example.com/game1/"),
    Channel = "stable",
    Verify = true,
    Backup = true,
    TrustedPublicKey = "BASE64_PUBKEY"
};

var client = new PatchClient(config);
var result = await client.PatchAsync("./client", CancellationToken.None);
```

## Security model
- Manifests can be signed with Ed25519.
- Clients verify signature when a trusted public key is configured.
- Each chunk is verified by strong hash and each file by full hash.

## Performance notes
- Rolling checksum reduces bandwidth for large similar files.
- Brotli-compressed chunks reduce server transfer size.
- Hash buckets keep file system lookups fast in large stores.

## Troubleshooting
- Patch fails with "Manifest signature is invalid":
  - Ensure `trustedPublicKey` matches the key used by the builder.
- Client redownloads everything:
  - Check `chunkSize` matches the builder setting.
- 404 on chunks:
  - Confirm `chunks/` is served from the host root.

## Status
Hina is actively developed. The core patch flow is implemented, with room for
future improvements such as parallel chunk downloads and advanced caching.
