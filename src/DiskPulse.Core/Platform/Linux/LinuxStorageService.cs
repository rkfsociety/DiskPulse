namespace Cleaner;

public sealed class LinuxStorageService : IStorageVolumeService
{
    private static readonly (string Name, string[] RelativePaths, string Color)[] RootVolumeCategories =
    [
        ("Система и программы", ["usr", "lib", "lib64", "bin", "sbin", "opt"], "#5B8DEF"),
        ("Пользователи", ["home", "root"], "#38B2AC"),
        ("Данные и кэши", ["var"], "#8576FF"),
        ("Конфигурация", ["etc"], "#EF8E5B")
    ];

    private static readonly (string Name, string[] RelativePaths, string Color)[] DataVolumeCategories =
    [
        ("Данные", ["."], "#5B8DEF")
    ];

    private static readonly HashSet<string> IgnoredFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2",
        "pstore", "securityfs", "debugfs", "tracefs", "fusectl", "configfs",
        "hugetlbfs", "mqueue", "bpf", "squashfs", "overlay", "autofs",
        "ramfs", "rpc_pipefs", "nfsd", "binfmt_misc", "efivarfs", "nsfs",
        "fuse.portal", "fuse.gvfsd-fuse", "iso9660"
    };

    private static readonly string[] IgnoredMountPrefixes =
    [
        "/proc", "/sys", "/dev", "/run", "/snap", "/boot"
    ];

    public string GetSystemDriveRoot() => "/";

    public IReadOnlyList<string> GetDefaultDriveRoots() =>
        GetAvailableVolumes().Select(volume => volume.Root).ToArray();

    public IReadOnlyList<StorageVolumeInfo> GetAvailableVolumes()
    {
        if (File.Exists("/proc/mounts"))
        {
            try
            {
                var lines = File.ReadLines("/proc/mounts");
                var parsed = ParseMountPoints(lines);
                var volumes = BuildVolumesFromMounts(parsed);
                if (volumes.Count > 0)
                {
                    return volumes;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return BuildFallbackVolumes();
    }

    public IReadOnlyList<(string Name, string[] RelativePaths, string Color)> GetUsageCategories(string volumeRoot)
    {
        return string.Equals(CleanerPaths.TrimTrailingSeparator(volumeRoot), "/", StringComparison.Ordinal)
            ? RootVolumeCategories
            : DataVolumeCategories;
    }

    internal static IReadOnlyList<(string Device, string MountPoint, string FileSystem)> ParseMountPoints(IEnumerable<string> procMountLines)
    {
        var result = new List<(string Device, string MountPoint, string FileSystem)>();
        var seenMounts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in procMountLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var device = parts[0];
            var mountPoint = UnescapeMountField(parts[1]);
            var fileSystem = parts[2];

            if (!IsRealMount(device, mountPoint, fileSystem))
            {
                continue;
            }

            if (seenMounts.Add(mountPoint))
            {
                result.Add((device, mountPoint, fileSystem));
            }
        }

        return result;
    }

    internal static bool IsRealMount(string device, string mountPoint, string fileSystem)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || IgnoredFileSystems.Contains(fileSystem))
        {
            return false;
        }

        if (string.Equals(mountPoint, "/", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var prefix in IgnoredMountPrefixes)
        {
            if (string.Equals(mountPoint, prefix, StringComparison.Ordinal) ||
                mountPoint.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                // Разрешаем пользовательские съёмные носители в /run/media/<user>/...
                if (prefix == "/run" && mountPoint.StartsWith("/run/media/", StringComparison.Ordinal))
                {
                    continue;
                }

                return false;
            }
        }

        if (device.StartsWith("/dev/loop", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<StorageVolumeInfo> BuildVolumesFromMounts(
        IReadOnlyList<(string Device, string MountPoint, string FileSystem)> mounts)
    {
        var volumes = new List<StorageVolumeInfo>();
        foreach (var (device, mountPoint, fileSystem) in mounts)
        {
            try
            {
                var drive = new DriveInfo(mountPoint);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var total = Math.Max(0, drive.TotalSize);
                var free = Math.Clamp(drive.AvailableFreeSpace, 0, total);
                var isSystem = string.Equals(mountPoint, "/", StringComparison.Ordinal);
                var label = isSystem ? "Корневой раздел" : Path.GetFileName(mountPoint.TrimEnd('/'));
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = mountPoint;
                }

                var fs = string.IsNullOrWhiteSpace(fileSystem) ? drive.DriveFormat : fileSystem;
                volumes.Add(new StorageVolumeInfo(
                    mountPoint,
                    $"{mountPoint} ({label})",
                    label,
                    fs,
                    total,
                    free,
                    isSystem));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return volumes;
    }

    private static IReadOnlyList<StorageVolumeInfo> BuildFallbackVolumes()
    {
        var volumes = new List<StorageVolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var root = drive.RootDirectory.FullName;
                if (!IsRealMount(root, root, drive.DriveFormat))
                {
                    continue;
                }

                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var isSystem = string.Equals(root, "/", StringComparison.Ordinal);
                var label = isSystem ? "Корневой раздел" : root;
                volumes.Add(new StorageVolumeInfo(
                    root,
                    $"{root} ({label})",
                    label,
                    drive.DriveFormat,
                    Math.Max(0, drive.TotalSize),
                    Math.Clamp(drive.AvailableFreeSpace, 0, drive.TotalSize),
                    isSystem));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (volumes.Count == 0)
        {
            volumes.Add(new StorageVolumeInfo("/", "/ (Корневой раздел)", "Корневой раздел", "ext4", 0, 0, true));
        }

        return volumes;
    }

    private static string UnescapeMountField(string value)
    {
        return value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
    }
}
