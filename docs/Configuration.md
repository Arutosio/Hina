# Configuration Reference

This document covers all configuration options for Hina, including the client patcher config (`hina.config.json`), host config (`hina.host.json`), config file resolution, and environment-specific examples.

> The package-manager descriptor `hina.app.json` (the file a publisher hosts at the
> URL `hina install` consumes) is a different file with its own schema. See the
> [Package Manager Guide](PackageManager-Guide.md) for the descriptor reference.

---

## Config File Resolution Order

The CLI resolves configuration in the following priority order:

1. **`--config` flag** -- If provided, loads the specified JSON file.
2. **`hina.config.json`** -- If present in the current working directory, loads it automatically.
3. **Defaults** -- Falls back to built-in default values.

Command-line flags (`--base`, `--pubkey`, `--channel`) override values from the config file.

---

## Client Configuration (hina.config.json)

### Full Property Reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `baseUrl` | `string` (URI) | `http://localhost/` | Base URL of the patch server. Must include a trailing slash for correct URI resolution. |
| `channel` | `string` | `"stable"` | Release channel name. Determines which manifest file to fetch (`manifest.json` for stable, `manifest.<channel>.json` for others). |
| `concurrency` | `int` | `4` | Number of concurrent chunk downloads. Higher values saturate bandwidth on high-latency connections. |
| `chunkSize` | `int` | `65536` | Fixed chunk size in bytes. Used in fixed chunking mode. Must match the value used by the builder. |
| `verify` | `bool` | `true` | Whether to verify file hashes after patching. Disabling this is not recommended. |
| `backup` | `bool` | `true` | Whether to keep backups of original files for rollback. When disabled, rollback is not available. |
| `trustedPublicKey` | `string?` | `null` | Base64-encoded Ed25519 public key for manifest signature verification. When set, the patcher rejects manifests with invalid or missing signatures. |
| `maxRetries` | `int` | `8` | Maximum number of retry attempts on transient errors (HTTP 5xx, network failures, timeouts). |
| `retryBaseDelayMs` | `int` | `1000` | Base delay in milliseconds for exponential backoff. Actual delay doubles on each attempt with added jitter. |
| `maxRetryDelayMs` | `int` | `30000` | Hard cap on a single retry sleep. Without this, retry 11 would wait ~17 minutes; the cap keeps the backoff sane on long flaky stretches. |
| `connectTimeoutMs` | `int` | `10000` | TCP handshake timeout. Short on purpose so a stalled SYN fails fast and retry kicks in instead of sitting on the default 100 s wall. |
| `requestTimeoutMs` | `int` | `60000` | Overall per-request timeout (`HttpClient.Timeout`). Caps how long a single chunk / manifest fetch can hang before being treated as transient. |
| `pooledConnectionLifetimeMs` | `int` | `60000` | After this, the underlying TCP socket is torn down and a fresh DNS + connect runs on the next request. Matters on flaky / mobile / changing-IP links. |
| `chunkingMode` | `string` | `"fixed"` | Chunking strategy. `"fixed"` for fixed-size chunking, `"cdc"` for content-defined chunking. |
| `minChunkSize` | `int` | `2048` | Minimum chunk size in bytes. CDC mode only. |
| `maxChunkSize` | `int` | `65536` | Maximum chunk size in bytes. CDC mode only. |
| `avgChunkSize` | `int` | `8192` | Target average chunk size in bytes. CDC mode only. Controls the Gear hash mask. |

### Retry Behavior

The retry policy uses exponential backoff with jitter. The delay for attempt N (1-indexed) is:

```
delay = retryBaseDelayMs * 2^(N-1) + jitter
```

Where jitter is a random value between 0 and 25% of the exponential delay.

| Attempt | Base Delay (ms) | Exponential Delay (ms) | Jitter Range (ms) |
|---------|-----------------|------------------------|--------------------|
| 1       | 1000            | 1000                   | 0 - 250            |
| 2       | 1000            | 2000                   | 0 - 500            |
| 3       | 1000            | 4000                   | 0 - 1000           |

Transient errors that trigger retry:

- HTTP 5xx responses (500, 502, 503, 504, etc.)
- Network-level failures (DNS resolution, connection reset, no status code)
- Timeouts (TaskCanceledException with TimeoutException inner exception)

Non-transient errors that do NOT trigger retry:

- HTTP 4xx responses (400, 401, 403, 404, etc.)
- User-initiated cancellation

### Chunking Mode Details

The `chunkingMode` must match between the builder and the client. If you build with `--chunking cdc`, the client config must also use `"chunkingMode": "cdc"` with matching size parameters.

**Fixed chunking** (`"fixed"`): Files are split into blocks of exactly `chunkSize` bytes (the last block may be smaller). Simple and predictable.

**CDC** (`"cdc"`): Files are split at content-defined boundaries using a Gear hash. Chunk sizes vary between `minChunkSize` and `maxChunkSize`, averaging around `avgChunkSize`. Provides better deduplication when files change through insertions or deletions.

---

## Complete Configuration Examples

### Minimal Configuration

```json
{
  "baseUrl": "https://patch.example.com/"
}
```

All other properties use their defaults: stable channel, 4 concurrent downloads, fixed 64KB chunks, verification enabled, backup enabled, no signature checking.

### Fixed Chunking with Signing

```json
{
  "baseUrl": "https://patch.example.com/",
  "channel": "stable",
  "concurrency": 4,
  "chunkSize": 65536,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_ED25519_PUBLIC_KEY"
}
```

### CDC Chunking with Signing

```json
{
  "baseUrl": "https://patch.example.com/",
  "channel": "stable",
  "concurrency": 4,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_ED25519_PUBLIC_KEY",
  "chunkingMode": "cdc",
  "minChunkSize": 2048,
  "maxChunkSize": 65536,
  "avgChunkSize": 8192
}
```

### Full Configuration (All Properties)

```json
{
  "baseUrl": "https://patch.example.com/",
  "channel": "stable",
  "concurrency": 4,
  "chunkSize": 65536,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_ED25519_PUBLIC_KEY",
  "maxRetries": 3,
  "retryBaseDelayMs": 1000,
  "chunkingMode": "fixed",
  "minChunkSize": 2048,
  "maxChunkSize": 65536,
  "avgChunkSize": 8192
}
```

### High-Bandwidth / Low-Latency Configuration

```json
{
  "baseUrl": "https://patch.example.com/",
  "concurrency": 8,
  "chunkSize": 131072,
  "maxRetries": 2,
  "retryBaseDelayMs": 500
}
```

### Unreliable Network Configuration

```json
{
  "baseUrl": "https://patch.example.com/",
  "concurrency": 2,
  "chunkSize": 32768,
  "maxRetries": 5,
  "retryBaseDelayMs": 2000
}
```

---

## Environment-Specific Configurations

### Development

```json
{
  "baseUrl": "http://localhost:5000/",
  "channel": "dev",
  "concurrency": 2,
  "verify": true,
  "backup": false,
  "chunkSize": 65536
}
```

Notes:
- Points to local Hina.Host instance.
- Backup disabled for faster iteration.
- No signature verification (no `trustedPublicKey`).

### Staging

```json
{
  "baseUrl": "https://staging-patch.example.com/",
  "channel": "beta",
  "concurrency": 4,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_STAGING_PUBLIC_KEY",
  "maxRetries": 3,
  "retryBaseDelayMs": 1000
}
```

Notes:
- Separate staging server and signing key.
- Beta channel for pre-release testing.
- Full verification and backup enabled.

### Production

```json
{
  "baseUrl": "https://patch.example.com/",
  "channel": "stable",
  "concurrency": 4,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_PRODUCTION_PUBLIC_KEY",
  "maxRetries": 3,
  "retryBaseDelayMs": 1000,
  "chunkingMode": "cdc",
  "minChunkSize": 2048,
  "maxChunkSize": 65536,
  "avgChunkSize": 8192
}
```

Notes:
- Signature verification is mandatory in production.
- CDC chunking for optimal bandwidth usage.
- Backup enabled for rollback safety.

---

## Host Configuration (hina.host.json)

Hina.Host is an ASP.NET Core static file server. Its configuration is minimal.

### Host Config File

```json
{
  "root": "patch"
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `root` | `string` | `"patch"` | Directory to serve as the static file root. Contains `manifest.json` and `chunks/`. |

### Host Config Resolution Order

1. **`--config` flag** -- If provided, reads the `root` property from the specified JSON file.
2. **`hina.host.json`** -- If present in the current working directory.
3. **`Patcher:Root` app setting** -- From ASP.NET Core configuration (appsettings.json, environment variables, etc.).
4. **Default** -- Falls back to `"patch"`.

### Host Usage

```shell
# Use default root ("patch" directory)
dotnet run --project Hina.Host

# Use explicit config file
dotnet run --project Hina.Host -- --config ./hina.host.json

# The host exposes a health check endpoint
# GET /health -> 200 "ok"
```

The host serves all files under the root directory as static files. The expected directory structure is:

```
<root>/
  manifest.json
  manifest.beta.json        (optional, for non-stable channels)
  chunks/
    a3/
      a3f7...chunk.br
    b1/
      b12e...chunk.br
    ...
```

---

## Programmatic Configuration

You can configure the patcher directly in code without a JSON file:

```csharp
using Hina.Core.Configuration;

var config = new PatcherConfig
{
    BaseUrl = new Uri("https://patch.example.com/"),
    Channel = "stable",
    Concurrency = 4,
    ChunkSize = 65536,
    Verify = true,
    Backup = true,
    TrustedPublicKey = "BASE64_ED25519_PUBLIC_KEY",
    MaxRetries = 3,
    RetryBaseDelayMs = 1000,
    ChunkingMode = "fixed",
    MinChunkSize = 2048,
    MaxChunkSize = 65536,
    AvgChunkSize = 8192
};
```

Or load from a JSON file:

```csharp
using Hina.Core.Configuration;

PatcherConfig config = PatcherConfigLoader.Load("./hina.config.json");
```

The `PatcherConfigLoader.Load` method uses `System.Text.Json` with case-insensitive property matching, so both `baseUrl` and `BaseUrl` are accepted in the JSON file.
