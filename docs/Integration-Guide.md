# Integration Guide

This guide covers embedding Hina.Core directly in your application as a library, bypassing the CLI entirely. This is the recommended approach for game launchers, auto-updaters, and any application that needs programmatic control over the patching process.

---

## Adding the Hina.Core Reference

### Project Reference (same solution)

```xml
<ItemGroup>
  <ProjectReference Include="..\Hina.Core\Hina.Core.csproj" />
</ItemGroup>
```

### NuGet Package (when published)

```xml
<ItemGroup>
  <PackageReference Include="Hina.Core" Version="1.0.0" />
</ItemGroup>
```

### Dependencies

Hina.Core brings two transitive dependencies:

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging interface. No runtime logger unless you provide one. |
| `NSec.Cryptography` | Ed25519 signing and verification (libsodium-based). |

---

## Basic Usage

### Minimal Example

```csharp
using Hina.Core.Configuration;
using Hina.Core.Patching;

var config = new PatcherConfig
{
    BaseUrl = new Uri("https://patch.example.com/"),
    TrustedPublicKey = "BASE64_PUBLIC_KEY"
};

var client = new PatchClient(config);
PatchResult result = await client.PatchAsync(@"C:\Games\MyGame", CancellationToken.None);

if (result.Success)
{
    Console.WriteLine($"Patched {result.AppliedFiles.Count} files.");
}
else
{
    Console.WriteLine($"Patch failed: {result.Message}");
}
```

### Loading Config from File

```csharp
using Hina.Core.Configuration;
using Hina.Core.Patching;

PatcherConfig config = PatcherConfigLoader.Load("hina.config.json");
var client = new PatchClient(config);
```

---

## IPatchClient Interface

`PatchClient` implements `IPatchClient`, which defines the full public API:

```csharp
public interface IPatchClient
{
    PatcherConfig Config { get; }
    Task<CheckResult> CheckAsync(string rootDir, CancellationToken ct);
    Task<PatchResult> PatchAsync(string rootDir, CancellationToken ct);
    Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct);
    Task RollbackAsync(string rootDir, CancellationToken ct);
}
```

### CheckAsync

Checks whether an update is available without downloading or modifying any files.

```csharp
CheckResult result = await client.CheckAsync(rootDir, cancellationToken);
```

| Property | Type | Description |
|----------|------|-------------|
| `IsUpdateAvailable` | `bool` | `true` if any file is missing or has a hash mismatch. |
| `Message` | `string` | Human-readable status message. |

**Return semantics:**
- Downloads the manifest and verifies its signature (if `TrustedPublicKey` is set).
- Compares SHA256 hashes of local files against manifest entries.
- Returns on the first mismatch found (does not enumerate all differences).

### PatchAsync

Downloads and applies all pending updates.

```csharp
PatchResult result = await client.PatchAsync(rootDir, cancellationToken);
```

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | `true` if all files were patched successfully. |
| `AppliedFiles` | `List<string>` | Relative paths of files that were updated. |
| `Message` | `string` | Error message on failure, empty on success. |

**Behavior:**
- Automatically rolls back any incomplete previous patch (detected via journal).
- Skips files whose local hash already matches the manifest.
- Uses rsync-like matching to reuse local data and minimize downloads.
- Writes to a temp file first, then swaps atomically.
- Creates backups (when `Backup` is `true`) for rollback support.
- Verifies each file's hash after reconstruction (when `Verify` is `true`).
- On failure, rolls back all changes made in this session.

### VerifyAsync

Verifies the integrity of all local files against the manifest.

```csharp
VerifyResult result = await client.VerifyAsync(rootDir, cancellationToken);
```

| Property | Type | Description |
|----------|------|-------------|
| `Success` | `bool` | `true` if all files match their expected hashes. |
| `BrokenFiles` | `List<string>` | Relative paths of files that are missing or corrupted. |
| `Message` | `string` | `"OK"` on success, `"Broken files detected."` on failure. |

**Behavior:**
- Downloads the manifest and verifies its signature.
- Checks every file in the manifest (does not stop on first mismatch).
- Reports all broken files in the `BrokenFiles` list.

### RollbackAsync

Restores files from backups created during the last patch session.

```csharp
await client.RollbackAsync(rootDir, cancellationToken);
```

**Behavior:**
- Reads the patch journal (`.hina/journal.json`) from the root directory.
- Restores each backed-up file from its `.hina.bak` copy.
- Deletes the journal after successful rollback.
- No-op if no journal exists.

---

## Logging Integration

`PatchClient` accepts an `ILogger<PatchClient>` for structured logging. Pass your application's logger to get full visibility into the patching process.

### With Microsoft.Extensions.Logging

```csharp
using Microsoft.Extensions.Logging;

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

ILogger<PatchClient> logger = loggerFactory.CreateLogger<PatchClient>();
var client = new PatchClient(config, logger);
```

### With Serilog

```csharp
using Serilog;
using Serilog.Extensions.Logging;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File("hina.log")
    .CreateLogger();

using var loggerFactory = new SerilogLoggerFactory();
ILogger<PatchClient> logger = loggerFactory.CreateLogger<PatchClient>();
var client = new PatchClient(config, logger);
```

### Without Logging

If you pass `null` or omit the logger parameter, `PatchClient` uses `NullLogger<PatchClient>.Instance` internally. No logging output is produced.

```csharp
var client = new PatchClient(config); // No logging
```

### Log Levels Used

| Level | What Is Logged |
|-------|----------------|
| `Debug` | Config resolution, chunk-level rsync matching, skipped files, per-file verification |
| `Information` | Operation start/complete, file patching, rollback, summary |
| `Warning` | Transient retry attempts, incomplete journal detection, missing files during verify |
| `Error` | Patch failures, unrecoverable errors |

---

## Error Handling

### Exception Types

| Exception | When Thrown | Meaning |
|-----------|------------|---------|
| `InvalidDataException` | Manifest signature invalid, file hash mismatch after patch | Integrity check failure. Rollback is triggered automatically. |
| `HttpRequestException` | All retries exhausted for a network request | Server unreachable, DNS failure, or persistent HTTP errors. |
| `OperationCanceledException` | `CancellationToken` was triggered | The caller cancelled the operation. |
| `IOException` | File system errors during read/write | Disk full, permission denied, locked files. |

### Handling PatchResult Failures

```csharp
PatchResult result = await client.PatchAsync(rootDir, ct);

if (!result.Success)
{
    // Rollback has already been attempted automatically.
    // result.Message contains the error description.
    logger.LogError("Patch failed: {Message}", result.Message);

    // Optionally run cleanup to remove leftover temp files
    PatchCleanup.Cleanup(rootDir);
}
```

### Handling CheckResult

```csharp
try
{
    CheckResult check = await client.CheckAsync(rootDir, ct);
    if (check.IsUpdateAvailable)
    {
        // Prompt user or auto-patch
    }
}
catch (InvalidDataException)
{
    // Manifest signature verification failed
}
catch (HttpRequestException ex)
{
    // Network error -- server may be down
}
```

---

## Custom Chunking (IChunker)

The `IChunker` interface allows implementing custom chunking strategies:

```csharp
public interface IChunker
{
    Task<List<ManifestChunk>> ChunkAsync(Stream stream, CancellationToken ct);
}
```

Built-in implementations:

| Class | Strategy | Description |
|-------|----------|-------------|
| `RsyncChunker` | Fixed-size | Splits files into fixed-size blocks. Simple and predictable. |
| `ContentDefinedChunker` | CDC (Gear hash) | Variable-size chunks with content-defined boundaries. Better deduplication for insertions/deletions. |

Custom chunkers are used primarily with `ManifestBuilder` and `ChunkStoreWriter` during the build process. The client does not need a chunker reference -- it reconstructs files based on manifest metadata.

```csharp
using Hina.Core.Hashing;
using Hina.Core.Rsync;

IHasher hasher = new Sha256Hasher();

// Fixed-size chunker
IChunker fixed = new RsyncChunker(chunkSize: 65536, hasher);

// Content-defined chunker
IChunker cdc = new ContentDefinedChunker(hasher, minSize: 2048, maxSize: 65536, avgSize: 8192);
```

---

## Custom Hashing (IHasher)

The `IHasher` interface allows replacing the hash algorithm:

```csharp
public interface IHasher
{
    string AlgorithmId { get; }
    Task<string> ComputeHashAsync(Stream stream, CancellationToken ct);
}
```

The built-in `Sha256Hasher` returns hashes in the format `sha256:<hex>`. A custom implementation must follow the same `<algorithm>:<hex>` convention to maintain compatibility with the manifest format and chunk URL generation.

```csharp
using Hina.Core.Hashing;

IHasher hasher = new Sha256Hasher();
// hasher.AlgorithmId == "sha256"
// Returns: "sha256:a3f7e2b1..."
```

---

## Thread Safety and Concurrency

### PatchClient

`PatchClient` is **not safe for concurrent use**. Do not call multiple methods simultaneously on the same instance. Each operation (check, patch, verify, rollback) should complete before starting the next.

```csharp
// CORRECT: sequential operations
CheckResult check = await client.CheckAsync(rootDir, ct);
if (check.IsUpdateAvailable)
{
    PatchResult patch = await client.PatchAsync(rootDir, ct);
}

// INCORRECT: concurrent operations on the same instance
// var checkTask = client.CheckAsync(rootDir, ct);
// var verifyTask = client.VerifyAsync(rootDir, ct);
// await Task.WhenAll(checkTask, verifyTask);
```

### CancellationToken

All async methods accept a `CancellationToken`. Cancellation is cooperative -- the operation checks the token between file operations and chunk downloads. Cancelling during a patch triggers rollback of changes made so far.

### Multiple Instances

You can create multiple `PatchClient` instances for different game directories or configurations. Each instance manages its own `HttpClient` and operates independently.

---

## Unity / Game Engine Integration

### Unity Integration Tips

1. **Threading**: Unity's main thread cannot be blocked. Run patching on a background thread or use Unity's `Task` support.

```csharp
// In a MonoBehaviour
public async void StartPatch()
{
    var config = new PatcherConfig
    {
        BaseUrl = new Uri("https://patch.example.com/")
    };

    var client = new PatchClient(config);

    // Run on background thread
    PatchResult result = await Task.Run(() =>
        client.PatchAsync(Application.persistentDataPath, CancellationToken.None));

    // Back on main thread
    if (result.Success)
    {
        Debug.Log($"Patched {result.AppliedFiles.Count} files");
    }
}
```

2. **Target framework**: Hina.Core targets `net10.0`. For Unity, you may need to build against `netstandard2.1` or the specific Unity .NET profile. Consider referencing the compiled DLL directly if the project reference does not work with Unity's build system.

3. **File paths**: Use `Application.persistentDataPath` or `Application.dataPath` as the root directory.

4. **Cancellation**: Wire up `Application.quitting` to a `CancellationTokenSource` to gracefully cancel in-progress patches when the application closes.

---

## WPF / WinForms Integration

### WPF Example with Progress UI

```csharp
public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        PatchButton.IsEnabled = false;
        StatusText.Text = "Checking for updates...";

        var config = new PatcherConfig
        {
            BaseUrl = new Uri("https://patch.example.com/"),
            TrustedPublicKey = "BASE64_PUBLIC_KEY"
        };

        // Wire up logging to the UI
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<PatchClient>();
        var client = new PatchClient(config, logger);
        _cts = new CancellationTokenSource();

        try
        {
            CheckResult check = await Task.Run(() =>
                client.CheckAsync(@"C:\Games\MyGame", _cts.Token));

            if (!check.IsUpdateAvailable)
            {
                StatusText.Text = "Already up to date.";
                return;
            }

            StatusText.Text = "Downloading updates...";

            PatchResult result = await Task.Run(() =>
                client.PatchAsync(@"C:\Games\MyGame", _cts.Token));

            StatusText.Text = result.Success
                ? $"Update complete. {result.AppliedFiles.Count} files updated."
                : $"Update failed: {result.Message}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Update cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            PatchButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
```

### WinForms Example

```csharp
public partial class UpdateForm : Form
{
    private CancellationTokenSource? _cts;

    private async void BtnUpdate_Click(object sender, EventArgs e)
    {
        btnUpdate.Enabled = false;
        btnCancel.Enabled = true;
        lblStatus.Text = "Checking...";

        var config = new PatcherConfig
        {
            BaseUrl = new Uri("https://patch.example.com/"),
            TrustedPublicKey = "BASE64_PUBLIC_KEY"
        };

        var client = new PatchClient(config);
        _cts = new CancellationTokenSource();

        try
        {
            PatchResult result = await Task.Run(() =>
                client.PatchAsync(@"C:\Games\MyGame", _cts.Token));

            lblStatus.Text = result.Success
                ? $"Done. {result.AppliedFiles.Count} files updated."
                : $"Failed: {result.Message}";
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            btnUpdate.Enabled = true;
            btnCancel.Enabled = false;
            _cts?.Dispose();
        }
    }

    private void BtnCancel_Click(object sender, EventArgs e)
    {
        _cts?.Cancel();
    }
}
```

---

## Cleanup

After a successful patch (or to recover from a failed one), use `PatchCleanup` to remove leftover temporary files:

```csharp
using Hina.Core.Patching;

PatchCleanup.Cleanup(@"C:\Games\MyGame");
```

This removes:
- `*.hina.tmp` -- incomplete download/reconstruction temp files
- `*.hina.bak` -- backup files from previous patches
- `.hina/journal.json` -- the patch session journal
