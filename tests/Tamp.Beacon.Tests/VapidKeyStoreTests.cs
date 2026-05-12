using System;
using System.IO;
using Tamp.Beacon.Push;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class VapidKeyStoreTests : IDisposable
{
    private readonly string _tempDir;

    public VapidKeyStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tamp-beacon-vapid-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FirstAccess_GeneratesAndPersistsKey()
    {
        var path = Path.Combine(_tempDir, "vapid.key");
        Assert.False(File.Exists(path));

        var store = new VapidKeyStore(path, "mailto:test@tamp.local");

        Assert.True(File.Exists(path));
        Assert.False(string.IsNullOrEmpty(store.PublicKey));
        Assert.False(string.IsNullOrEmpty(store.Details.PrivateKey));
    }

    [Fact]
    public void SecondAccess_LoadsPersistedKey()
    {
        var path = Path.Combine(_tempDir, "vapid.key");
        var first = new VapidKeyStore(path, "mailto:test@tamp.local");
        var second = new VapidKeyStore(path, "mailto:test@tamp.local");

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.Details.PrivateKey, second.Details.PrivateKey);
    }

    [Fact]
    public void CreatesParentDirectory_WhenMissing()
    {
        var nested = Path.Combine(_tempDir, "deeply", "nested", "vapid.key");
        var store = new VapidKeyStore(nested, "mailto:test@tamp.local");
        Assert.True(File.Exists(nested));
        Assert.False(string.IsNullOrEmpty(store.PublicKey));
    }

    [Fact]
    public void EmptyKeyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VapidKeyStore("", "mailto:x@y"));
    }

    [Fact]
    public void EmptySubject_Throws()
    {
        var path = Path.Combine(_tempDir, "vapid.key");
        Assert.Throws<ArgumentException>(() => new VapidKeyStore(path, ""));
    }
}
