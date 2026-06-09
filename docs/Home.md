# Hina Wiki

Hina is an open-source cross-platform package manager and rsync-like patcher for desktop applications and game clients, built on .NET 10. End users install apps with `hina install <url>` and updates are delta-fetched by computing rolling checksums against local files so only the chunks that differ are transferred.

---

## Table of Contents

- [**Quick Start**](Quick-Start.md) -- Task-oriented recipes: use an app, publish one or multiple platforms, host it, manage keys. Start here.
- [Architecture](Architecture.md) -- Project structure, core library internals, package-manager layer, data flow, class diagrams, and design decisions.
- [Diagrams](Diagrams.md) -- Rendered Mermaid diagrams: architecture graph, CLI routing, class diagrams, every pipeline, and the sandbox/container isolation flow per OS.
- [Install Script](Install-Script.md) -- The `curl | bash` / `iwr | iex` one-liner: capabilities, env vars, atomic install + rollback, SHA-256 verification, existing-install menu (reinstall / clean / integrity / uninstall), and a flow diagram.
- [Package Manager Guide](PackageManager-Guide.md) -- End-user CLI (`hina install/update/uninstall`), descriptor schema, whitelisted hooks, TOFU + signature chain, per-OS install paths.
- [Configuration](Configuration.md) -- Complete reference for all configuration properties, file resolution, host config, and environment-specific examples.
- [Builder Guide](Builder-Guide.md) -- Key generation, build commands, fixed vs CDC chunking, build output structure, manifest format, descriptor signing (`hina dev sign-descriptor`), and CI/CD integration.
- [CLI Guide](CLI-Guide.md) -- All CLI commands, flags, exit codes, verbose mode, debugging, and common workflows.
- [Host Guide](Host-Guide.md) -- Hina.Host configuration, deployment options, Nginx/Docker examples, CDN integration, CORS, and performance tuning.
- [Security](Security.md) -- Ed25519 manifest + descriptor signing, TOFU + key pinning, chunk integrity, threat model, key management, best practices.
- [Integration Guide](Integration-Guide.md) -- Embedding Hina.Core in your application, IPatchClient API, logging, error handling, and UI integration examples.
- [Troubleshooting](Troubleshooting.md) -- Common errors with causes and solutions, debugging with verbose mode, and bug reporting.
- [Changelog](Changelog.md) -- Version history and feature documentation.

---

## Quick Links

- [README](../README.md) -- Project overview, quick start, and usage examples.
- [License](../LICENSE) -- Apache License 2.0.

---

## Key Features

- Cross-platform package manager: `hina install <url>` / `update` / `uninstall` / `list` on Windows, Linux, macOS
- Whitelisted declarative hooks (PATH symlink, MIME, URL scheme, font, autostart), all user-scope (no admin)
- Ed25519 signed descriptors with TOFU on first install and pinned-key verification on update
- Optional [filesystem sandboxing](Security.md) for apps that opt in — enforced on Linux (Landlock) and macOS (Seatbelt) via `hina run`, declared-but-not-enforced on Windows
- [`hina perms`](Security.md) to inspect declared scope/capabilities and grant or revoke filesystem paths per app
- Update [permission consent](Security.md): an update that broadens an app's sandbox is refused until `--accept-new-permissions`
- Integrity tooling: `hina verify` checks installed files against the descriptor, `verify --deep` hash-verifies every file against the manifest (see [Troubleshooting](Troubleshooting.md))
- `hina repair` (`verify --repair`) cleans orphan registry rows and dangling shortcuts/hooks after a manual deletion (see [Troubleshooting](Troubleshooting.md))
- rsync-like delta patching with rolling checksum matching reuses local chunks
- Content-defined chunking (CDC) for superior deduplication
- Brotli-compressed chunk storage
- Retry with exponential backoff and jitter
- Backup and rollback with journaled patch sessions
- Concurrent chunk downloads
- Structured logging via Microsoft.Extensions.Logging
- Static hosting via ASP.NET Core or any CDN
- NativeAOT single-file binary (~7.5 MB), no .NET runtime required on user machines
