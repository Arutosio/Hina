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
  run <app> [entry]         Launch a sandboxed app through Hina
  perms [app]               Show app permissions (aliases: permissions, permessi)
  verify [name]             Reconcile registry + check local integrity (--deep)
  repair [name]             Alias for `verify [name] --repair`
  version                   Print the installed Hina version
  check-update              Check whether a newer Hina release is available

hina dev <subcommand> (patcher engine + publisher helpers):
  check       --dir <path> --base <url>
  patch       --dir <path> --base <url>
  verify      --dir <path> --base <url>
  rollback    --dir <path> --base <url>
  cleanup     --dir <path> --base <url>
  sign-descriptor --in <hina.app.json> --key <ed25519.priv.b64> [--out <path>]
  sandbox-run --app-dir <dir> [--allow <path>[:ro|:rw]] [--host] -- <exec> [args]

Global flags:
  -v, --verbose      Enable debug logging
  --allow-insecure   Permit HTTP descriptor URLs (install only)
  -V, --version      Print the installed Hina version
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

If a new version **broadens** the app's sandbox, the update is refused (nothing
written to disk) and the new permissions are listed. Re-run with
`--accept-new-permissions` to consent. Permission **narrowing** applies
automatically.

```shell
hina update foo --accept-new-permissions
```

**Exit codes:** `0` all updated (or already up to date), `2` at least one failure.

### reinstall

Reinstall an app from its registered descriptor URL.

```shell
hina reinstall foo
hina reinstall foo --rotate-key   # accept a publisher key change
```

Without `--rotate-key`, reinstall refuses to proceed if the new descriptor declares a different publisher key than the one pinned at original install time (silent key-rotation guard). The check happens before uninstall, so a refusal leaves the install intact.

### run

Launch a sandboxed app's executable **through Hina**, so the filesystem sandbox
is installed before the binary execs. Shell shortcuts for sandboxed apps point
their launch command at `hina run <app> <entry>` instead of at the binary.

```shell
hina run foo                 # default entry / per-OS executable
hina run foo editor          # a specific shell entry by id
hina run foo -- --flag arg   # everything after -- is passed to the app
```

The optional second positional is the entry id; everything after a bare `--` is
forwarded to the app unchanged. If the app declares no sandbox, it runs
unrestricted.

`run` never falls back to an unsandboxed launch when it can't read the sandbox
scope. It exits `1` (and suggests `hina reinstall`) if the app isn't installed,
the install directory is missing, the cached descriptor is missing or corrupt,
or the resolved executable is gone. Missing args exit `2`.

### perms

Show the declared and granted permissions of installed apps. Aliases:
`permissions`, `permessi`.

```shell
hina perms                 # overview table of all apps
hina perms list            # same as above
hina perms foo             # full detail for one app
```

The overview table has columns `APP SANDBOX FS NET AUDIO MIC SCREEN INPUT DEV`.
The `FS` column summarizes the filesystem scope: declared tokens, `host(!)` when
the app requests unrestricted host access, and `+Ng` for N user grants. A legend
notes that **the filesystem (FS) and network (NET) are enforced** (Linux/Landlock +
macOS/sandbox-exec; NET needs Linux 6.7+); the other capability columns are
declared by the app but not yet enforced.

`hina perms <app>` prints the per-app detail: whether the sandbox is on or off;
the **Filesystem (enforced)** section listing the install dir, each declared
token with its access, and each user-granted absolute path; then **Network**
(`allowed (enforced)` or `denied (enforced …)`); then Audio, Microphone, Screen,
Input and Devices, each shown as `declared (not enforced)` or `not declared`.

Manage the user's extra runtime filesystem grants (persisted in the registry):

```shell
hina perms foo --grant ~/Documents          # default access: ro
hina perms foo --grant ~/Projects:rw         # read-write
hina perms foo --revoke ~/Documents
```

Paths support `~` and are stored absolute. A grant with no `:ro`/`:rw` suffix is
read-only. Granting a path that already has a grant replaces it.

### verify

Reconcile the local registry against on-disk state **and** check local
integrity. Detects orphans created when the user manually deletes an app
directory, breaks a symlink, or removes a shortcut, and — always, offline —
confirms that the per-OS executable and every `entries[].exec` exist under the
install dir, flagging a missing or corrupt descriptor cache.

```shell
hina verify                # report problems across all apps
hina verify foo            # one app
hina verify --repair       # report + clean repairable problems
hina verify --deep         # also re-fetch the manifest and hash every file
```

`--repair` cleans the repairable problems (orphan registry rows, dangling
shortcuts/hooks, a missing app directory). The `hina repair` hint is only shown
when such repairable problems exist. Missing **files** can't be restored by
repair, so those instead suggest `hina reinstall`.

`--deep` additionally re-fetches the manifest and hash-verifies every file,
catching modified or truncated content the offline check can't see. It needs
network; if the manifest is unreachable the app is reported as not deep-verified
rather than crashing.

Detection is read-only; only `--repair` mutates anything. Exit code `0` when all
inspected apps are healthy or when `--repair` succeeds; `1` when problems were
found and not repaired; `2` on internal error.

### repair

Alias for `hina verify [name] --repair`.

```shell
hina repair                # repair all apps
hina repair foo            # one app
```

Removes orphan registry rows (left when an app directory was deleted by hand),
dangling shortcuts/hooks, and true-orphan artifacts left after a manual
`registry.json` deletion — on Linux, `hina-*.desktop` files in the applications
and autostart directories and `hina-*` fonts. Fail-soft: it cleans what it can
and does not abort on a single failure.

### version

Print the installed Hina version.

```shell
hina version          # -> "hina 1.0.0"
hina --version        # same; -V also works
```

The version is baked into the binary (`HinaVersion.Current`) and always equals
the GitHub release tag it was built from (the release pipeline fails if they
disagree), so `hina version` is authoritative.

### check-update

Ask GitHub whether a newer Hina release exists and report the result.

```shell
hina check-update     # also accepts: hina check update
```

Exit codes are script-friendly: `0` already up to date, `10` an update is
available (the message prints the install one-liner), `2` the check failed
(offline / rate-limited). Releases use plain SemVer tags (`v1.0.0`, `v1.0.1`),
so the comparison is a straight version ordering.

To upgrade, re-run the installer (it detects the older version and offers an
update), or pin a tag with `HINA_VERSION`:

```shell
curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash
```

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

### dev sandbox-run

Apply a filesystem sandbox (Landlock on Linux) then exec a command. Drives the
Landlock CI integration test and is handy for manual sandbox debugging.

```shell
hina dev sandbox-run --app-dir ./client -- ./client/foo
hina dev sandbox-run --app-dir ./client --allow ~/data:rw -- ./client/foo
hina dev sandbox-run --app-dir ./client --host -- ./client/foo
```

The app dir is added read-only; each `--allow <path>[:ro|:rw]` (default `ro`)
grants an additional path; `--host` runs unrestricted. Everything after the bare
`--` is the executable and its arguments. On success the process image is
replaced, so the exec's own exit code is the result.

---

## Flags Reference

### Top-level package commands

| Flag | Applies to | Description |
|------|------------|-------------|
| `--allow-insecure` | `install` | Permit HTTP descriptor URLs (default: HTTPS only) |
| `--rotate-key` | `reinstall` | Accept a publisher key change |
| `--force` | `update` | Re-run patcher even if descriptor version unchanged |
| `--accept-new-permissions` | `update` | Consent to an update that broadens the app's sandbox |
| `--jobs N` | `update` | Update N apps concurrently (default 4) |
| `--repair` | `verify` | Remove orphan registry entries + dangling side-effects |
| `--deep` | `verify` | Re-fetch the manifest and hash-verify every file (needs network) |
| `--grant <path>[:ro\|:rw]` | `perms` | Add a user filesystem grant (default `ro`); `~` expanded |
| `--revoke <path>` | `perms` | Remove a user filesystem grant |
| `--retries N` | `install`, `update` | Max retry attempts per HTTP request (default 8) |
| `--connect-timeout SEC` | `install`, `update` | TCP connect timeout in seconds (default 10) |
| `--request-timeout SEC` | `install`, `update` | Overall request timeout in seconds (default 60) |
| `-v`, `--verbose` | all | Enable debug logging |

### Network knobs

The three network flags exist for flaky / mobile / changing-IP connections where
the engine's defaults bail too early. Hina already pools and recycles its HTTP
connections every 60 s (forces DNS refresh after an IP change), but you can push
the retry budget higher and the timeouts tighter when packets get lost
frequently:

```shell
hina install <url> --retries 20 --connect-timeout 5 --request-timeout 30
hina update      --retries 20 --connect-timeout 5 --request-timeout 30
```

Smaller timeouts = faster failure = faster retry against the next route.

### Cancellation (Ctrl-C)

Every long-running command honours Ctrl-C cooperatively:

- **First press**: cancellation token fires. In-flight install / update rolls
  back via PatchClient + InstallTransaction; the registry is left in its
  pre-operation state. You should see "Cancellation requested. Press Ctrl-C
  again to force-exit." in stderr.
- **Second press**: the runtime kills the process. The journal at
  `<appDir>/.hina/journal.json` is left in place; the next `hina update` /
  `hina install` detects it, rolls back any partial changes, and the rsync
  matcher reuses every chunk that already made it to disk — effectively a
  free resume.

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

### hina dev sandbox-run

| Flag | Required | Description |
|------|----------|-------------|
| `--app-dir <path>` | Yes | Install dir, added read-only |
| `--allow <path>[:ro\|:rw]` | No | Extra path to allow (default `ro`); repeatable |
| `--host` | No | Run unrestricted (no filesystem isolation) |
| `-- <exec> [args...]` | Yes | Command to exec after the sandbox is applied |

---

## Exit Codes (Summary)

| Code | Command | Meaning |
|------|---------|---------|
| `0` | any | Success |
| `0` | `dev check` | Already up to date |
| `1` | `dev check` | Updates available |
| `1` | `install` | User cancelled at TOFU prompt |
| `1` | `info` / `which` | App not installed |
| `1` | `run` | App / install dir / descriptor cache / executable missing or corrupt |
| `1` | `verify` / `repair` | Problems found and not repaired |
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

### Verify and repair a corrupted install

Use the top-level `verify` / `repair` commands:

```shell
hina verify foo            # offline integrity + registry check
hina verify foo --deep     # also re-fetch the manifest and hash every file
hina repair foo            # clean orphans / dangling side-effects (= verify --repair)
hina reinstall foo         # restore missing or modified files
```

`verify` reports the problem and points you at the right fix: repairable
problems (orphan registry rows, dangling shortcuts/hooks, a missing app dir) at
`hina repair`; missing or corrupt files (which repair can't restore) at
`hina reinstall`.

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
