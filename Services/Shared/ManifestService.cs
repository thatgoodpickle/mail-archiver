using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    public sealed record ManifestInfo(string Name, DateTime GeneratedAt, long Entries, long? ChainSeq, string? ChainHash, string HeaderSha256);

    public sealed record ManifestCheck(
        bool Ok,
        string Name,
        bool SignatureValid,
        bool EntriesFileMatches,
        bool ChainStillContainsHead,
        long EmailsChangedOrMissingSince,
        IReadOnlyList<string> Problems);

    public interface IManifestService
    {
        /// <summary>Writes and signs a manifest of every stored original and the current chain head.</summary>
        Task<ManifestInfo> CreateAsync(string? requestedBy, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ManifestInfo>> ListAsync(int limit = 50);
        Task<ManifestCheck?> VerifyAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>SHA-256 of the DER public key (as in public-key.pem), or null when no key exists yet.</summary>
        string? GetPublicKeyFingerprint();
    }

    /// <summary>
    /// Signed manifest (fork, Phase 2). One manifest = a JSON-lines file with one line per stored
    /// original (email id, SHA-256, size, source, storage) plus a small signed header that names that file
    /// by its SHA-256, the entry count and the audit chain head. The header is signed with ECDSA P-256
    /// (DER signature), so anyone can check it without this software:
    /// <c>openssl dgst -sha256 -verify public-key.pem -signature manifest-X.sig manifest-X.json</c>.
    /// The manifest is also chained, so its existence cannot be quietly removed.
    /// </summary>
    public sealed class ManifestService : IManifestService
    {
        private static readonly Regex NamePattern = new(@"^manifest-\d{8}T\d{6}Z$", RegexOptions.Compiled);
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private readonly MailArchiverDbContext _db;
        private readonly IAuditChain _chain;
        private readonly IAuditChainVerifier _chainVerifier;
        private readonly IntegrityOptions _options;
        private readonly IHostEnvironment _env;
        private readonly ILogger<ManifestService> _logger;

        public ManifestService(MailArchiverDbContext db, IAuditChain chain, IAuditChainVerifier chainVerifier,
            IOptions<IntegrityOptions> options, IHostEnvironment env, ILogger<ManifestService> logger)
        {
            _db = db; _chain = chain; _chainVerifier = chainVerifier; _options = options.Value; _env = env; _logger = logger;
        }

        private string Dir => Path.IsPathRooted(_options.ManifestPath)
            ? _options.ManifestPath : Path.Combine(_env.ContentRootPath, _options.ManifestPath);

        private string KeyPath => string.IsNullOrWhiteSpace(_options.ManifestKeyPath)
            ? Path.Combine(Dir, "signing-key.pem") : _options.ManifestKeyPath;

        private string PublicKeyPath => Path.Combine(Dir, "public-key.pem");

        /// <summary>Loads the signing key, creating it (and the public key file) on first use.</summary>
        private ECDsa LoadOrCreateKey()
        {
            Directory.CreateDirectory(Dir);
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(KeyPath))
            {
                ecdsa.ImportFromPem(File.ReadAllText(KeyPath));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(KeyPath))!);
                File.WriteAllText(KeyPath, ecdsa.ExportPkcs8PrivateKeyPem());
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _logger.LogWarning("Created a new manifest signing key at {Path}", KeyPath);
            }
            // Always refreshed, so the public key file matches the key actually in use.
            File.WriteAllText(PublicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem());
            return ecdsa;
        }

        public async Task<ManifestInfo> CreateAsync(string? requestedBy, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            try
            {
                using var key = LoadOrCreateKey();
                var now = DateTime.UtcNow;
                var name = $"manifest-{now:yyyyMMdd'T'HHmmss'Z'}";
                var entriesName = name + ".jsonl";
                var entriesPath = Path.Combine(Dir, entriesName);

                // Head first, then the rows: any original stored in between is in the file but newer than the head, which is harmless.
                var head = await _chainVerifier.GetHeadAsync(cancellationToken);

                long count = 0;
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                await using (var fs = new FileStream(entriesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var rows = _db.RawMessages.AsNoTracking().OrderBy(r => r.ArchivedEmailId)
                        .Select(r => new { r.ArchivedEmailId, r.Sha256, r.Size, r.Source, r.StorageType }).AsAsyncEnumerable();
                    await foreach (var r in rows.WithCancellation(cancellationToken))
                    {
                        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                        {
                            emailId = r.ArchivedEmailId, sha256 = r.Sha256, size = r.Size, source = r.Source, storage = r.StorageType
                        }) + "\n");
                        sha.AppendData(line);
                        await fs.WriteAsync(line, cancellationToken);
                        count++;
                    }
                    await fs.FlushAsync(cancellationToken);
                    var entriesSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

                    var header = JsonSerializer.Serialize(new
                    {
                        version = 1,
                        generatedAt = now.ToString("O"),
                        generatedBy = requestedBy,
                        entries = count,
                        entriesFile = entriesName,
                        entriesSha256 = entriesSha,
                        chainSeq = head?.Seq,
                        chainHash = head?.Hash
                    }, new JsonSerializerOptions { WriteIndented = true });
                    var headerBytes = Encoding.UTF8.GetBytes(header);
                    var signature = key.SignData(headerBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

                    await File.WriteAllBytesAsync(Path.Combine(Dir, name + ".json"), headerBytes, cancellationToken);
                    await File.WriteAllBytesAsync(Path.Combine(Dir, name + ".sig"), signature, cancellationToken);

                    var headerSha = Convert.ToHexString(SHA256.HashData(headerBytes)).ToLowerInvariant();
                    await _chain.AppendAsync("manifest", null, headerSha, new { name, entries = count, chainSeq = head?.Seq }, cancellationToken);
                    _logger.LogInformation("Wrote signed manifest {Name}: {Count} originals, chain head {Seq}", name, count, head?.Seq);
                    return new ManifestInfo(name, now, count, head?.Seq, head?.Hash, headerSha);
                }
            }
            finally { Gate.Release(); }
        }

        public string? GetPublicKeyFingerprint()
        {
            if (!File.Exists(PublicKeyPath)) return null;
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(PublicKeyPath));
            return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        }

        public Task<IReadOnlyList<ManifestInfo>> ListAsync(int limit = 50)
        {
            var list = new List<ManifestInfo>();
            if (Directory.Exists(Dir))
            {
                foreach (var f in Directory.EnumerateFiles(Dir, "manifest-*.json").OrderByDescending(x => x).Take(limit))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (!NamePattern.IsMatch(name)) continue;
                    try
                    {
                        var bytes = File.ReadAllBytes(f);
                        using var doc = JsonDocument.Parse(bytes);
                        var r = doc.RootElement;
                        list.Add(new ManifestInfo(name, r.GetProperty("generatedAt").GetDateTime().ToUniversalTime(),
                            r.GetProperty("entries").GetInt64(),
                            r.TryGetProperty("chainSeq", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null,
                            r.TryGetProperty("chainHash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Unreadable manifest {File}", f); }
                }
            }
            return Task.FromResult<IReadOnlyList<ManifestInfo>>(list);
        }

        public async Task<ManifestCheck?> VerifyAsync(string name, CancellationToken cancellationToken = default)
        {
            if (!NamePattern.IsMatch(name)) return null; // also keeps path characters out
            var headerPath = Path.Combine(Dir, name + ".json");
            var sigPath = Path.Combine(Dir, name + ".sig");
            if (!File.Exists(headerPath)) return null;

            var problems = new List<string>();
            var headerBytes = await File.ReadAllBytesAsync(headerPath, cancellationToken);

            var sigOk = false;
            if (!File.Exists(sigPath)) problems.Add("The signature file is missing.");
            else if (!File.Exists(KeyPath) && !File.Exists(PublicKeyPath)) problems.Add("No key to check the signature with.");
            else
            {
                using var key = ECDsa.Create();
                // The private key file is the authority; the public key file is only a copy for outsiders.
                key.ImportFromPem(File.Exists(KeyPath) ? File.ReadAllText(KeyPath) : File.ReadAllText(PublicKeyPath));
                sigOk = key.VerifyData(headerBytes, await File.ReadAllBytesAsync(sigPath, cancellationToken),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                if (!sigOk) problems.Add("The signature does not match the manifest header.");
            }

            using var doc = JsonDocument.Parse(headerBytes);
            var root = doc.RootElement;
            var entriesFile = root.GetProperty("entriesFile").GetString() ?? string.Empty;
            var entriesPath = Path.Combine(Dir, Path.GetFileName(entriesFile));

            var entriesOk = false;
            var changed = 0L;
            if (!File.Exists(entriesPath)) problems.Add("The entries file is missing.");
            else
            {
                await using var fs = File.OpenRead(entriesPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken)).ToLowerInvariant();
                entriesOk = string.Equals(actual, root.GetProperty("entriesSha256").GetString(), StringComparison.Ordinal);
                if (!entriesOk) problems.Add("The entries file does not match the hash in the signed header.");
                else
                {
                    // Compare with the database now: an original whose hash differs or whose row is gone.
                    var current = await _db.RawMessages.AsNoTracking()
                        .ToDictionaryAsync(r => r.ArchivedEmailId, r => r.Sha256, cancellationToken);
                    foreach (var line in await File.ReadAllLinesAsync(entriesPath, cancellationToken))
                    {
                        if (line.Length == 0) continue;
                        using var l = JsonDocument.Parse(line);
                        var id = l.RootElement.GetProperty("emailId").GetInt32();
                        var sha = l.RootElement.GetProperty("sha256").GetString();
                        if (!current.TryGetValue(id, out var now) || !string.Equals(now, sha, StringComparison.Ordinal)) changed++;
                    }
                    if (changed > 0) problems.Add($"{changed} email(s) in the manifest are gone or hold a different original hash now (deleted emails show up here too).");
                }
            }

            // The chain entry the manifest recorded must still carry the same hash.
            var chainOk = true;
            if (root.TryGetProperty("chainSeq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
            {
                var seq = seqEl.GetInt64();
                var want = root.GetProperty("chainHash").GetString();
                var have = await _db.AuditChain.AsNoTracking().Where(e => e.Seq == seq).Select(e => e.EntryHash).FirstOrDefaultAsync(cancellationToken);
                chainOk = string.Equals(have, want, StringComparison.Ordinal);
                if (!chainOk) problems.Add($"Audit chain entry {seq} is missing or differs from the head recorded in the manifest.");
            }

            // Deleted emails are legitimate, so "changed" alone does not fail the check; the other findings do.
            var ok = sigOk && entriesOk && chainOk;
            return new ManifestCheck(ok, name, sigOk, entriesOk, chainOk, changed, problems);
        }
    }
}
