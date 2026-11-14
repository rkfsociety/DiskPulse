namespace Cleaner;

internal sealed class LinuxScanRootsProvider : IScanRootsProvider
{
    private static readonly string[] TempProtectedPrefixes =
    [
        "systemd-private-",
        ".X11-unix",
        ".ICE-unix",
        ".Test-unix",
        ".XIM-unix",
        ".font-unix",
        "ssh-",
        "tmux-",
        "pulse-",
        "snap-private-tmp",
        "dbus-",
        "gpg-",
        "orbit-"
    ];

    public IReadOnlyList<ScanRoot> BuildUserCleanupCategorizedRoots(IEnumerable<string> drives)
    {
        var driveSet = NormalizeDriveRoots(drives);
        var candidates = new List<ScanRoot>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheHome = !string.IsNullOrWhiteSpace(xdgCache) ? xdgCache : Path.Combine(home, ".cache");
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrWhiteSpace(xdgConfig) ? xdgConfig : Path.Combine(home, ".config");
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrWhiteSpace(xdgData) ? xdgData : Path.Combine(home, ".local", "share");

        if (Directory.Exists("/tmp") && IsOnSelectedMounts("/tmp", driveSet))
        {
            candidates.Add(new ScanRoot("/tmp", "Временные файлы /tmp"));
        }

        if (Directory.Exists(cacheHome) && IsOnSelectedMounts(cacheHome, driveSet))
        {
            var thumbs = Path.Combine(cacheHome, "thumbnails");
            if (Directory.Exists(thumbs))
            {
                candidates.Add(new ScanRoot(thumbs, "Кэш миниатюр"));
            }

            foreach (var sub in new[] { "mesa_shader_cache", "mesa_shader_cache_db", "nvidia" })
            {
                var path = Path.Combine(cacheHome, sub);
                if (Directory.Exists(path))
                {
                    candidates.Add(new ScanRoot(path, "Кэш шейдеров GPU"));
                }
            }

            // Кэши браузеров Linux
            AddChromiumLinuxBrowserCaches(cacheHome, configHome, candidates);
            AddFirefoxLinuxCaches(cacheHome, Path.Combine(home, ".mozilla", "firefox"), candidates);
        }

        if (Directory.Exists(configHome) && IsOnSelectedMounts(configHome, driveSet))
        {
            foreach (var appName in new[] { "Code", "discord", "Slack" })
            {
                var appRoot = Path.Combine(configHome, appName);
                foreach (var sub in new[] { Path.Combine("Cache", "Cache_Data"), "CachedData", "Code Cache", "GPUCache" })
                {
                    var full = Path.Combine(appRoot, sub);
                    if (Directory.Exists(full))
                    {
                        candidates.Add(new ScanRoot(full, "Кэш приложений"));
                    }
                }
            }
        }

        var apportDir = Path.Combine(dataHome, "apport");
        if (Directory.Exists(apportDir) && IsOnSelectedMounts(apportDir, driveSet))
        {
            candidates.Add(new ScanRoot(apportDir, "Дампы и отчёты о сбоях"));
        }

        return candidates.DistinctBy(r => r.Path, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ScanRoot> BuildSystemCleanupCategorizedRoots(IEnumerable<string> drives)
    {
        var driveSet = NormalizeDriveRoots(drives);
        var candidates = new ScanRoot[]
        {
            new("/var/tmp", "Системные временные файлы /var/tmp"),
            new("/var/crash", "Дампы сбоев (/var/crash)"),
            new("/var/cache/apt/archives", "Кэш пакетов APT"),
            new("/var/cache/pacman/pkg", "Кэш пакетов Pacman"),
            new("/var/cache/dnf", "Кэш пакетов DNF")
        };

        return candidates
            .Where(r => Directory.Exists(r.Path) && IsOnSelectedMounts(r.Path, driveSet))
            .DistinctBy(r => r.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<ScanRoot> BuildDevCacheCategorizedRoots(IEnumerable<string> drives)
    {
        var driveSet = NormalizeDriveRoots(drives);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheHome = !string.IsNullOrWhiteSpace(xdgCache) ? xdgCache : Path.Combine(home, ".cache");
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrWhiteSpace(xdgData) ? xdgData : Path.Combine(home, ".local", "share");

        var candidates = new List<ScanRoot>
        {
            new(Path.Combine(home, ".nuget", "packages"), "Кэш NuGet"),
            new(Path.Combine(dataHome, "NuGet", "v3-cache"), "Кэш NuGet"),
            new(Path.Combine(dataHome, "NuGet", "plugins-cache"), "Кэш NuGet"),
            new(Path.Combine(home, ".npm"), "Кэш npm"),
            new(Path.Combine(cacheHome, "npm"), "Кэш npm"),
            new(Path.Combine(cacheHome, "yarn"), "Кэш Yarn"),
            new(Path.Combine(dataHome, "pnpm", "store"), "Кэш pnpm"),
            new(Path.Combine(cacheHome, "pnpm"), "Кэш pnpm"),
            new(Path.Combine(cacheHome, "pip"), "Кэш pip"),
            new(Path.Combine(cacheHome, "go-build"), "Кэш Go")
        };

        return candidates
            .Where(r => Directory.Exists(r.Path) && IsOnSelectedMounts(r.Path, driveSet))
            .DistinctBy(r => r.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public List<ScanFile> ScanExtraUserFiles(IEnumerable<string> drives, CancellationToken cancellationToken) => [];

    public IReadOnlyList<string> GetExtraUserDeleteRoots(IEnumerable<string> drives) => [];

    public bool IsAllowedExtraUserFile(string fullPath, string root) => true;

    public bool ShouldIncludeScannedFile(string rootPath, string fullPath, string fileName)
    {
        var normalizedRoot = CleanerPaths.TrimTrailingSeparator(rootPath);
        if (normalizedRoot is "/tmp" or "/var/tmp")
        {
            var relative = Path.GetRelativePath(normalizedRoot, fullPath);
            if (IsProtectedTempRelativePath(relative))
            {
                return false;
            }
        }

        // В apt/archives не трогаем lock-файл
        if (normalizedRoot == "/var/cache/apt/archives" &&
            string.Equals(fileName, "lock", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    internal static bool IsProtectedTempRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        var firstSegment = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(firstSegment))
        {
            return true;
        }

        foreach (var prefix in TempProtectedPrefixes)
        {
            if (firstSegment.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddChromiumLinuxBrowserCaches(string cacheHome, string configHome, List<ScanRoot> candidates)
    {
        var browserSpecs = new (string DirName, string Category)[]
        {
            ("google-chrome", "Кэш Chrome"),
            ("chromium", "Кэш Chromium"),
            ("microsoft-edge", "Кэш Edge"),
            (Path.Combine("BraveSoftware", "Brave-Browser"), "Кэш Brave"),
            ("vivaldi", "Кэш Vivaldi"),
            ("opera", "Кэш Opera"),
            ("yandex-browser", "Кэш Яндекс Браузера")
        };

        foreach (var (dirName, category) in browserSpecs)
        {
            var browserCacheRoot = Path.Combine(cacheHome, dirName);
            if (Directory.Exists(browserCacheRoot))
            {
                try
                {
                    foreach (var profile in Directory.EnumerateDirectories(browserCacheRoot))
                    {
                        foreach (var sub in new[] { "Cache", Path.Combine("Cache", "Cache_Data"), "Code Cache" })
                        {
                            var path = Path.Combine(profile, sub);
                            if (Directory.Exists(path))
                            {
                                candidates.Add(new ScanRoot(path, category));
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var browserConfigRoot = Path.Combine(configHome, dirName);
            if (Directory.Exists(browserConfigRoot))
            {
                try
                {
                    foreach (var profile in Directory.EnumerateDirectories(browserConfigRoot))
                    {
                        var gpuCache = Path.Combine(profile, "GPUCache");
                        if (Directory.Exists(gpuCache))
                        {
                            candidates.Add(new ScanRoot(gpuCache, category));
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void AddFirefoxLinuxCaches(string cacheHome, string mozillaRoot, List<ScanRoot> candidates)
    {
        var firefoxCacheRoot = Path.Combine(cacheHome, "mozilla", "firefox");
        foreach (var profileParent in new[] { firefoxCacheRoot, mozillaRoot })
        {
            if (!Directory.Exists(profileParent))
            {
                continue;
            }

            try
            {
                foreach (var profile in Directory.EnumerateDirectories(profileParent))
                {
                    var cache2 = Path.Combine(profile, "cache2", "entries");
                    if (Directory.Exists(cache2))
                    {
                        candidates.Add(new ScanRoot(cache2, "Кэш Firefox"));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IReadOnlyList<string> NormalizeDriveRoots(IEnumerable<string> drives) =>
        drives
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(CleanerPaths.TrimTrailingSeparator)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool IsOnSelectedMounts(string path, IReadOnlyList<string> selectedMounts)
    {
        if (selectedMounts.Count == 0)
        {
            return false;
        }

        if (selectedMounts.Contains("/", StringComparer.Ordinal))
        {
            return true;
        }

        var normalized = CleanerPaths.TrimTrailingSeparator(path);
        return selectedMounts.Any(mount =>
            string.Equals(normalized, mount, StringComparison.Ordinal) ||
            normalized.StartsWith(mount + "/", StringComparison.Ordinal));
    }
}
