using System.Text;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Hash-named originals: naming, write-once behaviour and path safety. Synthetic bytes only.</summary>
public class HashNamedRawMessageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (FileSystemRawMessageStore Store, RawMessagePathResolver Paths, RawMessageOptions Options) Make(bool hashNamed)
    {
        var options = new RawMessageOptions
        {
            Enabled = true,
            Storage = RawMessageStorage.FileSystem,
            MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = hashNamed }
        };
        var paths = new RawMessagePathResolver(options.FileSystem, _root);
        // CreateRow/PersistAsync never touch the database context.
        var store = new FileSystemRawMessageStore(null!, Options.Create(options), paths, NullLogger<FileSystemRawMessageStore>.Instance);
        return (store, paths, options);
    }

    private static RawMessageCapture Capture(string text) => new(Encoding.ASCII.GetBytes(text), RawMessageSource.Imap);

    [Fact]
    public void GetRelativePathForHash_ShardsByHexPrefix()
    {
        var (_, paths, _) = Make(true);
        var sha = new string('a', 2) + new string('b', 2) + new string('c', 60);
        Assert.Equal($"aa/bb/{sha}.eml.gz", paths.GetRelativePathForHash(sha));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // uppercase
    [InlineData("abc")]
    public void GetRelativePathForHash_RejectsAnythingButLowercaseHex(string value)
    {
        var (_, paths, _) = Make(true);
        Assert.Throws<ArgumentException>(() => paths.GetRelativePathForHash(value));
    }

    [Fact]
    public void CreateRow_HashNamed_UsesTheHash_NotTheEmailId()
    {
        var (store, _, _) = Make(true);
        var capture = Capture("Subject: x\r\n\r\nbody\r\n");
        var row = store.CreateRow(42, capture)!;

        Assert.Equal($"{row.Sha256[..2]}/{row.Sha256.Substring(2, 2)}/{row.Sha256}.eml.gz", row.StoragePath);
        Assert.DoesNotContain("42", Path.GetFileName(row.StoragePath!));
    }

    [Fact]
    public void CreateRow_NotHashNamed_KeepsUpstreamIdNaming()
    {
        var (store, paths, _) = Make(false);
        var row = store.CreateRow(42, Capture("Subject: x\r\n\r\nbody\r\n"))!;
        Assert.Equal(paths.GetRelativePath(42), row.StoragePath);
    }

    [Fact]
    public async Task Persist_WritesFile_AndSameBytesTwiceShareOneFile()
    {
        var (store, paths, _) = Make(true);
        var capture = Capture("Subject: same\r\n\r\nidentical bytes\r\n");

        var row1 = store.CreateRow(1, capture)!;
        var row2 = store.CreateRow(2, capture)!;
        Assert.Equal(row1.StoragePath, row2.StoragePath);

        await store.PersistAsync(row1, capture);
        var absolute = paths.GetAbsolutePath(row1.StoragePath!);
        Assert.True(File.Exists(absolute));
        var firstWrite = File.GetLastWriteTimeUtc(absolute);
        var firstBytes = await File.ReadAllBytesAsync(absolute);

        await Task.Delay(50);
        await store.PersistAsync(row2, capture); // second email, same bytes: verified, not rewritten
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(absolute));
        // F-11: a reused file is touched so the original bin sees it as in use; its content is not rewritten.
        Assert.True(File.GetLastWriteTimeUtc(absolute) > firstWrite);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Persist_Refuses_ToOverwriteAFileThatDoesNotMatchItsName()
    {
        var (store, paths, _) = Make(true);
        var capture = Capture("Subject: real\r\n\r\ncontent\r\n");
        var row = store.CreateRow(1, capture)!;

        var absolute = paths.GetAbsolutePath(row.StoragePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, RawMessageCodec.Compress(Encoding.ASCII.GetBytes("tampered")));

        await Assert.ThrowsAsync<IOException>(() => store.PersistAsync(row, capture));
        // and the tampered file is still exactly as it was
        Assert.Equal(Encoding.ASCII.GetBytes("tampered"), RawMessageCodec.Decompress(await File.ReadAllBytesAsync(absolute)));
    }
}
