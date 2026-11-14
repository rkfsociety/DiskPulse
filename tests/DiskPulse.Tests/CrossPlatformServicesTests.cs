using Xunit;

namespace Cleaner.Tests;

public class CrossPlatformServicesTests
{
    [Fact]
    public void LinuxStorageService_ParseMountPoints_FiltersVirtualFileSystems()
    {
        var lines = new[]
        {
            "proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0",
            "tmpfs /run tmpfs rw,nosuid,nodev 0 0",
            "/dev/nvme0n1p2 / ext4 rw,relatime 0 0",
            "/dev/sda1 /mnt/My\\040Drive btrfs rw,relatime 0 0",
            "overlay /var/lib/docker/overlay2/abc/merged overlay rw 0 0"
        };

        var mounts = LinuxStorageService.ParseMountPoints(lines);

        Assert.Equal(2, mounts.Count);
        Assert.Equal("/", mounts[0].MountPoint);
        Assert.Equal("ext4", mounts[0].FileSystem);
        Assert.Equal("/mnt/My Drive", mounts[1].MountPoint);
        Assert.Equal("btrfs", mounts[1].FileSystem);
    }

    [Theory]
    [InlineData("systemd-private-1234/tmp", true)]
    [InlineData(".X11-unix/X0", true)]
    [InlineData("snap-private-tmp/abc", true)]
    [InlineData("pulse-xyz/pid", true)]
    [InlineData("my-app-cache/old.tmp", false)]
    public void LinuxScanRootsProvider_IsProtectedTempRelativePath_ProtectsSystemSockets(string relativePath, bool expected)
    {
        Assert.Equal(expected, LinuxScanRootsProvider.IsProtectedTempRelativePath(relativePath));
    }

    [Fact]
    public void UnixSafeDeleteService_TryDeleteUnix_EnforcesRootAndAge()
    {
        var root = Directory.CreateTempSubdirectory("diskpulse-unix-del");
        var outside = Directory.CreateTempSubdirectory("diskpulse-unix-outside");
        try
        {
            var oldFile = Path.Combine(root.FullName, "old.tmp");
            File.WriteAllText(oldFile, "old");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));

            var newFile = Path.Combine(root.FullName, "new.tmp");
            File.WriteAllText(newFile, "new");
            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

            var outsideFile = Path.Combine(outside.FullName, "ext.tmp");
            File.WriteAllText(outsideFile, "ext");

            var threshold = DateTime.UtcNow.AddHours(-24);

            var deletedOld = UnixSafeDeleteService.TryDeleteUnix(oldFile, root.FullName, threshold);
            Assert.True(deletedOld.Succeeded);
            Assert.False(File.Exists(oldFile));

            var skippedNew = UnixSafeDeleteService.TryDeleteUnix(newFile, root.FullName, threshold);
            Assert.False(skippedNew.Succeeded);
            Assert.Equal(CleanupSkipReason.Changed, skippedNew.Reason);
            Assert.True(File.Exists(newFile));

            var skippedOutside = UnixSafeDeleteService.TryDeleteUnix(outsideFile, root.FullName, threshold);
            Assert.False(skippedOutside.Succeeded);
            Assert.Equal(CleanupSkipReason.AccessDenied, skippedOutside.Reason);
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(true);
            }

            if (outside.Exists)
            {
                outside.Delete(true);
            }
        }
    }

    [Fact]
    public void LinuxTrashService_GetInfoAndEmpty_CountsAndClearsFiles()
    {
        var trashDir = Directory.CreateTempSubdirectory("diskpulse-linux-trash");
        try
        {
            var filesDir = Path.Combine(trashDir.FullName, "files");
            var infoDir = Path.Combine(trashDir.FullName, "info");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);

            File.WriteAllBytes(Path.Combine(filesDir, "sample.bin"), new byte[128]);
            File.WriteAllText(Path.Combine(infoDir, "sample.bin.trashinfo"), "[Trash Info]");

            var service = new LinuxTrashService(trashDir.FullName);
            var before = service.GetInfo(["/"]);
            Assert.Equal(128, before.Bytes);
            Assert.Equal(1, before.Items);

            Assert.True(service.Empty("/"));
            var after = service.GetInfo(["/"]);
            Assert.Equal(0, after.Bytes);
            Assert.Equal(0, after.Items);
        }
        finally
        {
            if (trashDir.Exists)
            {
                trashDir.Delete(true);
            }
        }
    }

    [Fact]
    public void LinuxInstalledProgramsService_ParsesDesktopAndPackageManagers()
    {
        var desktop = LinuxInstalledProgramsService.ParseDesktopFile(
            [
                "[Desktop Entry]",
                "Type=Application",
                "Name=Visual Studio Code",
                "Exec=/usr/share/code/code %F",
                "X-Flatpak=com.visualstudio.code"
            ],
            "/usr/share/applications/code.desktop",
            "Все пользователи");

        Assert.NotNull(desktop);
        Assert.Equal("Visual Studio Code", desktop!.Name);
        Assert.Equal("code", desktop.ExecBinary);
        Assert.Equal("com.visualstudio.code", desktop.FlatpakAppId);

        var dpkg = LinuxInstalledProgramsService.ParseDpkgOutput(
            "firefox\t131.0-1\t250000\tMozilla Team <team@mozilla.org>\tamd64\tinstall ok installed\n" +
            "removed-pkg\t1.0\t10\tMaintainer\tamd64\tdeinstall ok config-files\n");
        Assert.Single(dpkg);
        Assert.Equal("dpkg", dpkg[0].Manager);
        Assert.Equal("firefox", dpkg[0].PackageId);
        Assert.Equal(250000L * 1024L, dpkg[0].EstimatedBytes);

        var rpm = LinuxInstalledProgramsService.ParseRpmOutput("gimp\t2.10.38-1.fc40\t104857600\tFedora Project\t1700000000\tx86_64\n");
        Assert.Single(rpm);
        Assert.Equal("rpm", rpm[0].Manager);
        Assert.Equal(104857600L, rpm[0].EstimatedBytes);

        var pacman = LinuxInstalledProgramsService.ParsePacmanOutput("vlc 3.0.21-1\n");
        Assert.Single(pacman);
        Assert.Equal("pacman", pacman[0].Manager);

        var flatpak = LinuxInstalledProgramsService.ParseFlatpakOutput("org.gimp.GIMP\tGNU Image Manipulation Program\t2.10.38\t420 MB\tsystem\n");
        Assert.Single(flatpak);
        Assert.Equal("flatpak", flatpak[0].Manager);
        Assert.True(flatpak[0].EstimatedBytes > 400_000_000L);
    }

    [Fact]
    public void LinuxResidualCleanupService_GetLinuxRoots_ReturnsUserScopedPaths()
    {
        var homeDir = Directory.CreateTempSubdirectory("diskpulse-linux-home");
        try
        {
            var flatpakAppDir = Path.Combine(homeDir.FullName, ".var", "app", "com.acme.MyApp");
            var configDir = Path.Combine(homeDir.FullName, ".config", "MyApp");
            Directory.CreateDirectory(flatpakAppDir);
            Directory.CreateDirectory(configDir);

            var program = new InstalledProgram(
                Name: "MyApp",
                Publisher: "Acme",
                Version: "1.0",
                EstimatedBytes: 1024,
                InstalledAt: null,
                LastUsedAt: null,
                LastUsedSource: "test",
                InstallLocation: "",
                RegistryScope: "Текущий пользователь",
                UninstallString: "",
                RegistryKeyPath: "",
                PackageManager: "flatpak",
                PackageId: "com.acme.MyApp");

            var roots = LinuxResidualCleanupService.GetLinuxRoots(program, homeDir.FullName);

            Assert.NotEmpty(roots);
            Assert.Contains(roots, r => r.Contains("com.acme.MyApp", StringComparison.Ordinal));
            Assert.Contains(roots, r => r.Contains(".config", StringComparison.Ordinal));
        }
        finally
        {
            if (homeDir.Exists)
            {
                homeDir.Delete(true);
            }
        }
    }
}
