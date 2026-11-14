using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Cleaner;

public sealed class CleanerScanService
{
    private readonly IRecycleBinService _recycleBinService;
    private readonly ISafeDeleteService _safeDeleteService;
    private readonly IScanRootsProvider _scanRootsProvider;

    public CleanerScanService()
        : this(
            PlatformServices.CreateRecycleBinService(),
            PlatformServices.CreateSafeDeleteService(),
            PlatformServices.CreateScanRootsProvider())
    {
    }

    internal CleanerScanService(IRecycleBinService recycleBinService)
        : this(
            recycleBinService,
            PlatformServices.CreateSafeDeleteService(),
            PlatformServices.CreateScanRootsProvider())
    {
    }

    internal CleanerScanService(
        IRecycleBinService recycleBinService,
        ISafeDeleteService safeDeleteService,
        IScanRootsProvider scanRootsProvider)
    {
        _recycleBinService = recycleBinService;
        _safeDeleteService = safeDeleteService;
        _scanRootsProvider = scanRootsProvider;
    }

    public RecycleBinInfo GetRecycleBinInfo(IEnumerable<string> selectedDrives) =>
        _recycleBinService.GetInfo(NormalizeDrives(selectedDrives));

    internal static bool HasRecycleBinChanged(RecycleBinInfo scanned, RecycleBinInfo current) => scanned != current;

    public Task<ScanResult> ScanAsync(
        IEnumerable<string> selectedDrives,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null,
        IReadOnlyList<string>? permanentExclusions = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var userRoots = _scanRootsProvider.BuildUserCleanupCategorizedRoots(drives);
            var userTemp = ScanMany(userRoots, "Временные файлы и кэш пользователя", cancellationToken, progress, _scanRootsProvider);
            var extraUserFiles = _scanRootsProvider.ScanExtraUserFiles(drives, cancellationToken);
            if (extraUserFiles.Count > 0)
            {
                userTemp.AddRange(extraUserFiles);
            }

            var systemRoots = _scanRootsProvider.BuildSystemCleanupCategorizedRoots(drives);
            var windowsTemp = ScanMany(systemRoots, "Системные временные файлы", cancellationToken, progress, _scanRootsProvider);

            var devRoots = _scanRootsProvider.BuildDevCacheCategorizedRoots(drives);
            var devCaches = ScanMany(devRoots, "Кэш инструментов разработки", cancellationToken, progress, _scanRootsProvider);

            if (permanentExclusions is { Count: > 0 })
            {
                userTemp = userTemp.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
                windowsTemp = windowsTemp.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
                devCaches = devCaches.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
            }

            var totalCount = userTemp.Count + windowsTemp.Count + devCaches.Count;
            var totalBytes = userTemp.Sum(file => file.Bytes) + windowsTemp.Sum(file => file.Bytes) + devCaches.Sum(file => file.Bytes);
            progress?.Report(new ScanProgress("Корзина", totalCount, totalBytes));
            return new ScanResult(userTemp, windowsTemp, GetRecycleBinInfo(drives), DateTimeOffset.Now, devCaches);
        }, cancellationToken);
    }

    public Task<CleanupResult> DeleteAsync(
        ScanResult result,
        IEnumerable<string> selectedDrives,
        bool deleteUserTemp,
        bool deleteWindowsTemp,
        bool deleteRecycleBin,
        int minimumAgeHours,
        CancellationToken cancellationToken = default,
        IProgress<CleanupProgress>? progress = null,
        IReadOnlySet<string>? excludedPaths = null,
        bool deleteDevCaches = false,
        IReadOnlyList<string>? permanentExclusions = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var deleted = 0;
            var skipped = 0;
            long reclaimed = 0;
            var skipReasons = new Dictionary<CleanupSkipReason, int>();

            bool IsFileExcluded(ScanFile file) =>
                (excludedPaths is { Count: > 0 } && excludedPaths.Contains(file.Path)) ||
                (permanentExclusions is { Count: > 0 } && ExclusionListService.IsExcluded(file.Path, permanentExclusions));

            if (deleteUserTemp)
            {
                var userFiles = result.UserTempFiles.Where(file => !IsFileExcluded(file)).ToArray();
                var userRoots = _scanRootsProvider.BuildUserCleanupCategorizedRoots(drives).Select(r => r.Path).ToList();
                foreach (var extra in _scanRootsProvider.GetExtraUserDeleteRoots(drives))
                {
                    userRoots.Add(extra);
                }

                var stats = DeleteFilesCore(
                    userFiles,
                    userRoots,
                    minimumAgeHours,
                    _safeDeleteService,
                    _scanRootsProvider,
                    cancellationToken,
                    progress,
                    "Временные файлы и кэш пользователя",
                    deleted,
                    skipped,
                    reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
            }

            if (deleteWindowsTemp)
            {
                var systemFiles = result.WindowsTempFiles.Where(file => !IsFileExcluded(file)).ToArray();
                var systemRoots = _scanRootsProvider.BuildSystemCleanupCategorizedRoots(drives).Select(r => r.Path).ToArray();
                var stats = DeleteFilesCore(
                    systemFiles,
                    systemRoots,
                    minimumAgeHours,
                    _safeDeleteService,
                    _scanRootsProvider,
                    cancellationToken,
                    progress,
                    "Системные временные файлы",
                    deleted,
                    skipped,
                    reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
            }

            if (deleteDevCaches && result.DevCacheFilesOrEmpty.Count > 0)
            {
                var devFiles = result.DevCacheFilesOrEmpty.Where(file => !IsFileExcluded(file)).ToArray();
                var devRoots = _scanRootsProvider.BuildDevCacheCategorizedRoots(drives).Select(r => r.Path).ToArray();
                var stats = DeleteFilesCore(
                    devFiles,
                    devRoots,
                    minimumAgeHours,
                    _safeDeleteService,
                    _scanRootsProvider,
                    cancellationToken,
                    progress,
                    "Кэш инструментов разработки",
                    deleted,
                    skipped,
                    reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
            }

            if (deleteRecycleBin)
            {
                var beforeEmpty = _recycleBinService.GetInfoPerRoot(drives);
                foreach (var root in drives)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var succeeded = _recycleBinService.Empty(root);
                    var info = beforeEmpty.TryGetValue(root, out var value) ? value : new RecycleBinInfo(0, 0);
                    if (succeeded)
                    {
                        deleted += info.Items;
                        reclaimed += info.Bytes;
                    }
                    else
                    {
                        skipped += info.Items;
                        if (info.Items > 0)
                        {
                            skipReasons[CleanupSkipReason.Unknown] = skipReasons.GetValueOrDefault(CleanupSkipReason.Unknown) + info.Items;
                        }
                    }

                    progress?.Report(new CleanupProgress("Корзина", deleted, skipped, reclaimed));
                }
            }

            return new CleanupResult(deleted, skipped, reclaimed, skipReasons);
        }, cancellationToken);
    }

    internal static IReadOnlyList<string> NormalizeDrives(IEnumerable<string> drives)
    {
        return drives
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => OperatingSystem.IsWindows() ? (Path.GetPathRoot(root) ?? root) : CleanerPaths.TrimTrailingSeparator(root))
            .Distinct(CleanerPaths.PathComparer)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildWindowsTempRoots(IEnumerable<string> drives) =>
        BuildWindowsTempCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildWindowsTempCategorizedRoots(IEnumerable<string> drives) =>
        PlatformServices.CreateScanRootsProvider().BuildSystemCleanupCategorizedRoots(drives);

    internal static IReadOnlyList<string> BuildUserCleanupRoots(IEnumerable<string> drives) =>
        BuildUserCleanupCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildUserCleanupCategorizedRoots(IEnumerable<string> drives) =>
        PlatformServices.CreateScanRootsProvider().BuildUserCleanupCategorizedRoots(drives);

    internal static IReadOnlyList<string> BuildDevCacheRoots(IEnumerable<string> drives) =>
        BuildDevCacheCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildDevCacheCategorizedRoots(IEnumerable<string> drives) =>
        PlatformServices.CreateScanRootsProvider().BuildDevCacheCategorizedRoots(drives);

    internal static string GetExplorerThumbCacheDirectory() =>
        WindowsScanRootsProvider.GetExplorerThumbCacheDirectory();

    internal static List<ScanFile> ScanExplorerThumbCache(IEnumerable<string> drives, CancellationToken cancellationToken) =>
        new WindowsScanRootsProvider().ScanExtraUserFiles(drives, cancellationToken);

    internal static bool IsThumbcacheFileName(ReadOnlySpan<char> fileName) =>
        WindowsScanRootsProvider.IsThumbcacheFileName(fileName);

    private static List<ScanFile> ScanMany(
        IEnumerable<ScanRoot> roots,
        string stage,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress,
        IScanRootsProvider rootsProvider)
    {
        var rootList = roots as IReadOnlyList<ScanRoot> ?? roots.ToArray();
        if (rootList.Count == 0)
        {
            return [];
        }

        var buckets = new ConcurrentBag<List<ScanFile>>();
        var totalFiles = 0;
        var totalBytes = 0L;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(rootList, options, root =>
        {
            var localFiles = ScanDirectory(
                root.Path,
                root.Category,
                cancellationToken,
                bytes =>
                {
                    var filesCount = Interlocked.Increment(ref totalFiles);
                    var bytesCount = Interlocked.Add(ref totalBytes, bytes);
                    if ((filesCount & 127) == 0)
                    {
                        progress?.Report(new ScanProgress(stage, filesCount, bytesCount));
                    }
                },
                rootsProvider);

            if (localFiles.Count > 0)
            {
                buckets.Add(localFiles);
            }
        });

        var files = new List<ScanFile>(totalFiles);
        foreach (var bucket in buckets)
        {
            files.AddRange(bucket);
        }

        return files;
    }

    private static readonly EnumerationOptions ScanEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    internal static List<ScanFile> ScanDirectory(
        string path,
        string category,
        CancellationToken cancellationToken,
        Action<long>? onFileFound = null,
        IScanRootsProvider? rootsProvider = null)
    {
        var files = new List<ScanFile>();
        if (!Directory.Exists(path))
        {
            return files;
        }

        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return files;
            }

            var enumerable = new FileSystemEnumerable<ScanFile>(
                path,
                (ref FileSystemEntry entry) => new ScanFile(entry.ToFullPath(), entry.Length, category, entry.LastWriteTimeUtc.UtcDateTime),
                ScanEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory &&
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    (rootsProvider is null || rootsProvider.ShouldIncludeScannedFile(path, entry.ToFullPath(), entry.FileName.ToString())),
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    (rootsProvider is null || rootsProvider.ShouldIncludeScannedFile(path, entry.ToFullPath(), entry.FileName.ToString()))
            };

            foreach (var file in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
                onFileFound?.Invoke(file.Bytes);
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

    internal static CleanupResult DeleteFiles(
        IEnumerable<ScanFile> files,
        IEnumerable<string> roots,
        int minimumAgeHours,
        CancellationToken cancellationToken,
        IProgress<CleanupProgress>? progress = null,
        string stage = "Очистка",
        int deletedBefore = 0,
        int skippedBefore = 0,
        long reclaimedBefore = 0)
    {
        return DeleteFilesCore(
            files,
            roots,
            minimumAgeHours,
            PlatformServices.CreateSafeDeleteService(),
            PlatformServices.CreateScanRootsProvider(),
            cancellationToken,
            progress,
            stage,
            deletedBefore,
            skippedBefore,
            reclaimedBefore);
    }

    internal static CleanupResult DeleteFilesCore(
        IEnumerable<ScanFile> files,
        IEnumerable<string> roots,
        int minimumAgeHours,
        ISafeDeleteService safeDeleteService,
        IScanRootsProvider scanRootsProvider,
        CancellationToken cancellationToken,
        IProgress<CleanupProgress>? progress = null,
        string stage = "Очистка",
        int deletedBefore = 0,
        int skippedBefore = 0,
        long reclaimedBefore = 0)
    {
        var fullRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(CleanerPaths.EnsureTrailingSeparator)
            .Distinct(CleanerPaths.PathComparer)
            .ToArray();
        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        var skipReasons = new Dictionary<CleanupSkipReason, int>();
        var touchedDirectories = new HashSet<string>(CleanerPaths.PathComparer);
        DateTime? ageThresholdUtc = minimumAgeHours > 0 ? DateTime.UtcNow.AddHours(-minimumAgeHours) : null;

        void RecordSkip(CleanupSkipReason reason)
        {
            skipped++;
            skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                var deletionRoot = fullRoots.FirstOrDefault(root => fullPath.StartsWith(root, CleanerPaths.PathComparison));
                if (deletionRoot is null)
                {
                    RecordSkip(CleanupSkipReason.AccessDenied);
                    continue;
                }

                if (!scanRootsProvider.IsAllowedExtraUserFile(fullPath, deletionRoot))
                {
                    RecordSkip(CleanupSkipReason.AccessDenied);
                    continue;
                }

                if (ageThresholdUtc is { } threshold && file.LastWriteTimeUtc is { } cachedLastWriteUtc && cachedLastWriteUtc > threshold)
                {
                    RecordSkip(CleanupSkipReason.TooNew);
                    continue;
                }

                var attempt = safeDeleteService.TryDelete(fullPath, deletionRoot, ageThresholdUtc);
                if (!attempt.Succeeded)
                {
                    var reason = attempt.Reason == CleanupSkipReason.Changed && ageThresholdUtc is not null
                        ? CleanupSkipReason.TooNew
                        : attempt.Reason;
                    RecordSkip(reason);
                    continue;
                }

                deleted++;
                reclaimed += file.Bytes;
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    touchedDirectories.Add(parentDir);
                }
            }
            catch (UnauthorizedAccessException)
            {
                RecordSkip(CleanupSkipReason.AccessDenied);
            }
            catch (IOException ex)
            {
                var win32 = ex.HResult & 0xFFFF;
                RecordSkip(win32 is 32 or 33 ? CleanupSkipReason.FileInUse : CleanupSkipReason.Unknown);
            }

            if ((deleted + skipped) % 32 == 0)
            {
                progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
            }
        }

        RemoveEmptySubdirectories(touchedDirectories, fullRoots);
        progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
        return new CleanupResult(deleted, skipped, reclaimed, skipReasons);
    }

    internal static void MergeSkipReasons(Dictionary<CleanupSkipReason, int> target, IReadOnlyDictionary<CleanupSkipReason, int>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (reason, count) in source)
        {
            if (count > 0 && reason != CleanupSkipReason.None)
            {
                target[reason] = target.GetValueOrDefault(reason) + count;
            }
        }
    }

    public static string FormatSkipReasons(IReadOnlyDictionary<CleanupSkipReason, int>? skipReasons, int minimumAgeHours = 24)
    {
        if (skipReasons is null || skipReasons.Count == 0)
        {
            return string.Empty;
        }

        var parts = skipReasons
            .Where(pair => pair.Value > 0 && pair.Key != CleanupSkipReason.None)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{FormatSkipReasonLabel(pair.Key, minimumAgeHours)}: {pair.Value:N0}")
            .ToArray();

        return parts.Length > 0 ? string.Join(", ", parts) : string.Empty;
    }

    private static string FormatSkipReasonLabel(CleanupSkipReason reason, int minimumAgeHours) => reason switch
    {
        CleanupSkipReason.TooNew => minimumAgeHours > 0 ? $"младше {minimumAgeHours} ч" : "слишком свежие",
        CleanupSkipReason.FileInUse => "занято процессами",
        CleanupSkipReason.AccessDenied => "нет прав",
        CleanupSkipReason.ReparsePoint => "ссылки/junction",
        CleanupSkipReason.NotFound => "уже удалены",
        CleanupSkipReason.Changed => "изменены после проверки",
        _ => "прочие"
    };

    internal static void RemoveEmptySubdirectories(IEnumerable<string> directories, IReadOnlyList<string> fullRoots)
    {
        var candidates = new HashSet<string>(CleanerPaths.PathComparer);
        foreach (var directory in directories)
        {
            var current = directory;
            while (!string.IsNullOrEmpty(current))
            {
                var normalized = CleanerPaths.EnsureTrailingSeparator(current);
                if (fullRoots.Any(root => string.Equals(normalized, root, CleanerPaths.PathComparison)) ||
                    !fullRoots.Any(root => normalized.StartsWith(root, CleanerPaths.PathComparison)))
                {
                    break;
                }

                candidates.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(candidate) &&
                    !File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint) &&
                    !Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    Directory.Delete(candidate, recursive: false);
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

    internal static bool IsSafePathForDeletion(string fullPath, IEnumerable<string> fullRoots)
    {
        var root = fullRoots.FirstOrDefault(candidate => fullPath.StartsWith(candidate, CleanerPaths.PathComparison));
        if (root is null)
        {
            return false;
        }

        try
        {
            var current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                var normalizedCurrent = CleanerPaths.EnsureTrailingSeparator(current);
                if (string.Equals(normalizedCurrent, root, CleanerPaths.PathComparison))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }

            return !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
