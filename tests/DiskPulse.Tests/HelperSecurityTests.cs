using Xunit;

namespace Cleaner.Tests;

public class HelperSecurityTests
{
    [Theory]
    [InlineData("apt", true)]
    [InlineData("dpkg", true)]
    [InlineData("dnf", true)]
    [InlineData("rpm", true)]
    [InlineData("pacman", true)]
    [InlineData("flatpak", true)]
    [InlineData("bash", false)]
    [InlineData("sh", false)]
    [InlineData("sudo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidPackageManager_AllowsOnlyKnownManagers(string? manager, bool expected)
    {
        Assert.Equal(expected, HelperSecurityValidator.IsValidPackageManager(manager));
    }

    [Theory]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("gimp-data", true)]
    [InlineData("libstdc++6", true)]
    [InlineData("vlc:amd64", true)]
    [InlineData("foo; rm -rf /", false)]
    [InlineData("../evil", false)]
    [InlineData("-rf", false)]
    [InlineData("pkg with space", false)]
    [InlineData("", false)]
    public void IsValidPackageId_RejectsShellInjectionAndTraversal(string? packageId, bool expected)
    {
        Assert.Equal(expected, HelperSecurityValidator.IsValidPackageId(packageId));
    }

    [Theory]
    [InlineData(@"C:\Temp\..\Windows\System32\cmd.exe", true)]
    [InlineData("/tmp/../etc/passwd", true)]
    [InlineData("/tmp/ok/file.json", false)]
    [InlineData("line1\nline2", true)]
    public void HasTraversalOrControlCharacters_DetectsUnsafePaths(string path, bool expected)
    {
        Assert.Equal(expected, HelperSecurityValidator.HasTraversalOrControlCharacters(path));
    }

    [Fact]
    public void TryValidateDataExchangePath_AcceptsValidAbsolutePath_AndRejectsTraversal()
    {
        var tempDir = Directory.CreateTempSubdirectory("diskpulse-helper-sec");
        try
        {
            var validPath = Path.Combine(tempDir.FullName, "request.json");
            File.WriteAllText(validPath, "{}");

            Assert.True(HelperSecurityValidator.TryValidateDataExchangePath(validPath, mustExist: true, out var resolved));
            Assert.Equal(Path.GetFullPath(validPath), resolved);

            var traversalPath = Path.Combine(tempDir.FullName, "..", tempDir.Name, "request.json");
            Assert.False(HelperSecurityValidator.TryValidateDataExchangePath(traversalPath, mustExist: true, out _));
            Assert.False(HelperSecurityValidator.TryValidateDataExchangePath("relative.json", mustExist: false, out _));
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public void TryValidateDataExchangePath_RejectsSymlinkWhenSupported()
    {
        var tempDir = Directory.CreateTempSubdirectory("diskpulse-helper-sym");
        try
        {
            var target = Path.Combine(tempDir.FullName, "target.json");
            var link = Path.Combine(tempDir.FullName, "link.json");
            File.WriteAllText(target, "{}");

            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // На Windows без прав создания symlink пропускаем этот кейс
            }

            Assert.False(HelperSecurityValidator.TryValidateDataExchangePath(link, mustExist: true, out _));
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public void TryResolveAllowedCleanupTarget_EnforcesAllowedRoots()
    {
        var allowedDir = Directory.CreateTempSubdirectory("diskpulse-allowed-root");
        var outsideDir = Directory.CreateTempSubdirectory("diskpulse-outside-root");
        try
        {
            var insideFile = Path.Combine(allowedDir.FullName, "cache", "item.tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(insideFile)!);
            File.WriteAllText(insideFile, "abc");

            var outsideFile = Path.Combine(outsideDir.FullName, "secret.txt");
            File.WriteAllText(outsideFile, "xyz");

            var roots = new[] { allowedDir.FullName };

            Assert.True(HelperSecurityValidator.TryResolveAllowedCleanupTarget(insideFile, roots, out var resolvedInside, out var matchedRoot));
            Assert.Equal(Path.GetFullPath(insideFile), resolvedInside);
            Assert.StartsWith(CleanerPaths.EnsureTrailingSeparator(allowedDir.FullName), matchedRoot, CleanerPaths.PathComparison);

            Assert.False(HelperSecurityValidator.TryResolveAllowedCleanupTarget(outsideFile, roots, out _, out _));

            var traversalToOutside = Path.Combine(allowedDir.FullName, "..", outsideDir.Name, "secret.txt");
            Assert.False(HelperSecurityValidator.TryResolveAllowedCleanupTarget(traversalToOutside, roots, out _, out _));
        }
        finally
        {
            if (allowedDir.Exists)
            {
                allowedDir.Delete(true);
            }

            if (outsideDir.Exists)
            {
                outsideDir.Delete(true);
            }
        }
    }

    [Fact]
    public void IsSafeResidualRoot_RejectsSystemRoots_AndAllowsAppFolder()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "/";
        Assert.False(HelperSecurityValidator.IsSafeResidualRoot(root));
        Assert.False(HelperSecurityValidator.IsSafeResidualRoot("/"));
        Assert.False(HelperSecurityValidator.IsSafeResidualRoot("/usr"));
        Assert.False(HelperSecurityValidator.IsSafeResidualRoot("/etc"));

        var safeAppDir = Path.Combine(Path.GetTempPath(), "diskpulse-test-residual-app");
        Assert.True(HelperSecurityValidator.IsSafeResidualRoot(safeAppDir));
    }

    [Fact]
    public void HelperProgram_ExecuteCleanup_DeletesAllowedAndSkipsOutsideFile()
    {
        var allowedDir = Directory.CreateTempSubdirectory("diskpulse-exec-allowed");
        var outsideDir = Directory.CreateTempSubdirectory("diskpulse-exec-outside");
        try
        {
            var oldUtc = DateTime.UtcNow.AddDays(-5);
            var allowedFile = Path.Combine(allowedDir.FullName, "temp.log");
            File.WriteAllText(allowedFile, "12345");
            File.SetLastWriteTimeUtc(allowedFile, oldUtc);

            var outsideFile = Path.Combine(outsideDir.FullName, "keep.log");
            File.WriteAllText(outsideFile, "99999");
            File.SetLastWriteTimeUtc(outsideFile, oldUtc);

            var request = new HelperCleanupRequest(
                Files:
                [
                    new HelperCleanupFileItem(allowedFile, 5, oldUtc),
                    new HelperCleanupFileItem(outsideFile, 5, oldUtc)
                ],
                Drives: [],
                MinimumAgeHours: 1,
                EmptyRecycleBin: false);

            var response = HelperProgram.ExecuteCleanup(request, [allowedDir.FullName]);

            Assert.True(response.Success);
            Assert.Equal(1, response.DeletedFiles);
            Assert.Equal(1, response.SkippedFiles);
            Assert.False(File.Exists(allowedFile));
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            if (allowedDir.Exists)
            {
                allowedDir.Delete(true);
            }

            if (outsideDir.Exists)
            {
                outsideDir.Delete(true);
            }
        }
    }

    [Fact]
    public void HelperProgram_Run_ReturnsExitCode2_OnMissingOrInvalidArguments()
    {
        Assert.Equal(2, HelperProgram.Run([], [Path.GetTempPath()]));
        Assert.Equal(2, HelperProgram.Run(["--unknown"], [Path.GetTempPath()]));
        Assert.Equal(2, HelperProgram.Run(["--cleanup", "--input", "relative.json", "--output", "out.json"], [Path.GetTempPath()]));
    }
}
