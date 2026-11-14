namespace Cleaner;

public static class WindowsDriveService
{
    public static string GetSystemDriveRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/";
        }

        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
    }

    public static IReadOnlyList<string> GetFixedDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ["/"];
        }

        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return drives.Length > 0 ? drives : [GetSystemDriveRoot()];
    }
}

public sealed class WindowsStorageVolumeService : IStorageVolumeService
{
    private static readonly (string Name, string[] RelativePaths, string Color)[] WindowsCategories =
    [
        ("Windows", ["Windows"], "#5B8DEF"),
        ("Программы", ["Program Files", "Program Files (x86)"], "#EF8E5B"),
        ("Пользователи", ["Users"], "#38B2AC"),
        ("Данные программ", ["ProgramData"], "#8576FF")
    ];

    public string GetSystemDriveRoot() => WindowsDriveService.GetSystemDriveRoot();

    public IReadOnlyList<string> GetDefaultDriveRoots() => WindowsDriveService.GetFixedDriveRoots();

    public IReadOnlyList<StorageVolumeInfo> GetAvailableVolumes()
    {
        var systemRoot = GetSystemDriveRoot();
        var volumes = new List<StorageVolumeInfo>();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            try
            {
                var root = drive.RootDirectory.FullName;
                var total = Math.Max(0, drive.TotalSize);
                var free = Math.Clamp(drive.AvailableFreeSpace, 0, total);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel.Trim();
                var shortRoot = root.TrimEnd(Path.DirectorySeparatorChar);
                var isSystem = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase);
                volumes.Add(new StorageVolumeInfo(
                    root,
                    $"{shortRoot} ({label})",
                    label,
                    drive.DriveFormat,
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
        }

        if (volumes.Count == 0)
        {
            volumes.Add(new StorageVolumeInfo(systemRoot, systemRoot.TrimEnd('\\'), "Системный диск", "NTFS", 0, 0, true));
        }

        return volumes;
    }

    public IReadOnlyList<(string Name, string[] RelativePaths, string Color)> GetUsageCategories(string volumeRoot) =>
        WindowsCategories;
}
