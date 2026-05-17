# Hina Package Manager Guide

Hina ships a cross-platform package manager on top of the rsync-style patcher engine.
End users install apps from a publisher-hosted URL; updates are delta-fetched and
cleanly uninstallable.

This guide covers the user-facing CLI and the wire format publishers author.

---

## End-User CLI

| Command | What it does |
|---------|--------------|
| `hina install <url>` | Install an app from a `hina.app.json` URL |
| `hina uninstall <name>` | Remove an installed app and all its side-effects |
| `hina list` | List installed apps (name, version, source URL) |
| `hina info <name>` | Show install path, channel, pinned key, hooks, shell entries |
| `hina which <name>` | Print the install path of an app |
| `hina update [name]` | Update one app or all installed apps |
| `hina reinstall <name>` | Reinstall (use `--rotate-key` to accept a new publisher key) |
| `hina dev <subcommand>` | Advanced patcher commands (`check`, `patch`, `verify`, `rollback`, `cleanup`) |

Global flags:

- `-v`, `--verbose` — enable debug-level logging
- `--allow-insecure` — permit HTTP descriptor URLs on `install`

---

## Per-User Install Paths

Hina installs apps under user-scope OS-standard directories. No admin / sudo required.

| OS | Apps root | Registry file | Bin (for `addToPath`) |
|----|-----------|---------------|------------------------|
| Windows | `%LOCALAPPDATA%\Hina\Apps\` | `%LOCALAPPDATA%\Hina\registry.json` | `%LOCALAPPDATA%\Hina\bin\` (auto-added to user PATH) |
| Linux | `~/.local/share/hina/apps/` (or `$XDG_DATA_HOME/hina/apps`) | `~/.local/share/hina/registry.json` | `~/.local/bin/` |
| macOS | `~/Library/Application Support/Hina/Apps/` | `~/Library/Application Support/Hina/registry.json` | `~/.local/bin/` |

Shortcut destinations:

| OS | Menu entry | Autostart | Fonts |
|----|------------|-----------|-------|
| Windows | Start Menu `Programs\Hina\<name>.lnk` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | `%LOCALAPPDATA%\Microsoft\Windows\Fonts\` |
| Linux | `~/.local/share/applications/hina-<id>.desktop` | `~/.config/autostart/hina-autostart-<id>.desktop` | `~/.local/share/fonts/` |
| macOS | `~/Applications/<Name>.app` | `~/Library/LaunchAgents/com.hina.autostart.<id>.plist` | `~/Library/Fonts/` |

---

## Publisher: Authoring `hina.app.json`

The publisher hosts a single JSON descriptor at any URL. When the user runs
`hina install https://example.com/foo.app.json`, Hina fetches that file,
validates it, verifies the signature, and runs the install.

Minimal example:

```json
{
  "schemaVersion": 1,
  "name": "fooedit",
  "displayName": "FooEdit",
  "version": "2.3.1",
  "publisher": "Foo Software Ltd.",
  "description": "A fast text editor.",
  "homepage": "https://foo.example/",
  "license": "MIT",
  "icon": "icons/fooedit.png",
  "minHinaVersion": "0.4.0",

  "baseUrl": "https://cdn.foo.example/fooedit/",
  "channel": "stable",
  "publicKey": "BASE64_ED25519_32_BYTES",

  "exec": {
    "windows": "bin\\fooedit.exe",
    "linux":   "bin/fooedit",
    "macos":   "FooEdit.app/Contents/MacOS/fooedit"
  },

  "entries": [
    { "id": "main", "name": "FooEdit", "exec": "bin/fooedit",
      "icon": "icons/fooedit.png", "categories": ["Development", "TextEditor"],
      "terminal": false }
  ],

  "postInstall": [
    { "action": "addToPath",         "name": "fooedit", "target": "bin/fooedit" },
    { "action": "registerMimeType",  "mimeType": "application/x-fooedit", "extensions": [".foo"], "entryId": "main" },
    { "action": "registerUrlScheme", "scheme": "fooedit", "entryId": "main" },
    { "action": "installFont",       "files": ["assets/Foo.ttf"] },
    { "action": "registerAutostart", "entryId": "main", "args": ["--minimized"] }
  ],

  "descriptorSignature": {
    "algorithm": "ed25519",
    "signature": "BASE64_SIGNATURE",
    "publicKey": "BASE64_ED25519_32_BYTES"
  }
}
```

### Validation rules

- `name` matches `^[a-z][a-z0-9-]{1,63}$`
- `version` and `minHinaVersion` are SemVer
- `baseUrl` is HTTPS (HTTP requires user-side `--allow-insecure`)
- `publicKey` is a valid base64 32-byte Ed25519 key
- Hook paths (`target`, `exec`, `icon`, `files`) are relative — no `..`, no absolute paths
- Every hook `entryId` must match an `entries[].id`
- `exec` must define at least one platform

### `baseUrl` content

`baseUrl` points at the directory containing `manifest.json` and the `chunks/` tree
that `Hina.Builder` produced. The patcher delta-downloads from there.

### Hook actions

| Action | Effect | Evidence stored in registry |
|--------|--------|-----------------------------|
| `addToPath` | Adds the target exec to the user PATH (symlink on Linux/macOS, `.cmd` shim on Windows) | path to the symlink / `.cmd` |
| `registerMimeType` | Associates a MIME type and file extensions with an entry | per-OS resource path / registry key |
| `registerUrlScheme` | Registers a `scheme://` URL handler | per-OS resource path / registry key |
| `installFont` | Copies font files into the user-scope fonts dir | path(s) to installed font files |
| `registerAutostart` | Runs the entry at login | per-OS resource path / registry value |

Hooks are **whitelisted and declarative** — arbitrary scripts are not supported. This is
deliberate: a compromised publisher cannot ship malicious code.

---

## Signature Chain

1. The descriptor is signed with the publisher's Ed25519 key (`descriptorSignature`).
2. The manifest at `baseUrl/manifest.json` is signed with the **same** Ed25519 key.

When a user runs `hina install <url>`:

1. Descriptor is fetched and parsed.
2. Signature is verified against the descriptor's own declared `publicKey`.
3. A **trust-on-first-use (TOFU)** prompt shows the publisher name, source URL, and
   key fingerprint. The user accepts or rejects.
4. The patcher downloads chunks, verifying the manifest against the pinned key.
5. The pinned key is stored in the local registry alongside the install entry.

On every subsequent `hina update`:

- The new descriptor must verify against the **pinned key**, not its own declared key.
- A descriptor signed with a different key is **rejected** as a potential key-rotation
  attack.
- Real publisher key rotations require the user to opt in with
  `hina reinstall <name> --rotate-key`.

---

## Uninstall: Truth from the Registry

`hina uninstall <name>` reads side-effects from the local registry, **never** from
the live descriptor:

- A newer descriptor might list different hooks.
- The publisher might have replaced the file Hina would otherwise use as a reference.

This means uninstall is always faithful to what was actually installed, and it is
idempotent — repeated runs converge to "clean".

---

## Update Flow

`hina update [name]` re-fetches the descriptor for one or all installed apps:

1. Verify against the pinned key.
2. If the new descriptor version matches the installed version, exit as already
   up-to-date (use `--force` to re-run the patcher anyway).
3. Diff hook identity and entry id between the old registry record and the new
   descriptor.
4. Call `PatchClient.PatchAsync` for a delta download (rsync rolling checksums
   reuse local chunks).
5. Remove obsolete hooks and entries; add new ones.
6. Update the registry.

On any post-patch failure: `PatchClient.RollbackAsync` restores files from backups,
and the registry is reverted to the pre-update snapshot.

---

## Building a Release as a Publisher

Existing patcher tooling is reused — see
[`docs/Builder-Guide.md`](Builder-Guide.md) for the chunk store + manifest workflow.

End-to-end:

1. Generate an Ed25519 key pair with `dotnet run --project Hina.Builder -- keygen`.
2. Build the manifest with `--sign-key` pointed at your private key.
3. Upload the resulting `manifest.json` and `chunks/` tree to your CDN at the URL
   that the descriptor's `baseUrl` points to.
4. Sign `hina.app.json` with the same private key (Hina exposes
   `DescriptorSigner.AttachSignature` for build tooling; a `hina dev sign-descriptor`
   command is planned).
5. Host the descriptor at any URL you control. Tell users to run
   `hina install <descriptor-url>`.

---

## Troubleshooting

**"Descriptor signature does not match its declared publicKey."**
The descriptor's `descriptorSignature.signature` was computed with a different key
than the one in `descriptorSignature.publicKey`. Re-sign with the matching key.

**"Descriptor signature does not match the pinned publisher key."**
The publisher rotated their signing key. If you trust the rotation, run
`hina reinstall <name> --rotate-key` to accept the new key.

**`hina list` shows the app but `hina info` says "not installed"**
The registry file may have been partially restored from backup. Run `hina list`
again — if it still appears, file an issue with the registry JSON contents.

**Install hangs at "Trust this publisher and install?"**
The TOFU prompt is reading from stdin. If you're running via SSH/CI, redirect
input or pre-accept the publisher in a future non-interactive flow.

**macOS: `hina install` says signature is invalid but the file was just fetched**
Check that the descriptor was not mangled by HTML rewriting at your CDN edge
(some CDNs add UTF-8 BOM, line-ending changes, or pretty-printing). Serve
`hina.app.json` with `Content-Type: application/json` and disable any rewrites.
