# Quick Start — pick what you want to do

Short, task-oriented recipes. Each one links to the full guide for details.

| I want to… | Jump to |
|------------|---------|
| Install / update / remove an app (end user) | [Use an app](#use-an-app) |
| Publish a **single-platform** app | [Publish one platform](#publish-one-platform) |
| Publish a **multi-OS / multi-arch** app | [Publish multiple platforms](#publish-multiple-platforms) |
| Host the files I built | [Host it](#host-it) |
| Generate / rotate signing keys | [Keys & signing](#keys--signing) |

---

## Use an app

You only need the descriptor URL from the publisher.

```sh
hina install https://example.com/hina.app.json   # installs the build for YOUR os+arch
hina list                                         # what's installed
hina update <name>                                # delta-update (only changed chunks)
hina run <name>                                   # launch (sandboxed apps go through Hina)
hina uninstall <name>                             # remove + clean side-effects
```

Hina downloads only the build matching your machine. → details: [CLI Guide](CLI-Guide.md),
[Package Manager Guide](PackageManager-Guide.md).

---

## Publish one platform

Your built app is in one folder (`./build`). The wizard does everything: detects the
executable, asks a few questions (with smart defaults — just press Enter), makes keys,
writes a signed `hina.app.json`, and builds the manifest + chunk store.

```sh
hina-builder init --input ./build
```

That's it — then [host it](#host-it). → details: [Builder Guide](Builder-Guide.md).

---

## Publish multiple platforms

Use this when each OS/arch is a **separate build folder** and you don't want a Windows user
downloading the macOS/Linux builds. Each platform becomes its own manifest; the client
downloads only its own.

**1. Name each build folder by its platform token** `<os>[-<arch>]`
(os = `windows|macos|linux`; arch = `x64|arm64|x86|arm`; **64-bit = `x64`**; omit arch for a
universal build of that OS):

```
game/
  common/         OS-independent files (game data, assets) shared by every variant
  windows-x64/    your Windows build
  macos-arm64/    Apple Silicon build
  macos-x64/      Intel build
  linux-x64/      your Linux build
```

> Coming from `game_windows_x86_64`? Rename to `windows-x64`. The folder name must be exactly
> the token, nothing else.

Files in `common/` are merged into **every** variant's manifest at their root-relative paths
(no copying into each folder needed); if a variant ships its own copy of the same path, the
variant's file wins.

**2. Run the wizard** — it detects the variant folders and builds each one:

```sh
cd game
hina-builder init --input .
```

It writes `hina.app.json` (with a `platforms` array) into `game/`, and the keys + patch store
into a folder **outside** `game/` (so the private key is never shipped).

**Prefer manual control?** Build each folder into the same `--out` (shared chunk store) and
write the descriptor yourself:

```sh
hina-builder keygen --out keys --name game
hina-builder build --input game/windows-x64 --common game/common --platform windows-x64 --out patch --base https://you.example/ --version 1.0.0 --sign-key keys/game.key.b64
hina-builder build --input game/macos-arm64 --common game/common --platform macos-arm64 --out patch --base https://you.example/ --version 1.0.0 --sign-key keys/game.key.b64
# …one per variant, same --out and same --common
hina dev sign-descriptor --in hina.app.json --key keys/game.key.b64
```

A client on macOS arm64 with no native build falls back to the `x64` variant (Rosetta) with a
warning; if no variant serves its OS it errors cleanly. → details:
[Builder Guide → Multi-platform packages](Builder-Guide.md).

---

## Host it

`hina-builder` produced a folder with the manifest(s) and a `chunks/` tree. Put it online so
it's reachable at the `baseUrl` you set, and host `hina.app.json` at any URL.

- **Any static host / CDN** (S3, GitHub Pages, Nginx) — just serve the files.
- **Self-host** with the bundled server:
  ```sh
  hina-host --setup        # one-time wizard, writes hina.host.json
  hina-host                # serves manifests + chunks
  ```

Then share the descriptor URL; users run `hina install <url>`. → details:
[Host Guide](Host-Guide.md).

---

## Keys & signing

One Ed25519 key pair signs both the manifest and the descriptor.

```sh
hina-builder keygen --out keys --name myapp        # myapp.key.b64 (secret!), myapp.pub.b64
```

- Put `myapp.pub.b64`'s content into the descriptor's `publicKey`.
- **Never commit the private key**; keep it in a secrets manager for CI.
- Rotate by issuing a new pair and rebuilding; users accept the new key with
  `hina reinstall <name> --rotate-key`.

→ details: [Builder Guide → keygen](Builder-Guide.md), [Security](Security.md).
