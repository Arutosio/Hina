using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Hashing;
using Hina.Core.IO;
using Hina.Core.Manifest;
using Hina.Core.Net;
using Hina.Core.Rsync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.Core.Patching
{
    // Default patch client implementation using rsync-like matching.
    public sealed class PatchClient : IPatchClient
    {
        private readonly IHasher _hasher;
        private readonly HttpChunkClient _http;
        private readonly ILogger<PatchClient> _logger;

        public PatcherConfig Config { get; }

        public PatchClient(PatcherConfig config, ILogger<PatchClient>? logger = null)
        {
            Config = config;
            _hasher = new Sha256Hasher();
            _logger = logger ?? NullLogger<PatchClient>.Instance;
            var retryPolicy = new RetryPolicy(config.MaxRetries, config.RetryBaseDelayMs, config.MaxRetryDelayMs, _logger);
            _http = new HttpChunkClient(BuildHttpClient(config), retryPolicy);
        }

        // Build a fresh HttpClient with the connection knobs from PatcherConfig.
        // SocketsHttpHandler.PooledConnectionLifetime forces the underlying socket to
        // be torn down and rebuilt on a schedule — this is what makes the client cope
        // with IP changes / mobile-network hand-offs / DNS shifts mid-session without
        // the user noticing. ConnectTimeout caps the TCP handshake separately from the
        // overall request timeout so a stalled SYN fails fast and retry kicks in.
        private static HttpClient BuildHttpClient(PatcherConfig config)
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMilliseconds(config.PooledConnectionLifetimeMs),
                ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectTimeoutMs),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                EnableMultipleHttp2Connections = true
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMilliseconds(config.RequestTimeoutMs)
            };
        }

        public async Task<CheckResult> CheckAsync(string rootDir, CancellationToken ct)
        {
            _logger.LogInformation("Checking for updates in {RootDir}", rootDir);
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, Config.Platform, ct);
            VerifyManifestOrThrow(manifest);
            // Patch every file listed in the manifest.
            foreach (ManifestFile file in manifest.Files)
            {
                string localPath = PathUtils.ToOsPath(rootDir, file.Path);
                if (!File.Exists(localPath))
                {
                    _logger.LogInformation("Missing file detected: {FilePath}", file.Path);
                    return new CheckResult { IsUpdateAvailable = true, Message = "Missing files." };
                }

                using (FileStream fs = File.OpenRead(localPath))
                {
                    string hash = await _hasher.ComputeHashAsync(fs, ct);
                    if (!string.Equals(hash, file.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Out of date file detected: {FilePath}", file.Path);
                        return new CheckResult { IsUpdateAvailable = true, Message = "Out of date files." };
                    }
                }
            }

            _logger.LogInformation("All files are up to date");
            return new CheckResult { IsUpdateAvailable = false, Message = "Already up to date." };
        }

        public async Task<PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            _logger.LogInformation("Starting patch in {RootDir}", rootDir);
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, Config.Platform, ct);
            VerifyManifestOrThrow(manifest);
            PatchResult result = new PatchResult { Success = true };

            string journalPath = PatchJournal.GetJournalPath(rootDir);
            PatchJournal? existing = PatchJournal.Load(journalPath);
            if (existing != null)
            {
                if (existing.Status == PatchJournal.StatusCompleted)
                {
                    // Leftover from a prior successful patch — no rollback needed, just clean up.
                    _logger.LogDebug("Found completed journal from a prior patch; cleaning up leftovers.");
                    PatchCleanup.Cleanup(rootDir);
                }
                else
                {
                    // Status is InProgress (or unknown/corrupt value) — treat as interrupted, roll back.
                    _logger.LogWarning("Incomplete journal found, rolling back previous patch");
                    await RollbackAsync(rootDir, ct);
                }
            }

            PatchJournal journal = new PatchJournal();
            await journal.SaveAsync(journalPath);

            foreach (ManifestFile file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                string localPath = PathUtils.ToOsPath(rootDir, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? rootDir);

                // Skip work when the local file hash already matches.
                bool needsPatch = true;
                if (File.Exists(localPath))
                {
                    using (FileStream fs = File.OpenRead(localPath))
                    {
                        string hash = await _hasher.ComputeHashAsync(fs, ct);
                        needsPatch = !string.Equals(hash, file.FileHash, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!needsPatch)
                {
                    _logger.LogDebug("File already up to date, skipping {FilePath}", file.Path);
                    continue;
                }

                _logger.LogInformation("Patching file {FilePath}", file.Path);

                // Patch to a temp file first, then swap atomically.
                string tempPath = localPath + ".hina.tmp";
                string backupPath = localPath + ".hina.bak";

                try
                {
                    // Map manifest chunks to offsets in the local file (rsync-like match).
                    Dictionary<int, long> matches = new Dictionary<int, long>();
                    if (File.Exists(localPath))
                    {
                        matches = await RsyncMatchLocalAsync(localPath, file, ct);
                        _logger.LogDebug("Rsync matched {MatchCount}/{TotalChunks} chunks for {FilePath}", matches.Count, file.Chunks.Count, file.Path);
                    }

                    // Downloads run against a derived token so that on any failure we can cancel
                    // every in-flight chunk fetch and drain it — otherwise the tasks we started
                    // ahead of the write cursor would leak (open sockets, unobserved exceptions).
                    using CancellationTokenSource dl = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // Open the local file once for the whole rebuild instead of per matched chunk.
                    // The handle MUST be released before the File.Copy swap below: keeping it open
                    // (even read-only, FileShare.Read) makes the overwrite of localPath fail with a
                    // sharing violation — so srcFs's scope is this block, not the whole try.
                    // Whole-file verification hashes the bytes as they are written (both the
                    // disk-copied and downloaded paths feed it), replacing a full re-read of
                    // the temp file after the rebuild.
                    string? rebuiltHash = null;
                    using (FileStream? srcFs = matches.Count > 0 ? File.OpenRead(localPath) : null)
                    using (FileStream outFs = File.Create(tempPath))
                    using (IncrementalHash? verifyHash = Config.Verify
                        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                        : null)
                    {
                        // Rebuild the file in manifest order, reusing local data when possible.
                        // Missing chunks are downloaded in parallel: we keep up to Config.Concurrency
                        // downloads in flight ahead of the write cursor. The output is still written
                        // strictly in manifest order (so the file is byte-identical and the atomic
                        // swap is unchanged), but network latency overlaps instead of summing.
                        // The sliding window also caps memory to ~Concurrency chunks rather than
                        // buffering the whole file.
                        // Validate manifest chunk sizes up front so a hostile/corrupt manifest
                        // (Size <= 0 or absurdly large) fails the patch instead of silently writing
                        // a truncated file (download path) or hitting ArrayPool.Rent with a bad
                        // length (local copy path). (BUG-011)
                        foreach (ManifestChunk mc in file.Chunks)
                        {
                            if (mc.Size <= 0 || mc.Size > MaxChunkSize)
                            {
                                throw new InvalidDataException(
                                    $"Manifest chunk size {mc.Size} is out of the valid range (1–{MaxChunkSize} bytes).");
                            }
                        }

                        int window = Math.Max(1, Config.Concurrency);
                        Dictionary<int, Task<byte[]>> inflight = new Dictionary<int, Task<byte[]>>();
                        int nextToStart = 0;

                        // Start downloads for upcoming missing chunks until `window` are in flight
                        // (matched chunks are read from disk, so they don't consume a window slot).
                        void Pump()
                        {
                            while (inflight.Count < window && nextToStart < file.Chunks.Count)
                            {
                                int pos = nextToStart++;
                                ManifestChunk c = file.Chunks[pos];
                                if (!matches.ContainsKey(c.Index))
                                {
                                    _logger.LogDebug("Downloading chunk {ChunkIndex} for {FilePath}", c.Index, file.Path);
                                    inflight[pos] = _http.GetChunkAsync(Config.BaseUrl, c.Strong, dl.Token, c.Size);
                                }
                            }
                        }

                        try
                        {
                            for (int pos = 0; pos < file.Chunks.Count; pos++)
                            {
                                Pump();
                                ManifestChunk chunk = file.Chunks[pos];
                                if (matches.TryGetValue(chunk.Index, out long offset))
                                {
                                    // Reuse local data when a chunk matches.
                                    CopyChunk(srcFs!, offset, chunk.Size, outFs, verifyHash);
                                }
                                else
                                {
                                    byte[] data = await inflight[pos];
                                    inflight.Remove(pos);
                                    if (chunk.Size < 0 || chunk.Size > data.Length)
                                    {
                                        throw new InvalidDataException(
                                            $"Manifest chunk size {chunk.Size} is inconsistent with the {data.Length}-byte chunk content.");
                                    }
                                    await outFs.WriteAsync(data.AsMemory(0, chunk.Size), ct);
                                    verifyHash?.AppendData(data, 0, chunk.Size);
                                }
                            }
                        }
                        catch
                        {
                            // Cancel and drain the chunk downloads still in flight so they don't
                            // leak past this failed patch (the outer catch then rolls back).
                            dl.Cancel();
                            try { await Task.WhenAll(inflight.Values); }
                            catch { /* faults/cancellations observed; the original error is what propagates */ }
                            throw;
                        }

                        if (verifyHash != null)
                        {
                            rebuiltHash = "sha256:" + Hex.ToHexLower(verifyHash.GetHashAndReset());
                        }
                    }

                    if (Config.Verify)
                    {
                        if (!string.Equals(rebuiltHash, file.FileHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Hash mismatch after patch.");
                        }
                        _logger.LogDebug("Verification passed for {FilePath}", file.Path);
                    }

                    // Keep a backup of an existing file, or record a net-new file, so rollback can
                    // either restore or remove it. Without the net-new record, a later file's
                    // failure would roll back existing files but leave new ones — a mixed-version state.
                    bool existedBefore = File.Exists(localPath);
                    if (Config.Backup && existedBefore)
                    {
                        File.Copy(localPath, backupPath, overwrite: true);
                        journal.Entries.Add(new PatchJournalEntry
                        {
                            TargetPath = localPath,
                            BackupPath = backupPath
                        });
                        await journal.SaveAsync(journalPath);
                    }
                    else if (Config.Backup && !existedBefore)
                    {
                        journal.Entries.Add(new PatchJournalEntry { TargetPath = localPath, IsNew = true });
                        await journal.SaveAsync(journalPath);
                    }

                    // Atomic swap: rename the rebuilt temp onto the live path (BUG-032). File.Copy
                    // would rewrite localPath in place, so a crash/power-loss mid-copy would leave
                    // the already-in-production binary truncated. File.Move (MoveFileEx /
                    // rename(2), same volume since tempPath is a sibling) is all-or-nothing and
                    // consumes the temp, matching RegistryStore/AtomicFile.
                    File.Move(tempPath, localPath, overwrite: true);

                    result.AppliedFiles.Add(file.Path);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User cancellation: clean up like a failure (restore backups, drop temp),
                    // but propagate the cancellation instead of reporting Success=false —
                    // callers must see "cancelled", not "patch failed".
                    _logger.LogInformation("Patch cancelled for {FilePath}; rolling back.", file.Path);
                    await RollbackAsync(rootDir, CancellationToken.None);
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to patch file {FilePath}", file.Path);
                    result.Success = false;
                    result.Message = ex.Message;

                    // Best-effort rollback.
                    await RollbackAsync(rootDir, ct);

                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }

                    break;
                }
            }

            if (result.Success)
            {
                _logger.LogInformation("Patch completed successfully, {FileCount} files applied", result.AppliedFiles.Count);
                journal.Status = PatchJournal.StatusCompleted;
                await journal.SaveAsync(journalPath);
                // Best-effort cleanup: remove the now-obsolete journal and .hina.bak files.
                // A failure here must not turn a successful patch into an error — leftovers are
                // harmless and will be cleaned up at the start of the next PatchAsync call.
                try { PatchCleanup.Cleanup(rootDir); }
                catch (Exception cleanEx) { _logger.LogDebug(cleanEx, "Post-patch cleanup failed; leftovers will be removed on next patch."); }
            }
            else
            {
                // RollbackAsync (in the per-file catch) already restored backups and deleted the
                // journal. Don't rewrite a "Failed" journal here — a leftover journal makes the next
                // PatchAsync think a patch was interrupted and run a spurious rollback every time.
                _logger.LogError("Patch failed: {Message}", result.Message);
            }

            return result;
        }

        public async Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct)
        {
            _logger.LogInformation("Verifying files in {RootDir}", rootDir);
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, Config.Platform, ct);
            VerifyManifestOrThrow(manifest);
            VerifyResult result = new VerifyResult { Success = true };

            foreach (ManifestFile file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string localPath = PathUtils.ToOsPath(rootDir, file.Path);
                if (!File.Exists(localPath))
                {
                    _logger.LogWarning("Missing file during verification: {FilePath}", file.Path);
                    result.Success = false;
                    result.BrokenFiles.Add(file.Path);
                    continue;
                }

                using (FileStream fs = File.OpenRead(localPath))
                {
                    string hash = await _hasher.ComputeHashAsync(fs, ct);
                    if (!string.Equals(hash, file.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Hash mismatch for {FilePath}", file.Path);
                        result.Success = false;
                        result.BrokenFiles.Add(file.Path);
                    }
                }
            }

            result.Message = result.Success ? "OK" : "Broken files detected.";
            _logger.LogInformation("Verification complete: {Result}", result.Message);
            return result;
        }

        public Task RollbackAsync(string rootDir, CancellationToken ct)
        {
            _logger.LogInformation("Rolling back patch in {RootDir}", rootDir);
            string journalPath = PatchJournal.GetJournalPath(rootDir);
            PatchJournal? journal = PatchJournal.Load(journalPath);
            if (journal == null)
            {
                _logger.LogDebug("No journal found, nothing to roll back");
                return Task.CompletedTask;
            }

            foreach (PatchJournalEntry entry in journal.Entries)
            {
                if (entry.IsNew)
                {
                    // Net-new file from this session: remove it so a partial patch doesn't leave
                    // new files alongside rolled-back existing ones.
                    if (File.Exists(entry.TargetPath))
                    {
                        _logger.LogDebug("Removing net-new file {TargetPath} during rollback", entry.TargetPath);
                        File.Delete(entry.TargetPath);
                    }
                }
                else if (File.Exists(entry.BackupPath))
                {
                    _logger.LogDebug("Restoring {TargetPath} from backup", entry.TargetPath);
                    File.Copy(entry.BackupPath, entry.TargetPath, overwrite: true);
                    File.Delete(entry.BackupPath);
                }
            }

            File.Delete(journalPath);
            _logger.LogInformation("Rollback complete");
            return Task.CompletedTask;
        }

        // Maximum chunk size accepted on the local-copy path (256 MiB).
        // Protects ArrayPool.Rent from a hostile/corrupt manifest with an extreme Size value.
        private const int MaxChunkSize = 256 * 1024 * 1024;

        private static void CopyChunk(FileStream src, long offset, int size, FileStream output, IncrementalHash? verifyHash)
        {
            if (size <= 0 || size > MaxChunkSize)
            {
                throw new InvalidDataException(
                    $"Manifest chunk size {size} is out of the valid range (1–{MaxChunkSize} bytes).");
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                src.Seek(offset, SeekOrigin.Begin);
                // ReadExactly: a single Read may return fewer bytes than requested. A matched
                // rsync chunk always has `size` bytes available at `offset`, so a short read
                // means a truncated/changed source — fail (caught upstream → rollback) rather
                // than silently writing a short, corrupt chunk.
                src.ReadExactly(buffer, 0, size);
                output.Write(buffer, 0, size);
                verifyHash?.AppendData(buffer, 0, size);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<Dictionary<int, long>> RsyncMatchLocalAsync(string localPath, ManifestFile file, CancellationToken ct)
        {
            // Collect distinct chunk sizes present in this manifest file.
            // Fixed-size chunking → one size (or two if the last chunk is shorter).
            // CDC chunking → many sizes between min and max.
            // Every distinct size needs its own rolling window so that every chunk, regardless of
            // its actual byte count, gets a chance to be found. This fixes BUG-002 (CDC matcher
            // used file.ChunkSize as the sole window size, which mismatched all variable-size CDC
            // chunks) and BUG-010 (the last, shorter chunk of a fixed-size run was missed). We keep
            // all window sizes rolling in a SINGLE pass over the file (BUG-031) instead of
            // re-reading the whole file once per distinct size — the latter is O(#sizes * length)
            // I/O and pathological for CDC manifests, which have thousands of distinct sizes.
            Dictionary<int, Dictionary<uint, List<WeakEntry>>> perSizeWeakMaps =
                new Dictionary<int, Dictionary<uint, List<WeakEntry>>>();

            foreach (ManifestChunk chunk in file.Chunks)
            {
                int sz = chunk.Size;
                if (sz <= 0 || sz > MaxChunkSize)
                {
                    // Skip corrupt/oversized chunk entries; they can't be matched anyway.
                    continue;
                }

                if (!perSizeWeakMaps.TryGetValue(sz, out Dictionary<uint, List<WeakEntry>>? weakMap))
                {
                    weakMap = new Dictionary<uint, List<WeakEntry>>();
                    perSizeWeakMaps[sz] = weakMap;
                }

                if (!weakMap.TryGetValue(chunk.Weak, out List<WeakEntry>? list))
                {
                    list = new List<WeakEntry>();
                    weakMap[chunk.Weak] = list;
                }
                list.Add(new WeakEntry(chunk.Index, DecodeStrong(chunk.Strong)));
            }

            Dictionary<int, long> matches = new Dictionary<int, long>();

            if (perSizeWeakMaps.Count == 0)
            {
                return matches;
            }

            long fileLength;
            using (FileStream probe = File.OpenRead(localPath))
            {
                fileLength = probe.Length;
            }

            // Distinct window sizes that can possibly match (file must hold at least one window).
            List<int> sizes = new List<int>();
            foreach (int s in perSizeWeakMaps.Keys)
            {
                if (s <= fileLength)
                {
                    sizes.Add(s);
                }
            }
            if (sizes.Count == 0)
            {
                return matches;
            }

            int maxW = 0;
            foreach (int s in sizes)
            {
                if (s > maxW)
                {
                    maxW = s;
                }
            }

            // Single pass: keep one rolling checksum per distinct size plus a ring buffer of the
            // last maxW bytes (so the byte leaving window w at position p is history[(p-w) % maxW]).
            int n = sizes.Count;
            uint[] weak = new uint[n];
            byte[] history = new byte[maxW];
            byte[] linear = new byte[maxW];

            using (FileStream fs = File.OpenRead(localPath))
            {
                byte[] buffer = new byte[64 * 1024];
                long p = -1;
                int bufferRead;
                while ((bufferRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    for (int i = 0; i < bufferRead; i++)
                    {
                        byte add = buffer[i];
                        p++;
                        int writeIdx = (int)(p % maxW);

                        // Roll sizes whose window was already full at p-1. Read the leaving byte
                        // BEFORE overwriting the slot: for w == maxW the leaving slot IS writeIdx.
                        for (int k = 0; k < n; k++)
                        {
                            int w = sizes[k];
                            if (p >= w)
                            {
                                byte remove = history[(int)((p - w) % maxW)];
                                weak[k] = RollingChecksum.Roll(weak[k], remove, add, w);
                            }
                        }

                        history[writeIdx] = add;

                        // Seed the checksum for any size whose window becomes full exactly at p.
                        for (int k = 0; k < n; k++)
                        {
                            if (p == sizes[k] - 1)
                            {
                                Linearize(history, maxW, p, sizes[k], linear);
                                weak[k] = RollingChecksum.Compute(linear.AsSpan(0, sizes[k]));
                            }
                        }

                        // Try to match every size whose window ends at p.
                        for (int k = 0; k < n; k++)
                        {
                            int w = sizes[k];
                            if (p >= w - 1)
                            {
                                TryMatchAt(history, maxW, p, w, weak[k], perSizeWeakMaps[w], matches, linear);
                            }
                        }
                    }
                }
            }

            return matches;
        }

        // Synchronous, allocation-free on the no-hit path: no MemoryStream, no SHA256 object, no hex
        // string. Linearizes the w bytes ending at endPos from the history ring into a scratch
        // buffer, hashes into a stack buffer, and compares raw bytes. First match wins.
        private static void TryMatchAt(
            byte[] history,
            int maxW,
            long endPos,
            int w,
            uint weak,
            Dictionary<uint, List<WeakEntry>> weakMap,
            Dictionary<int, long> matches,
            byte[] linear)
        {
            if (!weakMap.TryGetValue(weak, out List<WeakEntry>? candidates))
            {
                return;
            }

            Linearize(history, maxW, endPos, w, linear);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(linear.AsSpan(0, w), hash);
            long startPos = endPos - w + 1;
            foreach (WeakEntry candidate in candidates)
            {
                if (hash.SequenceEqual(candidate.Strong))
                {
                    if (!matches.ContainsKey(candidate.Index))
                    {
                        matches[candidate.Index] = startPos;
                    }
                    break;
                }
            }
        }

        // Copies the w bytes ending at endPos (positions endPos-w+1 .. endPos) out of the history
        // ring into linear[0..w-1], unwrapping the circular buffer.
        private static void Linearize(byte[] history, int maxW, long endPos, int w, byte[] linear)
        {
            long startPos = endPos - w + 1;
            int startIdx = (int)(startPos % maxW);
            int firstPart = Math.Min(w, maxW - startIdx);
            Array.Copy(history, startIdx, linear, 0, firstPart);
            if (firstPart < w)
            {
                Array.Copy(history, 0, linear, firstPart, w - firstPart);
            }
        }

        // Decode a "sha256:<hex>" (or bare hex) strong hash to its 32 raw bytes.
        private static byte[] DecodeStrong(string strong)
        {
            int idx = strong.IndexOf(':');
            string hex = idx >= 0 ? strong.Substring(idx + 1) : strong;
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException ex)
            {
                // Surface a clear manifest error instead of a raw "not a valid hex string".
                throw new InvalidDataException($"Manifest contains an invalid chunk strong hash '{strong}'.", ex);
            }
        }

        private readonly struct WeakEntry
        {
            public readonly int Index;
            public readonly byte[] Strong;
            public WeakEntry(int index, byte[] strong)
            {
                Index = index;
                Strong = strong;
            }
        }

        private void VerifyManifestOrThrow(Manifest.Manifest manifest)
        {
            if (string.IsNullOrWhiteSpace(Config.TrustedPublicKey))
            {
                return;
            }

            bool ok = ManifestSigner.Verify(manifest, Config.TrustedPublicKey);
            if (!ok)
            {
                throw new InvalidDataException("Manifest signature is invalid.");
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
