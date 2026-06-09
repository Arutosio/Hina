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
- The server refuses unknown file extensions. Generic static-file servers (and **Hina.Host
  versions up to v1.4.0**, fixed since) return 404 for `.chunk.br` because `.br` has no
  registered content type.

**Solutions:**

- Verify the complete `chunks/` directory was uploaded to the server under the base URL.
- Check that the two-character hash prefix directories exist (e.g., `chunks/a3/`, `chunks/b1/`).
- Re-run the build and redeploy all output files.
- If the manifest downloads fine but **every** chunk 404s while the files exist on disk,
  configure the server to serve unknown extensions as `application/octet-stream`
  (nginx: `default_type application/octet-stream;` for the chunks location; IIS: add a
  `.br` MIME mapping). If you serve with Hina.Host, update it to a version newer than
  v1.4.0 — older builds had exactly this bug.

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

### 14. Patch Fails with "File Is Being Used by Another Process"

**Error:**

```
System.IO.IOException: The process cannot access the file '<app>/<file>' because it is being used by another process.
```

**Causes:**

- The app being patched is still running (its executable/data files are locked) — close it and retry.
- An antivirus or indexer is holding a handle on the file — usually transient, retry.
- **Hina versions up to v1.4.0** had a bug where patching an existing file that shared
  chunks with the new version (rsync reuse) kept a read handle open across the final
  file swap, failing with this error even when nothing else touched the file. Fixed
  since; update the CLI if you hit this reliably on every delta update.

**Solutions:**

- Make sure the target app is not running while `hina update` runs.
- Retry — the journal-based rollback leaves the install consistent, so a retry is safe.
- Update to a Hina build newer than v1.4.0 if the failure reproduces on every update
  that reuses local chunks.

---

## Package Manager Errors

These cover the end-user `hina install / update / uninstall / reinstall` surface.
For the patcher-level errors (which still apply to `hina dev <cmd>`), see the
scenarios above.

### PM1. Descriptor Signature Does Not Match Its Declared publicKey

**Error:**

```
Install failed: Descriptor signature does not match its declared publicKey.
```

**Causes:**

- The `hina.app.json` was signed with a different private key than the one declared
  in its own `publicKey` field.
- The descriptor was edited (BOM added, whitespace mangled, content re-flowed) by
  a CDN or proxy after signing.

**Solutions:**

- Re-sign with the matching private key: `hina dev sign-descriptor --in hina.app.json --key <priv.b64>`.
- Serve the descriptor with `Content-Type: application/json` and disable any
  CDN-edge HTML rewriting / pretty-printing.

---

### PM2. Descriptor Signature Does Not Match the Pinned Publisher Key

**Error:**

```
demo: Descriptor signature does not match the pinned publisher key. Use `hina reinstall --rotate-key` to accept a new key.
```

**Cause:**

The publisher rotated their Ed25519 signing key. Hina pinned the original key at
install time and refuses to silently accept a new one through `hina update`.

**Solutions:**

- If you trust the rotation, run `hina reinstall <name> --rotate-key` to accept
  the new key explicitly. The new key is pinned in place of the old one.
- If you did not expect a key rotation, **do not** rotate — verify with the
  publisher through an out-of-band channel first.

---

### PM3. App Is Already Installed

**Error:**

```
Install failed: 'demo' is already installed (version 1.0.0). Use `hina update` or `hina reinstall`.
```

**Cause:**

`hina install` is the fresh-install path. It refuses to clobber an existing app.

**Solutions:**

- Update with `hina update <name>` to apply newer versions.
- Use `hina reinstall <name>` to re-run the full install pipeline against the
  cached descriptor URL.

---

### PM3b. App Folder Manually Deleted

**Symptom:**

`hina list` shows the app with a trailing `[missing]` marker; `hina update`
fails the patch (no files to delta against); `hina info` prints a hint to run
`hina verify`.

**Cause:**

The user (or another tool) `rm -rf`d the app directory but the registry entry
remains, plus shortcuts/symlinks created at install time still point at the
vanished target.

**Solutions:**

```shell
hina verify          # see exactly what's dangling
hina verify --repair # clean orphan registry entry + dangling side-effects
```

`hina verify --repair` is idempotent and safe to run from cron.

---

### PM4. Install Directory Already Exists and Is Not Empty

**Error:**

```
Install failed: Install directory '/Users/me/Library/Application Support/Hina/Apps/demo' already exists and is not empty.
```

**Cause:**

A previous install or manual file placement left non-Hina files at the target
location. Hina refuses to overwrite content it didn't create.

**Solutions:**

- Inspect the directory; if it's leftover from a crashed previous install, delete
  it manually then re-run `hina install <url>`.
- Confirm no other app or tool is using the same path.

---

### PM5. TOFU Rejection (User Declined the Publisher Key)

**Symptom:**

```
Install cancelled.
```

The prompt was:

```
  App:        FooEdit
  Publisher:  Foo Software Ltd.
  Source:     https://foo.example/hina.app.json
  Key fpr:    8e1f:a2c4:3b6d:5d09

Trust this publisher and install? [y/N]
```

**Cause:**

The user typed `n` (or anything other than `y` / `yes`) at the trust prompt.

**Solutions:**

- Re-run with confidence in the publisher's identity. Compare the fingerprint
  against the value published on the vendor's official website.
- For scripted / CI installs, redirect stdin or pre-accept programmatically
  via the `InstallOptions.OnFirstTimeTrust` callback when embedding
  `Hina.PackageManager` directly.

---

### PM6. Descriptor Validation Failed

**Error:**

```
Descriptor validation failed:
  - name 'Foo' must match ^[a-z][a-z0-9-]{1,63}$.
  - baseUrl 'http://...' must be HTTPS (got 'http'). Pass --allow-insecure to permit HTTP.
  - exec must define at least one platform.
```

**Cause:**

The descriptor breaks the schema invariants documented in
[Package Manager Guide](PackageManager-Guide.md).

**Solutions:**

- Fix the listed errors and re-sign with `hina dev sign-descriptor`.
- For local-testing-only HTTP, pass `--allow-insecure` on `hina install` (do not
  use in production).

---

### PM6b. Flaky / Mobile / Changing-IP Network

**Symptoms:**

- `hina install` or `hina update` fails with `HttpRequestException: Request failed
  after N attempts` mid-download, especially after the connection drops, the laptop
  changes Wi-Fi, the modem reconnects, the carrier hands off to a new IP, etc.
- Long stalls (~100s) before any error, on a slow or congested link.

**What Hina already does:**

- Retries each chunk and the descriptor up to 8 times with exponential backoff
  capped at 30 s, so a 30-second outage no longer kills the install.
- Tears down stale TCP sockets every 60 s (`PooledConnectionLifetimeMs`) and
  re-resolves DNS, so a fresh IP / route is picked up automatically.
- Caps the TCP handshake at 10 s (`ConnectTimeoutMs`) so a black-holed route fails
  fast and retry kicks in, instead of sitting on the default 100 s wall.
- On Ctrl-C, cooperative cancellation rolls back any in-flight patch and leaves a
  journal; the next `hina update` or `hina install` finds the journal, rolls it
  back, and the rsync matcher reuses every chunk that already made it to disk —
  effectively a resume.

**Knobs if defaults aren't aggressive enough:**

```
hina install <url> --retries 20 --connect-timeout 5 --request-timeout 30
hina update --retries 20 --connect-timeout 5 --request-timeout 30
```

- `--retries N` raises the per-request retry budget (default 8).
- `--connect-timeout SEC` shortens the TCP handshake timeout (default 10).
- `--request-timeout SEC` shortens the overall request timeout (default 60).

Smaller timeouts mean faster failure → faster retry on a bad route. Raise
`--retries` until the operation completes during your typical reconnection
window.

---

### PM7. Update Reports "AlreadyUpToDate" but I Just Re-Built

**Cause:**

`UpdateService` skips work when `descriptor.version == registry.installedVersion`.
If you re-built the app without bumping the version, no update is performed.

**Solutions:**

- Bump the descriptor `version` (SemVer) before each release.
- For local iteration, run `hina update <name> --force` to re-run the patcher
  regardless of version.

---

### PM8. App Declares a Sandbox but Isn't Isolated on macOS/Windows

**Symptom:**

The app's `hina.app.json` declares a `sandbox` block, but on macOS or Windows the
app can still read and write anywhere. At install time you saw:

```
warn: This app DECLARES a filesystem sandbox, but Hina does not enforce sandboxing
      on this OS yet. The app will run with FULL user privileges (no isolation).
```

**Cause:**

Filesystem sandboxing is enforced **only on Linux** (via Landlock). On macOS and
Windows the declared scope is shown but **not applied** — the app runs with full
user privileges. This is expected, not a bug.

**Solutions:**

- Treat the declared scope as advisory on macOS/Windows.
- Run `hina perms <app>` to see what the app declares; the table footer and the
  detail view both state which surfaces are actually enforced.

---

### PM9. Update Fails Asking for `--accept-new-permissions`

**Error:**

```
'demo' 2.0.0 requests broader permissions (home (rw), capability: network).
Re-run with `--accept-new-permissions` to allow it.
```

**Cause:**

The new version of the descriptor *broadens* the app's sandbox: a new path, the
`host` token, a `ro → rw` upgrade, a new capability, or removing the sandbox
entirely. Hina refuses to silently widen an installed app's reach, so it stops
before touching any files.

**Solutions:**

- Review the listed `+` permission changes. If you trust them, re-run:

  ```shell
  hina update demo --accept-new-permissions
  ```

- If the new scope looks wrong, verify with the publisher before accepting.
  *Narrowing* changes never require this flag — they apply automatically.

---

### PM10. `hina verify` Reports a Missing File / App Corrupt

**Symptom:**

```
demo (1.2.0)
  install path: ...
  - descriptor cache missing
  - missing file: bin/demo

Some apps are missing files — run `hina reinstall <app>` to restore them.
```

`hina run` may also refuse to launch with a "descriptor is missing/corrupt;
cannot launch safely" message. Or `hina verify --deep` reports
`N file(s) corrupt or missing (hash check)`.

**Cause:**

A declared executable, an `entries[].exec`, or the cached descriptor is missing,
or (with `--deep`) a file's content no longer matches the manifest hash — i.e. the
install is incomplete or has been tampered with.

**Solutions:**

```shell
hina verify <app>          # offline: missing exec / entries / descriptor cache
hina verify <app> --deep   # network: hash every file against the manifest
hina reinstall <app>       # re-run the full install to restore the files
```

`hina reinstall` is the recovery path Hina points you at for any "missing file" /
"descriptor cache missing" condition.

---

### PM11. I Deleted an App Folder or `registry.json` by Hand — Now There Are Leftovers

**Symptom:**

`hina list` shows an app with `[missing]`, or shortcuts / PATH symlinks / MIME and
URL-scheme registrations point at a directory that no longer exists. After deleting
`registry.json` itself, the app directories and their side-effects are still on
disk with no registry to track them.

**Cause:**

The registry row and the on-disk side-effects (shortcuts, hooks) outlived the app
directory, or the whole registry was removed leaving "orphan" artifacts with no
backing entry.

**Solutions:**

```shell
hina verify          # show every dangling entry / shortcut / hook / orphan
hina repair          # = `hina verify --repair`: remove them all (idempotent)
hina uninstall <app> # also works even when the app directory is already gone
```

`hina repair` prunes orphan registry rows, dangling shortcuts/hooks, and
true-orphan artifacts left after a manual `registry.json` deletion. It is safe to
re-run (e.g. from cron).

---

### PM12. Sandboxed App Won't Start / Landlock

**Symptom:**

Launching a sandboxed app on Linux, the log shows one of:

```
warn: Landlock ruleset creation failed; running unsandboxed.
warn: This app requested filesystem sandboxing, but this platform cannot enforce
      it. Running unsandboxed.
```

The app still starts.

**Cause:**

The kernel is older than 5.13 (or lacks Landlock), so Hina cannot build the
ruleset. Enforcement degrades to a **no-op with a one-time warning** — by design,
Hina never blocks a launch over an unenforceable sandbox.

**Solutions:**

- If you want enforcement, run on a kernel ≥ 5.13 with Landlock available
  (unprivileged Landlock; no root or bubblewrap needed).
- Otherwise the app runs unsandboxed; this is safe to ignore if you trust the
  publisher (the descriptor is still signature-verified).

---

### PM13. `hina perms` Shows ✓ but the App Still Can't Access X

**Symptom:**

`hina perms <app>` shows a capability (e.g. `Network`, `Audio`, `Microphone`) as
`✓` / "declared (not enforced)", but the app behaves as if it has — or lacks —
that access regardless.

**Cause:**

Non-filesystem **capabilities are declared intent only**. Hina does not enforce
`network` / `audio` / `microphone` / `screen` / `input` / `devices` yet (no
portals). The `✓` records what the app *asked for*, not a grant Hina is policing.
Only the filesystem column (`FS`) is actually enforced, and only on Linux.

**Solutions:**

- For filesystem access, use `hina perms <app> --grant <path>[:rw]` — that is the
  one surface Hina enforces (Linux/Landlock).
- For capabilities, the OS itself governs access; treat the `hina perms` marker as
  a disclosure of the app's declared intent, not a Hina-applied control.

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
