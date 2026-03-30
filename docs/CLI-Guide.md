# CLI Guide

The Hina CLI (`Hina.CLI`) is the client-side patcher. It downloads manifests, compares local files, downloads missing chunks, and applies updates. This document covers all commands, flags, exit codes, and common workflows.

---

## General Usage

```
hina <command> --dir <path> --base <url> [options]
```

The CLI requires at minimum a command, a target directory (`--dir`), and a patch server URL (either `--base` flag or `baseUrl` in config).

---

## Commands

### check

Compares local files against the remote manifest without downloading anything. Reports whether updates are available.

**What it does:**

1. Fetches the manifest from the server.
2. Verifies the manifest signature (if `trustedPublicKey` is configured).
3. For each file in the manifest, computes the local file's SHA-256 hash.
4. If any file is missing or has a different hash, reports that an update is available.

```shell
hina check --dir ./client --base https://patch.example.com/
```

**Output:** Prints either "Already up to date." or "Missing files." / "Out of date files."

---

### patch

Downloads and applies all missing or changed files. This is the primary command for updating a client.

**What it does:**

1. Fetches the manifest from the server.
2. Verifies the manifest signature (if configured).
3. Checks for an incomplete previous patch (journal). If found, rolls back first.
4. Creates a new patch journal.
5. For each file in the manifest:
   - Skips files whose local hash already matches.
   - Performs rsync rolling checksum matching against the local file.
   - Builds the updated file from matched local chunks and downloaded remote chunks.
   - Verifies the rebuilt file hash (if `verify` is enabled).
   - Backs up the original file (if `backup` is enabled).
   - Swaps the new file into place.
6. On failure, rolls back all changes from backups.

```shell
hina patch --dir ./client --base https://patch.example.com/
```

---

### verify

Verifies the integrity of all local files against the manifest hashes. Does not download or modify any files.

**What it does:**

1. Fetches the manifest from the server.
2. Verifies the manifest signature (if configured).
3. For each file in the manifest, computes the local file's SHA-256 hash.
4. Reports any missing files or hash mismatches.

```shell
hina verify --dir ./client --base https://patch.example.com/
```

**Output:** Prints "OK" if all files match, or "Broken files detected." with details.

---

### rollback

Restores files from backups created during the last patch operation.

**What it does:**

1. Loads the patch journal from `.hina/journal.json` in the target directory.
2. For each journal entry, copies the `.hina.bak` file back to the original path.
3. Deletes the backup files and the journal.

```shell
hina rollback --dir ./client --base https://patch.example.com/
```

If no journal exists, rollback completes silently with no changes.

---

### cleanup

Removes leftover temporary and backup files from a previous patch.

**What it does:**

1. Recursively scans the target directory.
2. Deletes all files ending in `.hina.tmp`.
3. Deletes all files ending in `.hina.bak`.
4. Deletes the patch journal (`.hina/journal.json`).

```shell
hina cleanup --dir ./client --base https://patch.example.com/
```

Use this after a successful patch to reclaim disk space from backup files, or after a failed patch when you do not want to rollback.

---

## Flags

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--dir` | Yes | -- | Target directory to patch. This is the root of your application or game installation. |
| `--base` | Conditional | From config | Patch server base URL. Required if not set in the config file. Must include a trailing slash. |
| `--channel` | No | `"stable"` | Release channel. Determines which manifest to fetch (`manifest.json` for stable, `manifest.<channel>.json` for others). |
| `--config` | No | -- | Path to a `hina.config.json` file. If not provided, the CLI looks for `hina.config.json` in the current working directory. |
| `--pubkey` | No | -- | Path to an Ed25519 public key file (`.pub.b64`) for manifest signature verification. Overrides `trustedPublicKey` in config. |
| `-v`, `--verbose` | No | off | Enable debug-level logging. Shows detailed output for each step of the patch process. |
| `--help` | No | -- | Display help information and exit. |

### Flag Precedence

Command-line flags override config file values:

| Setting | Resolution order |
|---------|-----------------|
| Base URL | `--base` flag > `baseUrl` in config > default (`http://localhost/`) |
| Channel | `--channel` flag > `channel` in config > default (`"stable"`) |
| Public key | `--pubkey` flag > `trustedPublicKey` in config > none |
| Config file | `--config` flag > `hina.config.json` in cwd > defaults |

---

## Exit Codes

| Code | Command | Meaning |
|------|---------|---------|
| `0` | `check` | No updates available (already up to date) |
| `1` | `check` | Updates are available |
| `0` | `patch` | Patch applied successfully |
| `2` | `patch` | Patch failed |
| `0` | `verify` | All files verified successfully |
| `3` | `verify` | Verification failed (broken files detected) |
| `0` | `rollback` | Rollback completed successfully |
| `0` | `cleanup` | Cleanup completed successfully |
| `2` | (any) | Missing required arguments or unknown command |

These exit codes can be used in scripts and CI/CD pipelines to branch on the result.

---

## Verbose Mode and Debugging

Pass `-v` or `--verbose` to enable debug-level log output. This reveals:

- Each file being checked or patched.
- Rsync match results (how many chunks matched locally vs. needed download).
- Individual chunk download events.
- Hash verification results.
- Backup and journal operations.
- Retry attempts with delay information.

**Example verbose output:**

```
info: Hina.CLI[0] Starting patch in ./client
dbug: Hina.CLI[0] File already up to date, skipping data/config.json
info: Hina.CLI[0] Patching file game.exe
dbug: Hina.CLI[0] Rsync matched 14/16 chunks for game.exe
dbug: Hina.CLI[0] Downloading chunk 3 for game.exe
dbug: Hina.CLI[0] Downloading chunk 11 for game.exe
dbug: Hina.CLI[0] Verification passed for game.exe
info: Hina.CLI[0] Patch completed successfully, 1 files applied
```

---

## Common Workflows

### First Install (No Local Files)

When patching a fresh directory with no existing files, every chunk is downloaded from the server. No rsync matching occurs because there are no local files to match against.

```shell
mkdir ./client
hina patch --dir ./client --base https://patch.example.com/ --pubkey ./keys/myapp.pub.b64
```

### Regular Update

For subsequent updates, the patcher compares local files against the new manifest. Matching chunks are reused from local files, and only changed chunks are downloaded.

```shell
hina patch --dir ./client --base https://patch.example.com/ --pubkey ./keys/myapp.pub.b64
```

### Check Before Patching

Use `check` in a launcher UI to show whether an update is available before prompting the user.

```shell
hina check --dir ./client --base https://patch.example.com/
# Exit code 0 = up to date, 1 = update available
```

### Verify and Repair

Run `verify` to detect corrupted files, then `patch` to repair them.

```shell
# Step 1: Check integrity
hina verify --dir ./client --base https://patch.example.com/
# Exit code 3 means broken files

# Step 2: Repair by re-patching (will download only broken chunks)
hina patch --dir ./client --base https://patch.example.com/
```

### Rollback a Bad Update

If an update causes problems, rollback to the previous version from backups.

```shell
hina rollback --dir ./client --base https://patch.example.com/
```

This restores all files that were backed up during the last patch. Rollback is only available if `backup` was enabled in the configuration (it is by default).

### Clean Up After a Successful Patch

After confirming an update works, remove backup files to save disk space.

```shell
hina cleanup --dir ./client --base https://patch.example.com/
```

### Using a Config File

Instead of passing flags on every invocation, create a config file.

```shell
# Create hina.config.json in the working directory
cat > hina.config.json << 'EOF'
{
  "baseUrl": "https://patch.example.com/",
  "trustedPublicKey": "BASE64_ED25519_PUBLIC_KEY",
  "concurrency": 4,
  "verify": true,
  "backup": true
}
EOF

# Now commands only need --dir
hina check --dir ./client
hina patch --dir ./client
hina verify --dir ./client
```

### Beta Channel

Test pre-release updates by specifying a channel.

```shell
hina patch --dir ./client --base https://patch.example.com/ --channel beta
```

This fetches `manifest.beta.json` instead of `manifest.json`. The builder must have produced a separate manifest for the beta channel.

### Scripted Update with Error Handling

```shell
#!/bin/bash
set -e

CLIENT_DIR="./client"
BASE_URL="https://patch.example.com/"
PUBKEY="./keys/myapp.pub.b64"

# Check for updates
hina check --dir "$CLIENT_DIR" --base "$BASE_URL" --pubkey "$PUBKEY"
CHECK_EXIT=$?

if [ $CHECK_EXIT -eq 0 ]; then
    echo "Already up to date."
    exit 0
fi

# Apply patch
if hina patch --dir "$CLIENT_DIR" --base "$BASE_URL" --pubkey "$PUBKEY"; then
    echo "Patch applied successfully."
    hina cleanup --dir "$CLIENT_DIR" --base "$BASE_URL"
else
    echo "Patch failed, rolling back."
    hina rollback --dir "$CLIENT_DIR" --base "$BASE_URL"
    exit 1
fi
```
