using System.Text.Json;
using Priest.Profiles;

namespace Priest.Tests;

public class ProfileLoaderTests : IDisposable
{
    private readonly string _tmpDir;

    public ProfileLoaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private string WriteProfile(string name, object data)
    {
        var path = Path.Combine(_tmpDir, $"{name}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        return path;
    }

    [Fact]
    public void LoadsProfileFromDisk()
    {
        WriteProfile("bot", new { identity = "I am a bot.", rules = "Be nice." });
        var loader = new FilesystemProfileLoader(_tmpDir);
        var profile = loader.Load("bot");
        Assert.Equal("bot", profile.Name);
        Assert.Equal("I am a bot.", profile.Identity);
    }

    [Fact]
    public void ReturnsDefaultProfileWhenNameIsDefaultAndFileMissing()
    {
        var loader = new FilesystemProfileLoader(_tmpDir);
        var profile = loader.Load("default");
        Assert.Equal("default", profile.Name);
    }

    [Fact]
    public void ThrowsWhenProfileNotFoundAndNotDefault()
    {
        var loader = new FilesystemProfileLoader(_tmpDir);
        Assert.Throws<Errors.PriestException>(() => loader.Load("missing"));
    }

    [Fact]
    public void CacheHit_ReturnsSameInstanceWhenMtimeUnchanged()
    {
        WriteProfile("bot", new { identity = "Bot.", rules = "" });
        var loader = new FilesystemProfileLoader(_tmpDir);
        var first = loader.Load("bot");
        var second = loader.Load("bot");
        Assert.Same(first, second);
    }

    [Fact]
    public void CacheInvalidation_ReturnsNewInstanceAfterFileTouched()
    {
        var filePath = WriteProfile("bot", new { identity = "Bot v1.", rules = "" });
        var loader = new FilesystemProfileLoader(_tmpDir);
        var first = loader.Load("bot");

        // Advance mtime by at least 1 second to guarantee a different timestamp
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(2));

        var second = loader.Load("bot");
        Assert.NotSame(first, second);
    }
}
