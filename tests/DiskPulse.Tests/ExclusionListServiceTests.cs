using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public sealed class ExclusionListServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cleaner-excl-{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoad_NormalizesAndDeduplicatesPaths()
    {
        var path = Path.Combine(_root, "exclusions.json");
        var service = new ExclusionListService(path);
        var keepDir = Path.Combine(_root, "Keep");
        var logFile = Path.Combine(_root, "file.log");

        var saved = OperatingSystem.IsWindows()
            ? service.Save([keepDir + "\\", keepDir.ToLowerInvariant(), logFile])
            : service.Save([keepDir + "/", keepDir, logFile]);
        Assert.True(saved);

        var loaded = service.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(keepDir, loaded[0]);
        Assert.Equal(logFile, loaded[1]);
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenFileCorrupt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "exclusions.json");
        File.WriteAllText(path, "{invalid");
        var service = new ExclusionListService(path);

        Assert.Empty(service.Load());
    }

    [Fact]
    public void IsExcluded_MatchesExactFileAndSubfolderWithoutPrefixCollision()
    {
        var keepDir = Path.Combine(_root, "Keep");
        var singleLog = Path.Combine(_root, "single.log");
        var exclusions = new[] { keepDir, singleLog };

        Assert.True(ExclusionListService.IsExcluded(Path.Combine(keepDir, "a.txt"), exclusions));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(ExclusionListService.IsExcluded(Path.Combine(keepDir.ToLowerInvariant(), "sub", "b.txt"), exclusions));
        }

        Assert.True(ExclusionListService.IsExcluded(singleLog, exclusions));

        Assert.False(ExclusionListService.IsExcluded(Path.Combine(_root, "KeepOther", "a.txt"), exclusions));
        Assert.False(ExclusionListService.IsExcluded(singleLog + ".bak", exclusions));
    }

    [Fact]
    public void NormalizePath_TrimsTrailingSeparatorAndHandlesInvalidPaths()
    {
        var sampleDir = Path.Combine(_root, "Keep");
        Assert.Equal(sampleDir, ExclusionListService.NormalizePath($"  {sampleDir}{Path.DirectorySeparatorChar}  "));
        Assert.Equal(sampleDir, ExclusionListService.NormalizePath(sampleDir));
        var root = Path.GetPathRoot(_root)!;
        Assert.Equal(root, ExclusionListService.NormalizePath(root));
        Assert.Null(ExclusionListService.NormalizePath("   "));
        Assert.Null(ExclusionListService.NormalizePath("bad\0path"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
