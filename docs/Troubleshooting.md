# Troubleshooting

This guide covers common errors, their causes, and solutions. Each scenario includes the error message or symptom you will see and the steps to resolve it.

---

## Debugging Tools

### Verbose Mode

Enable verbose logging with the `--verbose` or `-v` flag:

```shell
hina patch --dir ./game --base https://patch.example.com/ --verbose
```

This sets the log level to `Debug`, which outputs:

- Config resolution details (which config file was loaded, which root was selected)
- Per-file rsync match counts (e.g., "Rsync matched 47/50 chunks for data/level1.bin")
- Skipped files (already up to date)
- Per-chunk download decisions
- Verification pass/fail per file
- Retry attempts with delay information

### Reading Log Output

Hina uses structured logging via `Microsoft.Extensions.Logging`. Log entries include named parameters in curly braces:

```
info: Hina.CLI[0] Patching file {FilePath}
      FilePath=data/level1.bin
warn: Hina.CLI[0] Transient error on attempt {Attempt}/{MaxRetries}, retrying in {DelayMs}ms
      Attempt=1, MaxRetries=3, DelayMs=1250
```

Key log patterns to watch for:

| Pattern | Meaning |
|---------|---------|
| `Patching file {FilePath}` | A file is being updated |
| `File already up to date, skipping` | File hash matches, no download needed |
| `Rsync matched X/Y chunks` | X out of Y chunks were found locally (higher is better) |
| `Downloading chunk {ChunkIndex}` | A chunk is being fetched from the server |
| `Transient error on attempt` | A retry is occurring due to a network issue |
| `Incomplete journal found, rolling back` | A previous patch did not finish cleanly |

---

## Common Errors

### 1. Manifest Signature Is Invalid

**Error:**

```
System.IO.InvalidDataException: Manifest signature is invalid.
```

**Causes:**

- The `trustedPublicKey` in the client config does not match the private key used to sign the manifest.
- The manifest was modified after signing (e.g., manually edited, corrupted during transfer).
- The manifest was built without signing but the client expects a signature.

**Solutions:**

- Verify the public key in your client config matches the key pair used during the build.
- Re-run the build with `--sign-key` pointing to the correct private key.
- If you do not need signing, remove `trustedPublicKey` from the client config (not recommended for production).

---

### 2. Manifest Download Failure (HTTP 404)

**Error:**

```
System.Net.Http.HttpRequestException: Response status code does not indicate success: 404 (Not Found).
```

**Causes:**

- The `baseUrl` in the config is incorrect or does not point to the directory containing `manifest.json`.
- The manifest file was not uploaded to the server.
- A non-stable channel was specified but the corresponding `manifest.<channel>.json` does not exist.

**Solutions:**

- Verify the base URL is correct and includes a trailing slash (e.g., `https://patch.example.com/`).
- Confirm `manifest.json` (or `manifest.<channel>.json`) exists on the server at the expected URL.
- Check the channel name matches exactly (case-sensitive).

---

### 3. Manifest Parse Error

**Error:**

```
System.Text.Json.JsonException: '<' is an invalid start of a value.
```

**Causes:**

- The server returned an HTML error page instead of JSON (common with misconfigured reverse proxies).
- The URL points to a directory listing or login page rather than the manifest file.

**Solutions:**

- Open the manifest URL directly in a browser to see what the server returns.
- Fix the server configuration to serve `manifest.json` as a static file.
- Ensure no authentication middleware is intercepting the request.

---

### 4. Chunk Download Failure (HTTP 404)

**Error:**

```
System.Net.Http.HttpRequestException: Response status code does not indicate success: 404 (Not Found).
Request failed after 4 attempts: ...
```

**Causes:**

- The chunks directory was not uploaded to the server, or only partially uploaded.
- The `baseUrl` does not match the URL structure expected by the chunk path (`chunks/<prefix>/<hash>.chunk.br`).
- The build output was regenerated but the old chunks were not cleaned up or the new ones were not deployed.

**Solutions:**

- Verify the complete `chunks/` directory was uploaded to the server under the base URL.
- Check that the two-character hash prefix directories exist (e.g., `chunks/a3/`, `chunks/b1/`).
- Re-run the build and redeploy all output files.

---

### 5. Hash Mismatch After Patch

**Error:**

```
System.IO.InvalidDataException: Hash mismatch after patch.
```

**Causes:**

- A downloaded chunk was corrupted in transit (rare, but possible with unreliable networks).
- The chunk file on the server does not match the manifest (stale or mismatched build output).
- Disk corruption on the server or client.

**Solutions:**

- Re-run the patch. Transient corruption is resolved by re-downloading.
- Re-run the build to regenerate a consistent manifest and chunk set.
- If the error persists, check the server's disk health.

---

### 6. Connection Timeout

**Error:**

```
System.Net.Http.HttpRequestException: Request failed after 4 attempts: The request was canceled due to the configured HttpClient.Timeout...
```

**Causes:**

- The patch server is unreachable (down, blocked by firewall, DNS failure).
- Network latency exceeds the HTTP client timeout.
- A proxy or VPN is interfering with the connection.

**Solutions:**

- Verify the server is running and accessible from the client network.
- Check DNS resolution: `nslookup patch.example.com`.
- Check connectivity: `curl -I https://patch.example.com/health`.
- If behind a corporate proxy, configure the system proxy settings.
- Increase `maxRetries` and `retryBaseDelayMs` for unreliable networks.

---

### 7. DNS Resolution Failure

**Error:**

```
System.Net.Http.HttpRequestException: No such host is known.
```

**Causes:**

- The hostname in `baseUrl` does not resolve to an IP address.
- DNS server is unreachable.
- Typo in the hostname.

**Solutions:**

- Verify the hostname is correct.
- Test DNS resolution: `nslookup patch.example.com`.
- Try using an IP address temporarily to isolate the issue.
- Check the system's DNS configuration.

---

### 8. SSL/TLS Certificate Error

**Error:**

```
System.Net.Http.HttpRequestException: The SSL connection could not be established...
```

**Causes:**

- The server's TLS certificate is expired, self-signed, or issued for a different hostname.
- The client machine does not trust the certificate authority.
- Intermediate certificates are missing on the server.

**Solutions:**

- Check the certificate: `openssl s_client -connect patch.example.com:443`.
- Ensure the certificate is valid and matches the hostname.
- Install missing CA certificates on the client.
- For development with self-signed certificates, add the certificate to the system trust store.

---

### 9. Permission Denied / Access Error

**Error:**

```
System.UnauthorizedAccessException: Access to the path '...' is denied.
```

**Causes:**

- The patcher does not have write permission to the target directory.
- A file is locked by another process (e.g., the game is still running).
- Antivirus software is blocking file modifications.

**Solutions:**

- Ensure the patcher runs with appropriate permissions (administrator on Windows if the game is in Program Files).
- Close the game and any process that may have files open in the target directory.
- Add the game directory to your antivirus exclusion list.
- On Linux/macOS, check directory ownership: `ls -la` and `chmod`/`chown` as needed.

---

### 10. Disk Full

**Error:**

```
System.IO.IOException: There is not enough space on the disk.
```

**Causes:**

- Insufficient disk space for the temp files (`.hina.tmp`), backups (`.hina.bak`), and final files.
- During a patch, Hina needs space for: the new file (temp) + the backup of the old file + the final file.

**Solutions:**

- Free disk space. A patch temporarily requires roughly 2x the size of the files being updated.
- Run `hina cleanup --dir ./game` to remove leftover temp and backup files from a previous failed patch.
- Disable backups (`"backup": false`) to reduce space requirements (at the cost of losing rollback capability).

---

### 11. Mismatched Chunk Size Configuration

**Symptom:**

The patch runs but downloads every chunk for every file, even when files have only minor changes. Rsync matching reports 0 matches.

**Cause:**

The `chunkSize` or `chunkingMode` in the client config does not match the values used during the build. The client computes rolling checksums with a different block size than the manifest expects.

**Solution:**

Ensure the client config matches the build parameters:

| Build Flag | Client Config Property |
|------------|----------------------|
| `--chunk 65536` | `"chunkSize": 65536` |
| `--chunking cdc` | `"chunkingMode": "cdc"` |
| `--min-chunk 2048` | `"minChunkSize": 2048` |
| `--max-chunk 65536` | `"maxChunkSize": 65536` |
| `--avg-chunk 8192` | `"avgChunkSize": 8192` |

---

### 12. Incomplete Previous Patch (Journal Recovery)

**Symptom:**

On starting a patch, the log shows:

```
warn: Incomplete journal found, rolling back previous patch
```

**Cause:**

A previous patch was interrupted (crash, power loss, cancellation) and left a journal file at `.hina/journal.json`.

**Explanation:**

This is normal and expected behavior. Hina detects the incomplete journal and automatically rolls back the partial changes before starting the new patch. No user action is required.

If you want to manually clean up without patching:

```shell
hina cleanup --dir ./game
```

---

### 13. Missing --dir or --base Arguments

**Error:**

```
error: Missing required --dir <path>
error: Missing required --base <url> or config baseUrl
```

**Cause:**

Required command-line arguments were not provided and no config file supplies the missing values.

**Solution:**

Provide both required arguments:

```shell
hina patch --dir ./game --base https://patch.example.com/
```

Or create a `hina.config.json` with `baseUrl` and use `--dir` on the command line.

---

## Reporting Bugs

If you encounter an issue not covered here:

1. Reproduce the issue with `--verbose` to capture detailed logs.
2. Note the exact error message and stack trace.
3. Include your Hina version, OS, and .NET runtime version.
4. Open an issue at [https://github.com/Arutosio/Hina/issues](https://github.com/Arutosio/Hina/issues) with the above information.

Include the following in your report:

```
**Environment:**
- Hina version: ...
- OS: Windows 11 / Ubuntu 24.04 / macOS 15
- .NET runtime: 10.0.x

**Steps to reproduce:**
1. ...
2. ...

**Expected behavior:**
...

**Actual behavior:**
...

**Verbose log output:**
```
(paste log output here)
```
```
