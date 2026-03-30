# Hina Wiki

Hina is an open-source, rsync-like patcher for game clients and desktop applications, built on .NET 10. It delivers fast, bandwidth-efficient updates by computing rolling checksums against local files and transferring only the chunks that differ.

---

## Table of Contents

- [Architecture](Architecture.md) -- Project structure, core library internals, data flow, class diagrams, and design decisions.
- [Configuration](Configuration.md) -- Complete reference for all configuration properties, file resolution, host config, and environment-specific examples.
- [Builder Guide](Builder-Guide.md) -- Key generation, build commands, fixed vs CDC chunking, build output structure, manifest format, and CI/CD integration.
- [CLI Guide](CLI-Guide.md) -- All CLI commands, flags, exit codes, verbose mode, debugging, and common workflows.
- [Host Guide](Host-Guide.md) -- Hina.Host configuration, deployment options, Nginx/Docker examples, CDN integration, CORS, and performance tuning.
- [Security](Security.md) -- Ed25519 manifest signing, chunk integrity, threat model, key management, and best practices.
- [Integration Guide](Integration-Guide.md) -- Embedding Hina.Core in your application, IPatchClient API, logging, error handling, and UI integration examples.
- [Troubleshooting](Troubleshooting.md) -- Common errors with causes and solutions, debugging with verbose mode, and bug reporting.
- [Changelog](Changelog.md) -- Version history and feature documentation.

---

## Quick Links

- [README](../README.md) -- Project overview, quick start, and usage examples.
- [License](../LICENSE) -- Apache License 2.0.

---

## Key Features

- rsync-like delta patching with rolling checksum matching
- Content-defined chunking (CDC) for superior deduplication
- Ed25519 manifest signing and verification
- Brotli-compressed chunk storage
- Retry with exponential backoff and jitter
- Backup and rollback with journaled patch sessions
- Concurrent chunk downloads
- Structured logging via Microsoft.Extensions.Logging
- Static hosting via ASP.NET Core or any CDN
