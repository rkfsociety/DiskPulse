using System.IO.Enumeration;
using System.Text.Json;

namespace Cleaner;

/// <summary>Считает крупные категории на дисках или разделах для диаграммы обзора.</summary>
public sealed class DiskUsageService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly string _cachePath;
    private readonly IStorageVolumeService _storageService;

    public DiskUsageService()
        : this(PlatformServices.CreateStorageVolumeService(), CleanerPaths.GetDefaultStandaloneDataDirectory())
    {
    }

    public DiskUsageService(IStorageVolumeService storageService, string dataDirectory)
    {
        _storageService = storageService;
        _cachePath = Path.Combine(dataDirectory, "disk-usage-cache.json");
    }

    public Task<DiskUsageSnapshot?> ReadAsync(IEnumerable<string> driveRoots, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var roots = driveRoots.Distinct(CleanerPaths.PathComparer).ToArray();
        return Task.Run(() => ReadCached(roots, cancellationToken, forceRefresh), cancellationToken);
    }

    private DiskUsageSnapshot? ReadCached(IReadOnlyList<string> driveRoots, CancellationToken cancellationToken, bool forceRefresh)
    {
        var signature = TryBuildSignature(driveRoots);
        if (!forceRefresh && signature is not null && TryReadCache(signature, out var cached))
        {
            return cached with { FromCache = true };
        }

        var snapshot = Read(driveRoots, _storageService, cancellationToken);
        if (snapshot is not null && signature is not null && !cancellationToken.IsCancellationRequested)
        {
            TryWriteCache(signature, snapshot);
        }

        return snapshot;
    }

    internal static DiskUsageSnapshot? Read(IEnumerable<string> driveRoots, CancellationToken cancellationToken) =>
        Read(driveRoots, PlatformServices.CreateStorageVolumeService(), cancellationToken);

    internal static DiskUsageSnapshot? Read(
        IEnumerable<string> driveRoots,
        IStorageVolumeService storageService,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        long totalFreeBytes = 0;
        var skippedDrives = 0;
        var driveSummaries = new List<DiskUsageDriveSummary>();
        var orderedCategories = new List<(string Name, string Color)>();
        var categoryBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var driveRoot in driveRoots.Distinct(CleanerPaths.PathComparer))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var free = Math.Clamp(drive.AvailableFreeSpace, 0, drive.TotalSize);
                var actualRoot = drive.RootDirectory.FullName;
                driveSummaries.Add(new DiskUsageDriveSummary(actualRoot, drive.TotalSize, free));
                totalBytes += drive.TotalSize;
                totalFreeBytes += free;

                var categories = storageService.GetUsageCategories(actualRoot);
                foreach (var cat in categories)
                {
                    if (!categoryBytes.ContainsKey(cat.Name))
                    {
                        categoryBytes[cat.Name] = 0L;
                        orderedCategories.Add((cat.Name, cat.Color));
                    }
                }

                var computed = categories
                    .AsParallel()
                    .WithCancellation(cancellationToken)
                    .Select(category => (
                        category.Name,
                        Bytes: category.RelativePaths.Sum(relative =>
                        {
                            var full = relative == "." ? actualRoot : Path.Combine(actualRoot, relative);
                            return relative == "." ? 0L : GetDirectoryBytes(full, cancellationToken);
                        })))
                    .ToDictionary(item => item.Name, item => item.Bytes, StringComparer.OrdinalIgnoreCase);

                long knownBytes = 0;
                foreach (var category in categories)
                {
                    var bytes = Math.Min(computed[category.Name], Math.Max(0, drive.TotalSize - free - knownBytes));
                    knownBytes += bytes;
                    categoryBytes[category.Name] += bytes;
                }
            }
            catch (IOException) { skippedDrives++; }
            catch (UnauthorizedAccessException) { skippedDrives++; }
            catch (ArgumentException) { skippedDrives++; }
        }

        if (totalBytes <= 0)
        {
            return null;
        }

        var segments = orderedCategories
            .Select(category => new DiskUsageSegment(category.Name, categoryBytes[category.Name], category.Color))
            .ToList();
        var knownTotal = segments.Sum(segment => segment.Bytes);
        segments.Add(new DiskUsageSegment("Прочее", Math.Max(0, totalBytes - totalFreeBytes - knownTotal), "#64748B"));
        segments.Add(new DiskUsageSegment("Свободно", totalFreeBytes, "#34D399"));
        return new DiskUsageSnapshot(totalBytes, segments, skippedDrives, driveSummaries);
    }

    private string? TryBuildSignature(IEnumerable<string> driveRoots)
    {
        try
        {
            var values = driveRoots.Select(root =>
            {
                var drive = new DriveInfo(root);
                const long cacheBucket = 64L * 1024 * 1024;
                var freeBucket = drive.AvailableFreeSpace / cacheBucket;
                return $"{drive.RootDirectory.FullName}|{drive.TotalSize}|{freeBucket}";
            });
            return string.Join(";", values.OrderBy(value => value, CleanerPaths.PathComparer));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private bool TryReadCache(string signature, out DiskUsageSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            if (!File.Exists(_cachePath))
            {
                return false;
            }

            var cache = JsonSerializer.Deserialize<DiskUsageCache>(File.ReadAllText(_cachePath));
            if (cache is null ||
                !string.Equals(cache.Signature, signature, StringComparison.Ordinal) ||
                DateTimeOffset.UtcNow - cache.CapturedAt > CacheLifetime)
            {
                return false;
            }

            snapshot = cache.Snapshot;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void TryWriteCache(string signature, DiskUsageSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var cache = new DiskUsageCache(signature, DateTimeOffset.UtcNow, snapshot);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record DiskUsageCache(string Signature, DateTimeOffset CapturedAt, DiskUsageSnapshot Snapshot);

    private static readonly EnumerationOptions FastEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    internal static long GetDirectoryBytes(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            var enumerable = new FileSystemEnumerable<long>(
                path,
                (ref FileSystemEntry entry) => entry.IsDirectory ? 0L : entry.Length,
                FastEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };

            var count = 0;
            foreach (var length in enumerable)
            {
                if ((++count & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                total += length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return total;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
