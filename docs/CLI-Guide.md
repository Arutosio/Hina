# CLI Guide

The Hina CLI (`Hina.CLI`) ships as a NativeAOT single-file binary (~7.5 MB) on Windows, Linux, and macOS. The top-level surface is the cross-platform package manager. The original patcher commands moved under `hina dev <subcommand>` for app developers, CI, and troubleshooting.

For the end-user package-manager flow (install / update / uninstall / list / info / which / reinstall) see [Package Manager Guide](PackageManager-Guide.md). This document is the full CLI reference: every command, every flag, every exit code.

---

## Command Tree

```
hina <command> [args] [global flags]

Top-level (end-user package manager):
  install <url>             Install an app from a hina.app.json URL
  uninstall <name>          Remove an installed app
  list                      List installed apps
  info <name>               Show app details
  which <name>              Print the install path of an app
  update [name]             Update one app or every installed app
  reinstall <name>          Reinstall an app

hina dev <subcommand> (patcher engine + publisher helpers):
  check       --dir <path> --base <url>
  patch       --dir <path> --base <url>
  verify      --dir <path> --base <url>
  rollback    --dir <path> --base <url>
  cleanup     --dir <path> --base <url>
  sign-descriptor --in <hina.app.json> --key <ed25519.priv.b64> [--out <path>]

Global flags:
  -v, --verbose      Enable debug logging
  --allow-insecure   Permit HTTP descriptor URLs (install only)
  --help             Show help
```

---

## End-User Package Commands

### install

Install an app from the URL of its `hina.app.json` descriptor.

```shell
hina install https://example.com/foo.app.json
```

On first install Hina prompts for trust-on-first-use (TOFU) approval of the publisher's Ed25519 key fingerprint. On accept the key is pinned in the local registry; subsequent updates verify against it.

**Flags:** `--allow-insecure` (permit HTTP), global `-v`/`--verbose`.

**Exit codes:** `0` success, `1` user-cancelled, `2` failed.

### uninstall

Remove an installed app and every side-effect Hina created.

```shell
hina uninstall foo
```

Replays the registry's recorded hook evidence in reverse, removes shell entries, deletes the app dir + descriptor cache, and updates the registry. Idempotent: re-running on an already-removed name exits 0.

### list

List every installed app with its version and source URL.

```shell
hina list
```

### info

Show detailed info for one installed app: install path, channel, pinned key fingerprint, hooks executed, shell entries created, timestamps.

```shell
hina info foo
```

### which

Print the install directory of one installed app.

```shell
hina which foo
```

### update

Update one app or every installed app. Each update re-fetches the descriptor, verifies the signature against the pinned key, computes hook/entry diffs, and runs a delta patch via `PatchClient` (reuses local chunks).

```shell
hina update          # all installed apps
hina update foo      # just one
hina update --force  # run patcher even if version unchanged
```

**Exit codes:** `0` all updated (or already up to date), `2` at least one failure.

### reinstall

Reinstall an app from its registered descriptor URL.

```shell
hina reinstall foo
hina reinstall foo --rotate-key   # accept a publisher key change
```

Without `--rotate-key`, reinstall refuses to proceed if the new descriptor declares a different publisher key than the one pinned at original install time (silent key-rotation guard). The check happens before uninstall, so a refusal leaves the install intact.

---

## hina dev — Developer / Publisher Subcommands

The original patcher engine commands. End users of an app should not normally need these — they're for app developers building releases, CI, troubleshooting, and signing descriptors.

### dev check

Compare local files against a remote manifest without downloading.

```shell
hina dev check --dir ./client --base https://patch.example.com/
```

**Exit codes:** `0` up to date, `1` updates available, `2` missing args.

### dev patch

Download and apply all missing or changed files.

```shell
hina dev patch --dir ./client --base https://patch.example.com/
```

Behavior:

1. Fetch manifest; verify signature if `--pubkey` or config `trustedPublicKey` is set.
2. Roll back any incomplete previous patch (journal recovery).
3. For each file: skip if hash matches, rsync-match against local chunks, download missing chunks, verify rebuilt file, backup original, atomic swap.

**Exit codes:** `0` success, `2` failure (automatic rollback attempted).

### dev verify

Verify integrity of all local files against the manifest. No downloads, no modifications.

```shell
hina dev verify --dir ./client --base https://patch.example.com/
```

**Exit codes:** `0` all good, `3` broken files detected.

### dev rollback

Restore files from backups created during the last `patch` operation.

```shell
hina dev rollback --dir ./client --base https://patch.example.com/
```

No-op if no journal exists.

### dev cleanup

Remove leftover `*.hina.tmp`, `*.hina.bak`, and `.hina/journal.json`.

```shell
hina dev cleanup --dir ./client --base https://patch.example.com/
```

### dev sign-descriptor

Publisher-side helper to attach an Ed25519 signature to a `hina.app.json`.

```shell
hina dev sign-descriptor --in hina.app.json --key ./keys/myapp.key.b64
hina dev sign-descriptor --in hina.app.json --key ./keys/myapp.key.b64 --out signed.json
```

Validates the descriptor against the schema, attaches `descriptorSignature`, writes back (in-place if `--out` is omitted). Generate the key pair with `Hina.Builder keygen`.

---

## Flags Reference

### Top-level package commands

| Flag | Applies to | Description |
|------|------------|-------------|
| `--allow-insecure` | `install` | Permit HTTP descriptor URLs (default: HTTPS only) |
| `--rotate-key` | `reinstall` | Accept a publisher key change |
| `--force` | `update` | Re-run patcher even if descriptor version unchanged |
| `-v`, `--verbose` | all | Enable debug logging |

### hina dev patch / check / verify / rollback / cleanup

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--dir <path>` | Yes | — | Target directory to operate on |
| `--base <url>` | Yes if not in config | From config | Patch server base URL (trailing slash required) |
| `--channel <name>` | No | `stable` | Release channel — selects `manifest.json` or `manifest.<channel>.json` |
| `--config <path>` | No | `hina.config.json` in cwd | JSON config file path |
| `--pubkey <b64>` | No | From config | Trusted Ed25519 public key for manifest verification |
| `-v`, `--verbose` | No | off | Debug logging |

### hina dev sign-descriptor

| Flag | Required | Description |
|------|----------|-------------|
| `--in <path>` | Yes | Input `hina.app.json` |
| `--key <path>` | Yes | Ed25519 private key file (base64) |
| `--out <path>` | No | Output path (default: overwrite input) |

---

## Exit Codes (Summary)

| Code | Command | Meaning |
|------|---------|---------|
| `0` | any | Success |
| `0` | `dev check` | Already up to date |
| `1` | `dev check` | Updates available |
| `1` | `install` | User cancelled at TOFU prompt |
| `1` | `info` / `which` | App not installed |
| `2` | any | Missing required args, invalid input, or operation failed |
| `3` | `dev verify` | Verification failed (broken files detected) |

---

## Verbose Mode

`-v` or `--verbose` sets the log level to `Debug`. Useful for diagnosing install /
update failures and seeing rsync match counts during `dev patch`.

Example verbose output:

```
info: hina[0] Fetching descriptor https://example.com/foo.app.json
dbug: Hina.Core.Patching.PatchClient[0] Starting patch in /Users/me/.../Apps/foo
dbug: Hina.Core.Patching.PatchClient[0] Rsync matched 14/16 chunks for bin/foo
dbug: Hina.Core.Patching.PatchClient[0] Downloading chunk 3 for bin/foo
info: hina[0] Installed foo 1.0.0 → /Users/me/.../Apps/foo
```

---

## Common Workflows

### Install a published app (end user)

```shell
hina install https://foo.example/hina.app.json
hina list
hina info fooedit
```

### Update everything once a day (cron)

```shell
hina update
```

Exit code 2 means at least one app failed; check per-app messages in stderr.

### Build, sign, and ship a release (publisher)

```shell
# 1. Build the chunk store + manifest with the private key
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://cdn.foo.example/foo/ \
  --version 1.0.0 \
  --sign-key ./keys/foo.key.b64

# 2. Upload ./patch to your CDN at https://cdn.foo.example/foo/

# 3. Author and sign the descriptor
hina dev sign-descriptor --in hina.app.json --key ./keys/foo.key.b64

# 4. Host hina.app.json at any URL. Tell users:
#    hina install https://foo.example/hina.app.json
```

### Verify and repair a corrupted install (advanced)

`hina` doesn't expose `verify` at the top level for installed apps — use the
patcher engine directly with the app's recorded baseUrl from `hina info`:

```shell
hina info foo                                  # note the BaseUrl
hina dev verify --dir <install path> --base <BaseUrl>
hina dev patch  --dir <install path> --base <BaseUrl>   # re-patch only broken chunks
```

### Scripted update with error handling

```bash
#!/bin/bash
set -e

if ! hina update; then
    echo "One or more updates failed; see above."
    exit 1
fi
echo "All apps up to date."
```

---

## See Also

- [Package Manager Guide](PackageManager-Guide.md) — descriptor schema, hooks, signature chain, per-OS install paths, troubleshooting
- [Builder Guide](Builder-Guide.md) — `dotnet run --project Hina.Builder -- build/keygen` details
- [Configuration](Configuration.md) — `hina.config.json` reference for `hina dev <cmd>` flows
- [Security](Security.md) — Ed25519 chain, TOFU + pinning, threat model
- [Troubleshooting](Troubleshooting.md) — error scenarios with causes and fixes
