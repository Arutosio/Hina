# Hina Package Manager Guide

Hina ships a cross-platform package manager on top of the rsync-style patcher engine.
End users install apps from a publisher-hosted URL; updates are delta-fetched and
cleanly uninstallable.

This guide covers the user-facing CLI and the wire format publishers author.

---

## Installing the Hina CLI

**Quick install (curl | sh).** On Linux and macOS:

```sh
curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash
```

On Windows (PowerShell 5.1+):

```powershell
iwr -useb https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.ps1 | iex
```

The one-liner downloads the latest release for your OS/arch, verifies it
against the published SHA-256, drops `hina` into `~/.local/bin` (Unix) or
`%LOCALAPPDATA%\Hina\bin` (Windows) via an atomic rename (power-loss safe),
and prepends the install directory to your `PATH`.

**Re-running on an existing install.** When the installer detects an
existing `hina` binary, it shows a five-option menu:

1. **Reinstall** — replace the binary; keep installed apps and registry.
2. **Clean reinstall** — wipe the registry, installed apps, and pinned
   publisher keys, then reinstall fresh. The installer prints the exact
   paths it will delete and requires typing `yes` to confirm. Side-effect
   files (desktop entries, Start Menu shortcuts, `.cmd` shims, fonts)
   become orphaned and need manual cleanup.
3. **Integrity check** — re-download the release, verify its SHA-256,
   compare the released binary's hash against the installed binary's
   hash, and report match or mismatch. No filesystem changes.
4. **Exit** — leave the installation as-is.
5. **Uninstall** — remove hina with one of three granularities:
   - **(a) Full** — remove hina binary + all installed apps + configs
     (registry, keys, descriptors). Per-app teardown runs
     `hina uninstall <name>` first so side-effects (desktop entries,
     shortcuts, shims, fonts, HKCU MIME entries on Windows) are cleaned.
     Also strips the marker-delimited PATH stanza this installer added
     to your shell rc files (Unix) or the user-PATH entry on Windows.
     Requires typing `yes` to confirm.
   - **(b) Configs** — remove hina binary + configs (registry.json,
     descriptors/, pinned keys) but **keep app binaries on disk** as
     orphan files. hina will no longer track them; you can hand-curate
     `~/.local/share/hina/Apps/` (or `%LOCALAPPDATA%\Hina\Apps\`).
     Requires typing `yes` to confirm.
   - **(c) Binary only** — remove just the hina binary. Apps + configs
     are preserved. Reinstalling hina later resumes the previous state.
     Simple `y/N` confirmation.

In non-interactive contexts (CI / `curl | bash` with no controlling
terminal) the menu is skipped: the installer reinstalls if the target
version differs and exits if it matches. Use `HINA_ACTION` to force a
specific path (e.g. `HINA_ACTION=uninstall-full`). Clean reinstall and
uninstall-full / uninstall-configs are never automatic — they each
require a second gate (`HINA_PURGE_YES=1` for clean reinstall,
`HINA_UNINSTALL_YES=1` for the destructive uninstall modes).

**Environment overrides:**

| Variable | Effect |
|----------|--------|
| `HINA_VERSION` | Pin a specific tag, e.g. `HINA_VERSION=v1.2.3`. Default: latest release. |
| `HINA_INSTALL_DIR` | Override destination directory. |
| `HINA_NO_MODIFY_PATH=1` | Skip the shell-rc / user-PATH edit; print manual instructions instead. |
| `HINA_ACTION` | Bypass the menu: `reinstall`, `purge`, `verify`, `exit`, `uninstall-full`, `uninstall-configs`, or `uninstall-binary`. |
| `HINA_PURGE_YES=1` | Required alongside `HINA_ACTION=purge` to confirm a clean reinstall non-interactively. |
| `HINA_UNINSTALL_YES=1` | Required alongside `HINA_ACTION=uninstall-full` or `HINA_ACTION=uninstall-configs` for non-interactive confirmation. `uninstall-binary` is unaffected. |
| `HINA_NO_CHECKSUM=1` | Skip SHA-256 verification (debug only — leaves you vulnerable to corruption and tampering). |

**Resilience.** The installer downloads to a `.partial` file with
`curl -C -` resume, retries up to 5× on network errors, verifies the
archive's published `.sha256`, and only swaps the live binary via an
atomic rename after a smoke test of the new binary. If anything fails
mid-rename, the previous binary is restored from a backup. A lock file
prevents concurrent installs from racing.

**Manual / package-manager install.** GitHub releases ship one or more
artifacts per platform. Pick the one that matches your OS:

| OS | File | How to install |
|----|------|----------------|
| macOS (Apple Silicon) | `Hina-macos-arm64.pkg` | double-click; the first time, right-click → Open to bypass Gatekeeper (the `.pkg` is unsigned until we wire up an Apple Developer cert) |
| macOS (Intel) | `Hina-macos-x64.pkg` | same |
| Debian / Ubuntu / derivatives | `Hina-linux-x64.deb` / `Hina-linux-arm64.deb` | `sudo dpkg -i Hina-linux-*.deb` |
| **Arch / Fedora / openSUSE / any other Linux** | `Hina-linux-x64.tar.gz` / `Hina-linux-arm64.tar.gz` | `tar -xzf ...tar.gz && cd Hina-linux-* && ./install.sh` — lands in `~/.local/bin/hina`, no root needed |
| Windows (x64 / arm64) — preferred | `Hina-windows-x64.msi` / `Hina-windows-arm64.msi` | double-click; installs to `%LOCALAPPDATA%\Hina\bin` and prepends to user `PATH` (no admin) |
| Windows — via Scoop | `hina.json` from the same release | `scoop install https://github.com/Arutosio/Hina/releases/latest/download/hina.json` (auto-update via `scoop update` after that) |
| Windows — fallback | `Hina-windows-x64.zip` / `Hina-windows-arm64.zip` | extract, run `install.bat` |

The `.tar.gz` + `install.sh` flow is the universal fallback and works on every
Linux distribution (including Arch — there is no AUR PKGBUILD yet). It installs
into `~/.local/bin/hina` without root; make sure that directory is on your
`PATH` (most shells put it there by default).

After install:

```shell
hina --help
hina install <url-to-hina.app.json>
```

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
| `hina run <app> [entryId] [-- args...]` | Launch a sandboxed app through Hina so its filesystem sandbox is installed before exec (the launch chokepoint a sandboxed app's shortcut points at) |
| `hina perms` / `permissions` / `permessi` | Inspect each app's declared + granted permissions: `perms [list]` for a table, `perms <app>` for detail, `perms <app> --grant <path>[:ro\|:rw]` / `--revoke <path>` to manage runtime filesystem grants |
| `hina verify [name] [--repair] [--deep]` | Reconcile registry against on-disk state; check local integrity (per-OS exec + every `entries[].exec` + descriptor cache present); detect orphans (missing dirs, dangling shortcuts) and optionally clean them. `--deep` re-fetches the manifest and hash-verifies every file |
| `hina repair [name]` | Alias for `verify --repair`: remove orphan registry entries + dangling side-effects |
| `hina dev <subcommand>` | Advanced patcher / publisher commands (`check`, `patch`, `verify`, `rollback`, `cleanup`, `sign-descriptor`) |

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

Each registry row also carries a `userGrants` list — the absolute filesystem paths
the user has granted a sandboxed app via `hina perms --grant`, additive over the
descriptor's declared scope and preserved across updates. The cached descriptor
that backs `run`/`perms`/`verify` is written atomically, so a crash mid-write can
never leave a half-written descriptor behind.

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

  "sandbox": {
    "enabled": true,
    "filesystem": [
      { "path": "home",           "access": "ro" },
      { "path": "xdg-documents",  "access": "rw" }
    ],
    "capabilities": { "network": true, "audio": false, "microphone": false, "screen": false, "input": false, "devices": false }
  },

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

### The optional `sandbox` block

`sandbox` is an optional top-level object and is part of the signed payload. Absent
or `"enabled": false` ⇒ the app runs **unsandboxed** (legacy behavior, full user
privileges). Set `"enabled": true` to opt in.

**`filesystem[]`** — each entry is `{ "path": <token>, "access": "ro" | "rw" }`. The
`path` is an **abstract token**, never a raw host path, so the same descriptor
resolves correctly across machines and users:

| Token | Resolves to |
|-------|-------------|
| `app` | the install directory — **always implicitly granted** (read-only + exec); listing it is harmless |
| `home` | the user's home directory |
| `xdg-documents` | the user's Documents directory |
| `xdg-download` | the user's Downloads directory |
| `xdg-config` | the user's config directory |
| `tmp` | the temp directory |
| `host` | **no filesystem restriction** — an escape hatch, flagged loudly at install. Use it only as a last resort |

Any token outside this set is rejected at validation (fail closed — Hina never
silently grants an unknown path).

**`capabilities`** — booleans: `network`, `audio`, `microphone`, `screen`,
`input`, `devices`. **`network` is enforced on Linux 6.7+** (Landlock ABI ≥ 4): a
sandboxed app that does not declare `network: true` has all TCP bind/connect
denied. On older kernels and other OSes `network` is declared-only. The remaining
capabilities are **not enforced** — surfaced to the user (by `hina perms`) as
*"declared — not enforced"* so the display never implies isolation Hina does not
provide.

**What is enforced where** — the **filesystem** scope and the **`network`**
capability are enforced on **Linux** (via Landlock; network needs kernel 6.7+) and
on **macOS** (via `sandbox-exec`). On Windows the declared scope is shown at
install time with a warning that it is *not* applied. See the [Sandboxing](#sandboxing)
section for the full model.

### Validation rules

- `name` matches `^[a-z][a-z0-9-]{1,63}$`
- `version` and `minHinaVersion` are SemVer
- `baseUrl` is HTTPS (HTTP requires user-side `--allow-insecure`)
- `publicKey` is a valid base64 32-byte Ed25519 key
- Hook paths (`target`, `exec`, `icon`, `files`) are relative — no `..`, no absolute paths
- Every hook `entryId` must match an `entries[].id`
- `entries[].id` matches `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` (it flows into the `.desktop` Exec via the `hina run` launch command)
- `exec` must define at least one platform
- `sandbox.filesystem[].path` must be a known token (`app`, `home`, `xdg-documents`, `xdg-download`, `xdg-config`, `tmp`, `host`); unknown tokens are rejected
- `sandbox.filesystem[].access` is `ro` or `rw`

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

## Sandboxing

A publisher can opt an app into a Flatpak-style filesystem sandbox via the
[`sandbox` block](#the-optional-sandbox-block). The enforcement model in v1 is
deliberately narrow:

- **Filesystem scope is the only enforced category**, and **only on Linux** (via
  Landlock — kernel ≥ 5.13, unprivileged, no root or bubblewrap). `hina run`
  installs the Landlock ruleset, then `execv`s the app; the restrictions are
  inherited across the exec, so the app's process is the restricted one.
- **macOS** enforces via `sandbox-exec` (Seatbelt): `hina run` generates a profile
  from the declared scope and launches the app under `sandbox-exec -f <profile>`,
  so its shortcut routes through `hina run` just like Linux.
- **Windows does not enforce the sandbox yet** (that backend is deferred).
  Installing a sandboxed app there warns that it runs with full user privileges,
  and its shortcut launches the binary directly — it does **not** route through
  `hina run` (which would gain nothing).
- **Old kernel / no Landlock / no sandbox-exec** → a no-op plus a one-time warning. A missing or
  too-old sandbox backend never blocks a launch.
- **`network` is enforced on Linux 6.7+.** A sandboxed app that doesn't declare
  `network: true` has all TCP bind/connect denied (Landlock ABI ≥ 4); on older
  kernels and other OSes it falls back to declared-only. `audio`, `microphone`,
  `screen`, `input`, and `devices` remain declared-only — surfaced to the user as
  *"declared — not enforced"*; nothing restricts them yet.
- **No portals.** There are no dynamic file-picker grants. Scope is the static set
  of declared paths plus any paths the user grants manually.

The app dir is always granted read-only + exec, even when the descriptor doesn't
list `app`.

### Inspecting and granting permissions

`hina perms` shows what each app declared and what the user has granted:

```shell
hina perms               # table of all installed apps
hina perms <app>         # full detail for one app
hina perms <app> --grant <path>[:ro|:rw]   # add a runtime filesystem grant (defaults to ro)
hina perms <app> --revoke <path>           # remove a grant
```

User grants are stored as absolute resolved paths in the registry's `userGrants`
and are **kept across updates** — they are the only way to widen a sandboxed app's
filesystem reach beyond its declared scope.

---

## Update Flow

`hina update [name]` re-fetches the descriptor for one or all installed apps:

1. Verify against the pinned key.
2. If the new descriptor version matches the installed version, exit as already
   up-to-date (use `--force` to re-run the patcher anyway).
3. Diff hook identity and entry id between the old registry record and the new
   descriptor.
4. **Diff the sandbox permissions** of the old (cached) descriptor against the new
   one. A **broadening** change — a new filesystem path, `host` added, a `ro → rw`
   widening, a newly-declared capability, or the sandbox being removed/disabled —
   is **refused** before anything touches disk, unless the user re-runs with
   `--accept-new-permissions`. A **narrowing** change — a removed path, `rw → ro`,
   a capability turned off, or an app becoming newly/more-tightly sandboxed —
   applies automatically.
5. Call `PatchClient.PatchAsync` for a delta download (rsync rolling checksums
   reuse local chunks).
6. Remove obsolete hooks and entries; add new ones.
7. Update the registry.
8. Refresh the cached descriptor (atomic write) so `run`/`perms` reflect the new
   declared scope. The user's `userGrants` persist across the update untouched.

On any post-patch failure: `PatchClient.RollbackAsync` restores files from backups,
and the registry is reverted to the pre-update snapshot.

---

## Building a Release as a Publisher

Existing patcher tooling is reused — see
[`docs/Builder-Guide.md`](Builder-Guide.md) for the chunk store + manifest workflow.

End-to-end:

1. Generate an Ed25519 key pair with:
   ```
   dotnet run --project Hina.Builder -- keygen --out . --name myapp
   ```
   That writes `myapp.key.b64` (keep secret) and `myapp.pub.b64` (paste into the
   descriptor as `publicKey`).
2. Build the manifest with the same private key:
   ```
   dotnet run --project Hina.Builder -- build \
     --input ./build --out ./patch \
     --base https://cdn.example.com/myapp/ --version 1.0.0 \
     --sign-key ./myapp.key.b64
   ```
3. Upload the resulting `manifest.json` and `chunks/` tree to your CDN at the URL
   that the descriptor's `baseUrl` points to.
4. Author `hina.app.json` with the same `publicKey` and sign it:
   ```
   hina dev sign-descriptor --in hina.app.json --key ./myapp.key.b64
   ```
   The command validates the descriptor, attaches an Ed25519
   `descriptorSignature`, and writes the result in place (or to `--out <path>`).
5. Host the descriptor at any URL you control. Tell users to run
   `hina install <descriptor-url>`.

---

## Recovery: detecting orphans

If you (or another tool) manually delete an installed app directory or move
Hina's data dir, the registry will still reference the missing app. `hina list`
flags the row with `[missing]` and points at:

```shell
hina verify            # report problems across all installed apps
hina verify --deep     # also re-fetch the manifest and hash-verify every file
hina repair            # the primary recovery command: prune orphans + dangling side-effects
```

`hina repair` is the equivalent of `hina verify --repair`.

The verifier checks each installed app's:

- Install directory presence
- **Local integrity**: the per-OS executable, every `entries[].exec`, and the
  cached descriptor are present. A missing exec or file means the install is
  incomplete (deleted by hand, botched patch) — the fix is `hina reinstall`, not
  repair. `verify --deep` goes further and hash-verifies every file against the
  manifest.
- Each recorded shell entry's evidence (symlink target, .lnk, .app bundle, .desktop)
- Each recorded hook's evidence (`addToPath` symlink targets, `installFont` files, MIME/URL/autostart artifacts)

`hina repair` removes orphan registry rows (whose install dir is gone) along with
their symlinks/`.desktop` entries and dangling evidence, and sweeps up
**true-orphan artifacts** left after a manual `registry.json` deletion (on Linux,
stray `hina-*.desktop` entries and installed fonts). Repair is fail-soft: it logs
and continues on individual failures. Safe to run from cron.

**Manual-deletion matrix.** What to run after a hand edit Hina didn't make:

| You deleted… | `hina verify` reports | Fix |
|--------------|-----------------------|-----|
| the app's install directory | "app directory missing" | `hina repair`, or `hina uninstall <name>` (fail-soft on the missing dir) |
| `registry.json` | nothing per-app (the rows are gone) | `hina repair` runs the orphan-artifact scan and cleans the leftovers |
| files inside the install dir | missing file(s) | `hina reinstall <name>` |

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
