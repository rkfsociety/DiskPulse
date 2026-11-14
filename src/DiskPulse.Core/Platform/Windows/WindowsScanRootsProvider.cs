using System.IO.Enumeration;

namespace Cleaner;

internal sealed class WindowsScanRootsProvider : IScanRootsProvider
{
    public IReadOnlyList<ScanRoot> BuildUserCleanupCategorizedRoots(IEnumerable<string> drives)
    {
        var roots = new List<ScanRoot>();
        if (IsOnSelectedDrive(Path.GetTempPath(), drives))
        {
            roots.Add(new ScanRoot(Path.GetTempPath(), "Временные файлы и кэш пользователя"));
            roots.AddRange(BuildUserDiagnosticAndCacheRoots());
            roots.AddRange(BuildBrowserCacheRoots());
        }

        return roots.DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<ScanRoot> BuildSystemCleanupCategorizedRoots(IEnumerable<string> drives)
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsRoot = string.IsNullOrEmpty(windowsFolder) ? null : Path.GetPathRoot(windowsFolder);
        if (windowsRoot is null || !drives.Contains(windowsRoot, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var candidates = new ScanRoot[]
        {
            new(Path.Combine(windowsFolder, "Temp"), "Системные временные файлы"),
            new(Path.Combine(windowsFolder, "Minidump"), "Дампы сбоев"),
            new(Path.Combine(windowsFolder, "SoftwareDistribution", "Download"), "Кэш обновлений Windows"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive"), "Отчёты об ошибках WER"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue"), "Отчёты об ошибках WER"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "Temp"), "Отчёты об ошибках WER")
        };

        return candidates
            .Where(root => Directory.Exists(root.Path))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ScanRoot> BuildDevCacheCategorizedRoots(IEnumerable<string> drives)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<ScanRoot>
        {
            new(Path.Combine(localAppData, "NuGet", "v3-cache"), "Кэш NuGet"),
            new(Path.Combine(localAppData, "NuGet", "plugins-cache"), "Кэш NuGet"),
            new(Path.Combine(userProfile, ".nuget", "packages"), "Кэш NuGet"),
            new(Path.Combine(localAppData, "npm-cache"), "Кэш npm"),
            new(Path.Combine(appData, "npm-cache"), "Кэш npm"),
            new(Path.Combine(localAppData, "Yarn", "Cache"), "Кэш Yarn"),
            new(Path.Combine(localAppData, "pnpm", "store"), "Кэш pnpm"),
            new(Path.Combine(localAppData, "pnpm-cache"), "Кэш pnpm"),
            new(Path.Combine(localAppData, "pip", "cache"), "Кэш pip")
        };

        return candidates
            .Where(root => IsOnSelectedDrive(root.Path, drives) && Directory.Exists(root.Path))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public List<ScanFile> ScanExtraUserFiles(IEnumerable<string> drives, CancellationToken cancellationToken)
    {
        var explorerDir = GetExplorerThumbCacheDirectory();
        if (!IsOnSelectedDrive(explorerDir, drives) || !Directory.Exists(explorerDir))
        {
            return [];
        }

        var files = new List<ScanFile>();
        try
        {
            if (File.GetAttributes(explorerDir).HasFlag(FileAttributes.ReparsePoint))
            {
                return files;
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var enumerable = new FileSystemEnumerable<ScanFile>(
                explorerDir,
                (ref FileSystemEntry entry) => new ScanFile(entry.ToFullPath(), entry.Length, "Кэш миниатюр Проводника", entry.LastWriteTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory &&
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    IsThumbcacheFileName(entry.FileName)
            };

            foreach (var file in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return files;
    }

    public IReadOnlyList<string> GetExtraUserDeleteRoots(IEnumerable<string> drives)
    {
        var explorerDir = GetExplorerThumbCacheDirectory();
        return IsOnSelectedDrive(explorerDir, drives) && Directory.Exists(explorerDir)
            ? [explorerDir]
            : [];
    }

    public bool IsAllowedExtraUserFile(string fullPath, string root)
    {
        var explorerRoot = CleanerPaths.EnsureTrailingSeparator(GetExplorerThumbCacheDirectory());
        if (string.Equals(root, explorerRoot, StringComparison.OrdinalIgnoreCase))
        {
            return IsThumbcacheFileName(Path.GetFileName(fullPath));
        }

        return true;
    }

    public bool ShouldIncludeScannedFile(string rootPath, string fullPath, string fileName) => true;

    internal static string GetExplorerThumbCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
    }

    internal static bool IsThumbcacheFileName(ReadOnlySpan<char> fileName) =>
        fileName.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private static bool IsOnSelectedDrive(string path, IEnumerable<string> drives)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return root is not null && drives.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ScanRoot> BuildUserDiagnosticAndCacheRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<ScanRoot>
        {
            new(Path.Combine(localAppData, "CrashDumps"), "Дампы сбоев"),
            new(Path.Combine(localAppData, "D3DSCache"), "Кэш DirectX шейдеров"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"), "Временный веб-кэш Windows"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"), "Отчёты об ошибках WER"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"), "Отчёты об ошибках WER"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "Temp"), "Отчёты об ошибках WER")
        };

        var appCacheFolders = new[]
        {
            Path.Combine(appData, "Code"),
            Path.Combine(appData, "discord"),
            Path.Combine(appData, "Slack")
        };

        foreach (var appRoot in appCacheFolders)
        {
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "Cache", "Cache_Data"), "Кэш приложений"));
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "Code Cache"), "Кэш приложений"));
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "GPUCache"), "Кэш приложений"));
        }

        return candidates.Where(root => Directory.Exists(root.Path)).ToArray();
    }

    private static IReadOnlyList<ScanRoot> BuildBrowserCacheRoots()
    {
        var roots = new List<ScanRoot>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var chromiumUserDataRoots = new (string Path, string Category)[]
        {
            (Path.Combine(localAppData, "Google", "Chrome", "User Data"), "Кэш Chrome"),
            (Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), "Кэш Edge"),
            (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), "Кэш Brave"),
            (Path.Combine(localAppData, "Vivaldi", "User Data"), "Кэш Vivaldi"),
            (Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data"), "Кэш Яндекс Браузера")
        };

        foreach (var (browserRoot, category) in chromiumUserDataRoots.Where(entry => Directory.Exists(entry.Path)))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(browserRoot))
                {
                    roots.AddRange(new[]
                    {
                        Path.Combine(profile, "Cache", "Cache_Data"),
                        Path.Combine(profile, "Code Cache"),
                        Path.Combine(profile, "GPUCache")
                    }
                    .Where(Directory.Exists)
                    .Select(path => new ScanRoot(path, category)));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        var operaRoots = new[]
        {
            Path.Combine(localAppData, "Opera Software", "Opera Stable"),
            Path.Combine(localAppData, "Opera Software", "Opera GX Stable"),
            Path.Combine(appData, "Opera Software", "Opera Stable"),
            Path.Combine(appData, "Opera Software", "Opera GX Stable")
        };

        foreach (var operaRoot in operaRoots.Where(Directory.Exists))
        {
            roots.AddRange(new[]
            {
                Path.Combine(operaRoot, "Cache", "Cache_Data"),
                Path.Combine(operaRoot, "Code Cache"),
                Path.Combine(operaRoot, "GPUCache")
            }
            .Where(Directory.Exists)
            .Select(path => new ScanRoot(path, "Кэш Opera")));
        }

        var firefoxProfiles = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles))
                {
                    var cache = Path.Combine(profile, "cache2", "entries");
                    if (Directory.Exists(cache))
                    {
                        roots.Add(new ScanRoot(cache, "Кэш Firefox"));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return roots;
    }
}
