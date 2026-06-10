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
                _logger.LogWarning("Incomplete journal found, rolling back previous patch");
                await RollbackAsync(rootDir, ct);
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

                    File.Copy(tempPath, localPath, overwrite: true);
                    // The file is now swapped in. Deleting the temp is cleanup only — a failure here
                    // (e.g. AV/indexer holding the handle on Windows) must NOT trigger rollback of an
                    // already-applied file. Leftover .hina.tmp is removed by PatchCleanup later.
                    try { File.Delete(tempPath); }
                    catch (Exception delEx) { _logger.LogDebug(delEx, "Could not delete temp file {Temp}; left for cleanup.", tempPath); }

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
                journal.Status = "Completed";
                await journal.SaveAsync(journalPath);
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

        private static void CopyChunk(FileStream src, long offset, int size, FileStream output, IncrementalHash? verifyHash)
        {
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
            // Build weak checksum lookup for quick candidate matching. Pre-decode each chunk's
            // strong hash to raw bytes once here, so the per-offset hot path can compare 32 bytes
            // directly (SequenceEqual) instead of formatting a hex string and doing a string compare.
            Dictionary<uint, List<WeakEntry>> weakMap = new Dictionary<uint, List<WeakEntry>>();
            foreach (ManifestChunk chunk in file.Chunks)
            {
                if (!weakMap.TryGetValue(chunk.Weak, out List<WeakEntry>? list))
                {
                    list = new List<WeakEntry>();
                    weakMap[chunk.Weak] = list;
                }
                list.Add(new WeakEntry(chunk.Index, DecodeStrong(chunk.Strong)));
            }

            Dictionary<int, long> matches = new Dictionary<int, long>();
            int chunkSize = file.ChunkSize;
            // A hostile/corrupt manifest could carry chunkSize <= 0; the sliding window uses it as a
            // modulo divisor (% chunkSize) and a buffer size, so guard against a DivideByZeroException.
            // Returning no matches just means every chunk is downloaded.
            if (chunkSize <= 0)
            {
                return matches;
            }

            using (FileStream fs = File.OpenRead(localPath))
            {
                if (fs.Length < chunkSize)
                {
                    return matches;
                }

                byte[] window = new byte[chunkSize];
                int read = await fs.ReadAsync(window.AsMemory(0, chunkSize), ct);
                if (read < chunkSize)
                {
                    return matches;
                }

                // Reusable scratch for the linearized window — avoids an allocation per weak hit.
                byte[] linear = new byte[chunkSize];

                long offset = 0;
                // Start with the first full window.
                uint weak = RollingChecksum.Compute(window);
                TryMatchWindow(window, 0, weak, offset, weakMap, matches, linear);

                int ringIndex = 0;
                byte[] buffer = new byte[64 * 1024];
                int bufferRead;

                // Slide one byte at a time using a ring buffer and rolling checksum.
                while ((bufferRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    for (int i = 0; i < bufferRead; i++)
                    {
                        byte remove = window[ringIndex];
                        byte add = buffer[i];
                        window[ringIndex] = add;
                        ringIndex = (ringIndex + 1) % chunkSize;

                        weak = RollingChecksum.Roll(weak, remove, add, chunkSize);
                        offset++;

                        TryMatchWindow(window, ringIndex, weak, offset, weakMap, matches, linear);
                    }
                }
            }

            return matches;
        }

        // Synchronous, allocation-free on both the no-hit and hit paths: no per-byte await state
        // machine, no MemoryStream, no SHA256 object, no hex string. Hashes the linearized window
        // straight into a stack buffer and compares raw bytes.
        private static void TryMatchWindow(
            byte[] ring,
            int ringIndex,
            uint weak,
            long offset,
            Dictionary<uint, List<WeakEntry>> weakMap,
            Dictionary<int, long> matches,
            byte[] linear)
        {
            if (!weakMap.TryGetValue(weak, out List<WeakEntry>? candidates))
            {
                return;
            }

            // Resolve weak hits by strong hash to avoid collisions.
            RingToLinear(ring, ringIndex, linear);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(linear, hash);
            foreach (WeakEntry candidate in candidates)
            {
                if (hash.SequenceEqual(candidate.Strong))
                {
                    if (!matches.ContainsKey(candidate.Index))
                    {
                        matches[candidate.Index] = offset;
                    }
                    break;
                }
            }
        }

        private static void RingToLinear(byte[] ring, int startIndex, byte[] linear)
        {
            int tail = ring.Length - startIndex;
            Array.Copy(ring, startIndex, linear, 0, tail);
            if (startIndex > 0)
            {
                Array.Copy(ring, 0, linear, tail, startIndex);
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
    }
}
