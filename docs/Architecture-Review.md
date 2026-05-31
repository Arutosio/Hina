# Architecture Review & Roadmap

> Revisione architetturale di Hina con findings concreti e roadmap di refactoring.
> Complementa [Architecture.md](Architecture.md), che è descrittivo: questo documento è
> **valutativo + prescrittivo** (cosa migliorare, perché, in che ordine).
>
> Data: 2026-05-28 · Scope: intero solution (`Hina.sln`, ~14k LOC, 7 progetti, .NET 10 NativeAOT)

---

## 0. Stato implementazione (aggiornato 2026-05-29, branch `refactor/wave1-architecture`)

Eseguito in autonomia. Suite: **259 test verdi** (Core 113, PackageManager 133, CLI 13).

| Item | Stato | Commit |
|---|---|---|
| #1 composition root CLI (`CommandContext`+`CommandRouter`) | ✅ Fatto | `refactor(cli)` |
| #7 lock condiviso in lettura (list/info/which) | ✅ Fatto | `refactor(cli)` |
| #9 fail-soft logging in UpdateService/UninstallService | ✅ Fatto | `fix(update)`, `fix(uninstall)` |
| #12 progetto test CLI (13 smoke test) | ✅ Fatto | `refactor(cli)` |
| #4 helper platform condivisi (`PlatformText`) | ✅ Fatto | `refactor(platform)` |
| #5 split `UpdateService` → `UpdateDiff` (+6 test) | ✅ Fatto | `refactor(update)` |
| #8 gate schema-version registry (+2 test) | ✅ Fatto | `feat(registry)` |
| #13/#14 dedup build/installer | ⏸️ **Deferred** — richiede CI/release per verifica; non toccato senza poterlo eseguire | — |
| #2/#3/#6/#10/#11 (Wave 3 residuo) | ⏳ Aperti | — |

**Bug reali trovati e corretti** (bug-hunt con 3 reviewer paralleli, ognuno fixato con test):
- **CRITICAL** `HttpChunkClient`: chunk content-addressed mai verificato per-hash → con `Verify=false` corruzione accettata silenziosamente. Ora verifica SHA per-chunk. `fix(core)`
- **HIGH** `UpdateService` rollback: ri-creava le entry rimosse dal descriptor *nuovo* (mai matcha) → side-effect persi. Ora ripristina da descriptor *cached* (entry + hook). `fix(update)`
- **HIGH** `UninstallService`: `File.Delete` su directory-symlink fallisce su Windows (leak del link). Ora `Directory.Delete(recursive:false)`. `fix(uninstall)`
- **HIGH** `PatchClient.CopyChunk`: `Stream.Read` singolo → short-read corruttivo. Ora `ReadExactly`. `fix(core)`
- **MEDIUM** Windows `IsEvidenceDangling` font: controllava solo il primo file di N. Ora tutti. `fix(windows)`

**Bug-hunt round 2** (superficie contenuti non fidati / MITM):
- **CRITICAL** `PathUtils.ToOsPath`: nessun containment → un manifest ostile/non firmato con `path` `../../...` o assoluto faceva scrivere/leggere a `PatchClient` fuori dalla dir di install. Ora rifiuta i path che escono dalla root. `security(core)`
- **MEDIUM** `BrotliCodec.Decompress`: illimitato → decompression bomb (OOM) da chunk store ostile. Ora cap a `maxBytes` (il chunk download passa la size esatta dal manifest). `security(core)`
- **MEDIUM** `DescriptorSigner.Verify`: ignorava `algorithm` → confusion/downgrade. Ora pinnato a `ed25519`. `security(descriptor)`
- **MEDIUM** `Channel` non validato → entrava nell'URL del manifest (`../`/controlli). Ora regex token-safe. `security(descriptor)`
- **LOW** `PatchClient`: `chunk.Size` non confrontato col contenuto decompresso → validato prima dell'uso. `security(core)`

**Non confermati come bug** (analizzati, scartati): `UpdateAllAsync` "self-deadlock" (il lock O2 è a finestre brevi, non si serializza in pratica); `RetryPolicy` DNS-NXDOMAIN retried (latenza, non correttezza).

**Chiusi nel giro perf/hardening/cleanup (2026-05-31, branch `improve/perf-hardening-cleanup`):**
- **PERF** `PatchClient.PatchAsync`: chunk mancanti scaricati in serie → ora prefetch parallelo a finestra scorrevole, cap `Config.Concurrency`, scrittura ancora in ordine manifest. `perf(core)`
- **TOFU fail-open → fail-closed**: `InstallOptions.AssumeTrustOnFirstUse` (default false). Senza prompt né opt-in, il primo install rifiuta invece di auto-fidare. CLI invariata. `security(install)`
- **HTTP descriptor fetch + redirect**: `DescriptorFetcher` rifiuta il downgrade https→http sul redirect (l'URL finale dopo i redirect è confrontato con lo scheme iniziale). `security(descriptor)`
- **#9 fail-soft logging (residuo platform)**: i `catch {}` muti di unregister/cleanup in Windows/macOS ora loggano a Debug; `ILogger` opzionale (NullLogger default) instradato via factory. `refactor(platform)`
- **#10 `RegistryStore.LoadAsync`** aggiunto e usato dai comandi read-only. `#C3` `DevCommand` ora async (`await`, niente `.Result`/`.Wait()`). `refactor(cli,registry)`

**Aperti / scelte per l'utente** (da decidere):
- `#6 split PatchClient`, `#13/#14 dedup build` — vedi roadmap. `#13/#14` resta deferred: richiede un release run per verifica.

---

## 1. Sintesi

L'architettura è **sana e ben stratificata**. Il grafo delle dipendenze è lineare e privo di
cicli, le dipendenze esterne sono minime, e le scelte non convenzionali (niente DI container,
IO diretto su filesystem, composizione manuale) sono **deliberate** e giustificate dal vincolo
NativeAOT (no reflection, no dynamic dispatch).

Non serve una riscrittura. I miglioramenti proposti sono **mirati**: ridurre duplicazione,
spezzare un paio di metodi troppo grandi, chiudere alcune lacune di robustezza e coprire con
test il layer CLI oggi scoperto. **Nessuna nuova dipendenza NuGet** è proposta.

---

## 2. Mappa architettura

### Grafo dipendenze (lineare, zero cicli)

```
Hina.Core ─────────────────────────────────┐  (foundation; nessun project ref)
   ▲                                        │  NuGet: Logging.Abstractions, NSec.Cryptography
   │                                        │
   ├── Hina.PackageManager ── Core          │  (+ Microsoft.Win32.Registry)
   │        ▲                                │
   │        └── Hina.CLI ── PackageManager + Core   (NativeAOT, 6 RID)
   │
   ├── Hina.Builder ── Core
   └── Hina.Host ──── Core                      (ASP.NET)

Test: Hina.Core.Tests → Core · Hina.PackageManager.Tests → PackageManager + Core
```

### Progetti

| Progetto | Tipo | LOC | .cs | Ruolo |
|---|---|---:|---:|---|
| Hina.Core | lib | ~1.6k | 34 | chunking, crypto, hashing, patching, rsync, manifest, net |
| Hina.PackageManager | lib | ~3.8k | 39 | install/update/uninstall, descriptor, registry, platform, hooks |
| Hina.CLI | exe (AOT) | ~1.1k | 33 | verb-tree, comandi, dispatch |
| Hina.Host | web | ~0.6k | 4 | static file server ASP.NET |
| Hina.Builder | exe | ~0.2k | 4 | generazione manifest |
| Hina.Core.Tests | test | ~2.6k | 21 | algoritmi (crypto/rsync/patch/manifest) |
| Hina.PackageManager.Tests | test | ~3.1k | 25 | install/uninstall/platform/transaction |

### Pattern da preservare (NON cambiare)

- **Evidence model** — ogni side-effect (file, symlink, registry key, .desktop) registra una
  stringa-evidenza; l'undo legge l'evidenza e la inverte. Disaccoppia *creazione* OS-specifica
  da *reversal* generico.
- **Atomic registry write** — tmp + `fsync` + `File.Move(overwrite)`; crash a metà write lascia
  il registry buono precedente.
- **Source-gen JSON contexts** — niente reflection, AOT-clean.
- **`IPlatformIntegration` + `PlatformIntegrationFactory`** — unica astrazione OS, impl private al factory.
- **Lock O2 a finestre brevi** in `UpdateService` — il lock copre solo read/write registry, il lavoro
  lento (fetch/patch) è lock-free → `update --all` parallelo non si serializza.
- **Composizione manuale con default nei costruttori** — niente DI container è una scelta AOT, da mantenere.

---

## 3. Findings

Legenda: **Effort** S/M/L · **Rischio** = rischio del refactoring proposto · 🔒 = scelta deliberata da preservare.

### A — Coupling / dipendenze

| # | Finding | File:linea | Perché conta | Fix proposto | Effort/Rischio |
|---|---|---|---|---|---|
| 1 | **Composizione manuale duplicata** in ~10 comandi CLI | `Hina.CLI/Commands/*.cs` (es. `ListCommand.cs:13-14`) | Cambio firma costruttore → toccare tutti i call-site; nessun punto unico | `CommandContext`/composition root unico che costruisce `InstallPaths` + `IPlatformIntegration` + servizi una volta. Resta manuale (no DI container 🔒) | M / basso |
| 2 | **Threading parametri network** lungo tutta la catena | `NetworkArgs` → `InstallOptions.Network` → service → `DescriptorFetcher`/`PatchClient` | Nuovo parametro network = modifica a ogni anello | Passare un unico `NetworkConfig` come blob già fatto (parzialmente esiste); evitare di esplodere i campi | S / basso |
| 3 | **IO filesystem diretto e non astratto** | service + `Platform/*` | `File.*`/`Directory.*` sparsi; unit test costretti a temp dir reali | 🔒 accettabile per AOT/semplicità. Introdurre `IFileSystem` **solo** se la superficie di test cresce. Documentare il confine | L / medio |
| 4 | **Duplicazione logica platform** | `Platform/{MacOS,Linux,Windows}/*Integration.cs` (318–400 LOC l'una) | safe-delete, sanitize-id, evidence tracking ripetuti 3× | Estrarre helper comuni (`PlatformFs.SafeDelete`, `Sanitize.Id`) in classe condivisa; la parte OS-specifica resta | M / basso |

### B — God-class / complessità

| # | Finding | File:linea | Perché conta | Fix proposto | Effort/Rischio |
|---|---|---|---|---|---|
| 5 | **`UpdateService.UpdateAsync` = 467 LOC in un metodo** | `Install/UpdateService.cs:44-353` | Fa fetch + validate + diff hook/entry + snapshot + patch + remove + add bracketed + rollback multi-livello + merge registry + recovery dump. Ramificazione alta, rollback non testabile in isolamento | Estrarre `UpdateDiff` (calcolo diff + `ResolveIdentity`, righe `128-161`+`427-452`) e `UpdateRollback` (rollback patch+hook+registry, righe `182-204`+`257-296`). `UpdateAsync` diventa orchestratore | L / medio |
| 6 | **`PatchClient` = 422 LOC** | `Hina.Core/Patching/PatchClient.cs` | manifest fetch + journal + chunk download + verify + rollback in una classe | Monitorare. Candidabile a split (download vs apply vs journal) se cresce. Non urgente | M / medio |

### C — Robustezza / correttezza

| # | Finding | File:linea | Perché conta | Fix proposto | Effort/Rischio |
|---|---|---|---|---|---|
| 7 | **Comandi read-only senza lock** | `Registry/RegistryStore.cs:12` (commento "Caller must hold a LockManager lock") vs `ListCommand.cs:14`, `InfoCommand`, `WhichCommand` | `.Load()` senza lock durante install/update concorrente → possibile lettura stato parziale | Acquisire un lock condiviso in lettura, **oppure** documentare esplicitamente che read+write concorrente non è supportato (l'atomic write mitiga ma non garantisce) | S / basso |
| 8 | **Registry senza schema migration** | `RegistryStore.cs:24-45` | `Load()` assume `schemaVersion=1`, nessun branch di versione; un futuro bump rende `registry.json` illeggibile silenziosamente | Gate di versione: leggere `schemaVersion`, su mismatch o migrare o fallire con messaggio chiaro ("registry scritto da Hina più recente") | M / basso |
| 9 | **Fail-soft incoerente** | `UpdateService.cs:193,211,215,263,267,277,283,286,321` | Molti `catch { /* fail-soft */ }` muti, altri loggano. Bug silenziosi; `RegistryVerifier` è l'unica rete | Policy uniforme: ogni fail-soft logga almeno `LogDebug`/`LogWarning` con contesto | S / basso |
| 10 | **`RegistryStore.Load()` sincrono** in codebase async | `RegistryStore.cs:24` | Incoerente con `SaveAsync`; blocca su IO | `LoadAsync` (micro, opzionale) | S / basso |
| 11 | **Identità hook legacy fragile (H2)** | `UpdateService.cs:427-452` | `ResolveIdentity` ricostruisce identità da evidence per righe pre-Phase-3 con euristica basename/hash; fragile se il formato evidence cambia | Documentare come debito a scadenza: rimuovibile dopo una migrazione registry (lega-si a #8) | S / basso |

### D — Test / build infra

| # | Finding | File:linea | Perché conta | Fix proposto | Effort/Rischio |
|---|---|---|---|---|---|
| 12 | **Zero test sul layer CLI** | `Hina.CLI/Program.cs`, `Args.cs` | Verb-routing e parsing flag non coperti; typo flag / ordine non intercettati | Test d'integrazione CLI: spawn `hina <verb>` su fixtures, assert exit code + output. Estrarre il routing in funzione testabile aiuta | M / basso |
| 13 | **Duplicazione CI ↔ script locali** | `.github/workflows/release.yml` vs `scripts/release.{sh,ps1}` | rename binario (`Hina.CLI`→`hina`), `friendly_rid()`, mapping RID duplicati → drift | Estrarre la logica condivisa in uno script unico richiamato sia da CI sia in locale | M / basso |
| 14 | **Template installer inline** | `scripts/release.{sh,ps1}` (heredoc `install.sh`/`install.bat`) | Modifiche all'installer = editare gli script di release | Estrarre in file template versionati (`packaging/install-template.{sh,bat}`) | S / basso |

---

## 4. Roadmap

Tre ondate, dalla più alta ROI/basso rischio alla più evolutiva.

### Wave 1 — Alto valore, basso rischio
- **#1** Composition root unico per i comandi CLI (`CommandContext`). Resta manuale (no DI container 🔒).
- **#7** Lock in lettura per comandi read-only **oppure** doc esplicito del contratto di concorrenza.
- **#9** Policy fail-soft: ogni `catch` muto logga con contesto.
- **#12** Suite smoke d'integrazione per il CLI (verb routing + parsing).

### Wave 2 — Refactor mirato
- **#5** Split `UpdateService` → `UpdateDiff` + `UpdateRollback`; `UpdateAsync` orchestratore. Aggiungere
  unit test sui percorsi di rollback ora isolabili.
- **#4** Helper comuni per le tre platform integration (safe-delete, sanitize-id).
- **#13 / #14** Dedup build: logica condivisa CI/locale + template installer estratti.

### Wave 3 — Evolutivo
- **#8** Schema-version gate del registry (sblocca #11).
- **#11** Ritiro del debito H2 dopo la migrazione registry.
- **#6** Valutare split di `PatchClient` se la classe cresce.
- **#3** `IFileSystem` **solo** se la superficie di test lo giustifica.
- **#2 / #10** Pulizie minori (network blob unico, `LoadAsync`).

---

## 5. Scelte deliberate da NON toccare

| Scelta | Motivo |
|---|---|
| Niente DI container (Microsoft.Extensions.DI/Autofac) | NativeAOT preferisce composizione statica; reflection penalizzata. Composition root resta manuale/leggero. |
| IO diretto su filesystem (no `IFileSystem` di default) | Semplicità + AOT; i test usano temp dir reali. Astrarre solo se necessario. |
| Source-gen JSON contexts | Obbligatorio per AOT; non sostituire con serializzazione reflection-based. |
| COM `[CoClass]` per `ShellLink` (Windows) | Pattern AOT-safe da .NET 8+; non tornare a lookup ProgID via reflection. |
| Fail-soft su uninstall/rollback | L'uninstall non deve mai fallire a metà; il fix è *loggare*, non rendere fatale. |

---

## 6. Note

- Dipendenze esterne già minime: nessuna nuova NuGet proposta — i miglioramenti sono strutturali interni.
- I numeri di riga si riferiscono allo stato del branch al 2026-05-28; verificare prima di agire.
- Vedi anche: [Architecture.md](Architecture.md) (struttura), [Troubleshooting.md](Troubleshooting.md)
  (`hina verify --repair` come rete di sicurezza del registry).
