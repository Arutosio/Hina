using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, ct);
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
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, ct);
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

                    using (FileStream outFs = File.Create(tempPath))
                    {
                        // Rebuild the file in manifest order, reusing local data when possible.
                        foreach (ManifestChunk chunk in file.Chunks)
                        {
                            if (matches.TryGetValue(chunk.Index, out long offset))
                            {
                                // Reuse local data when a chunk matches.
                                CopyChunk(localPath, offset, chunk.Size, outFs);
                            }
                            else
                            {
                                // Download missing chunk from server.
                                _logger.LogDebug("Downloading chunk {ChunkIndex} for {FilePath}", chunk.Index, file.Path);
                                byte[] data = await _http.GetChunkAsync(Config.BaseUrl, chunk.Strong, ct);
                                await outFs.WriteAsync(data.AsMemory(0, chunk.Size), ct);
                            }
                        }
                    }

                    if (Config.Verify)
                    {
                        using (FileStream fs = File.OpenRead(tempPath))
                        {
                            string hash = await _hasher.ComputeHashAsync(fs, ct);
                            if (!string.Equals(hash, file.FileHash, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("Hash mismatch after patch.");
                            }
                        }
                        _logger.LogDebug("Verification passed for {FilePath}", file.Path);
                    }

                    // Keep a backup when configured to allow rollback.
                    if (Config.Backup && File.Exists(localPath))
                    {
                        File.Copy(localPath, backupPath, overwrite: true);
                        journal.Entries.Add(new PatchJournalEntry
                        {
                            TargetPath = localPath,
                            BackupPath = backupPath
                        });
                        await journal.SaveAsync(journalPath);
                    }

                    File.Copy(tempPath, localPath, overwrite: true);
                    File.Delete(tempPath);

                    result.AppliedFiles.Add(file.Path);
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
                _logger.LogError("Patch failed: {Message}", result.Message);
                journal.Status = "Failed";
                await journal.SaveAsync(journalPath);
            }

            return result;
        }

        public async Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct)
        {
            _logger.LogInformation("Verifying files in {RootDir}", rootDir);
            Manifest.Manifest manifest = await _http.GetManifestAsync(Config.BaseUrl, Config.Channel, ct);
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
                if (File.Exists(entry.BackupPath))
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

        private static void CopyChunk(string localPath, long offset, int size, FileStream output)
        {
            byte[] buffer = new byte[size];
            using (FileStream fs = File.OpenRead(localPath))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                // ReadExactly: a single Read may return fewer bytes than requested. A matched
                // rsync chunk always has `size` bytes available at `offset`, so a short read
                // means a truncated/changed source — fail (caught upstream → rollback) rather
                // than silently writing a short, corrupt chunk.
                fs.ReadExactly(buffer, 0, size);
                output.Write(buffer, 0, size);
            }
        }

        private async Task<Dictionary<int, long>> RsyncMatchLocalAsync(string localPath, ManifestFile file, CancellationToken ct)
        {
            // Build weak checksum lookup for quick candidate matching.
            Dictionary<uint, List<ManifestChunk>> weakMap = new Dictionary<uint, List<ManifestChunk>>();
            foreach (ManifestChunk chunk in file.Chunks)
            {
                if (!weakMap.TryGetValue(chunk.Weak, out List<ManifestChunk>? list))
                {
                    list = new List<ManifestChunk>();
                    weakMap[chunk.Weak] = list;
                }
                list.Add(chunk);
            }

            Dictionary<int, long> matches = new Dictionary<int, long>();
            int chunkSize = file.ChunkSize;

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

                long offset = 0;
                // Start with the first full window.
                uint weak = RollingChecksum.Compute(window);
                await TryMatchWindowAsync(window, 0, weak, offset, weakMap, matches, ct);

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

                        await TryMatchWindowAsync(window, ringIndex, weak, offset, weakMap, matches, ct);
                    }
                }
            }

            return matches;
        }

        private async Task TryMatchWindowAsync(
            byte[] ring,
            int ringIndex,
            uint weak,
            long offset,
            Dictionary<uint, List<ManifestChunk>> weakMap,
            Dictionary<int, long> matches,
            CancellationToken ct)
        {
            if (!weakMap.TryGetValue(weak, out List<ManifestChunk>? candidates))
            {
                return;
            }

            // Resolve weak hits by strong hash to avoid collisions.
            byte[] window = RingToLinear(ring, ringIndex);
            using (var ms = new MemoryStream(window, 0, window.Length, writable: false, publiclyVisible: true))
            {
                string strong = await _hasher.ComputeHashAsync(ms, ct);
                foreach (ManifestChunk candidate in candidates)
                {
                    if (string.Equals(candidate.Strong, strong, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!matches.ContainsKey(candidate.Index))
                        {
                            matches[candidate.Index] = offset;
                        }
                        break;
                    }
                }
            }
        }

        private static byte[] RingToLinear(byte[] ring, int startIndex)
        {
            byte[] linear = new byte[ring.Length];
            int tail = ring.Length - startIndex;
            Array.Copy(ring, startIndex, linear, 0, tail);
            if (startIndex > 0)
            {
                Array.Copy(ring, 0, linear, tail, startIndex);
            }
            return linear;
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
