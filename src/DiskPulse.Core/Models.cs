using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Cleaner;

public enum CleanupSkipReason
{
    None,
    AccessDenied,
    FileInUse,
    NotFound,
    ReparsePoint,
    Changed,
    TooNew,
    Unknown
}

public sealed record ScanFile(string Path, long Bytes, string Category = "", DateTime? LastWriteTimeUtc = null);
internal sealed record ScanRoot(string Path, string Category);
public sealed record CleanupResult(int DeletedFiles, int SkippedFiles, long ReclaimedBytes, IReadOnlyDictionary<CleanupSkipReason, int>? SkipReasons = null);
public sealed record ScanProgress(string Stage, int FilesFound, long BytesFound);
public sealed record CleanupProgress(string Stage, int DeletedFiles, int SkippedFiles, long ReclaimedBytes);

public sealed record RecycleBinInfo(long Bytes, int Items);

public sealed record ScanResult(
    IReadOnlyList<ScanFile> UserTempFiles,
    IReadOnlyList<ScanFile> WindowsTempFiles,
    RecycleBinInfo RecycleBin,
    DateTimeOffset ScannedAt,
    IReadOnlyList<ScanFile>? DevCacheFiles = null)
{
    public IReadOnlyList<ScanFile> DevCacheFilesOrEmpty => DevCacheFiles ?? [];
    public long UserTempBytes => UserTempFiles.Sum(file => file.Bytes);
    public long WindowsTempBytes => WindowsTempFiles.Sum(file => file.Bytes);
    public long DevCacheBytes => DevCacheFilesOrEmpty.Sum(file => file.Bytes);
    public long TotalBytes => UserTempBytes + WindowsTempBytes + DevCacheBytes + RecycleBin.Bytes;
    public int TotalFiles => UserTempFiles.Count + WindowsTempFiles.Count + DevCacheFilesOrEmpty.Count + RecycleBin.Items;

    public bool IsFresh(TimeSpan maximumAge, DateTimeOffset now) => now - ScannedAt <= maximumAge;
}

public sealed record StorageVolumeInfo(
    string Root,
    string DisplayName,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSystem);

public sealed record DiskUsageSegment(string Name, long Bytes, string Color);
public sealed record DiskUsageDriveSummary(string Root, long TotalBytes, long FreeBytes);

public sealed record DiskUsageSnapshot(
    long TotalBytes,
    IReadOnlyList<DiskUsageSegment> Segments,
    int SkippedDrives = 0,
    IReadOnlyList<DiskUsageDriveSummary>? Drives = null)
{
    public bool FromCache { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - (Segments.FirstOrDefault(segment => segment.Name == "Свободно")?.Bytes ?? 0));
}

public sealed record InstalledProgram(
    string Name,
    string Publisher,
    string Version,
    long EstimatedBytes,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastUsedAt,
    string LastUsedSource,
    string InstallLocation,
    string RegistryScope,
    string UninstallString,
    string RegistryKeyPath,
    string? PackageManager = null,
    string? PackageId = null)
{
    public string Architecture
    {
        get
        {
            if (RegistryScope.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
            if (RegistryScope.Contains("32-бит", StringComparison.OrdinalIgnoreCase) ||
                RegistryScope.Contains("i386", StringComparison.OrdinalIgnoreCase) ||
                RegistryScope.Contains("x86", StringComparison.OrdinalIgnoreCase))
            {
                return "x86";
            }

            if (RegistryScope.Contains("noarch", StringComparison.OrdinalIgnoreCase) ||
                RegistryScope.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                return "all";
            }

            return "x64";
        }
    }
}

public enum ProgramSortMode
{
    LeastRecentlyUsed,
    MostRecentlyUsed,
    LargestFirst,
    NewestInstall,
    Name,
    Publisher,
    Source
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record UninstallCommand(string FileName, string Arguments)
{
    public string Display => string.IsNullOrWhiteSpace(Arguments) ? FileName : $"{FileName} {Arguments}";
}

public sealed record ResidualFile(string Path, long Size, string Root);

[SupportedOSPlatform("windows")]
public sealed record ResidualRegistryEntry(RegistryHive Hive, RegistryView View, string KeyPath, string DisplayName);

public sealed record UninstallOutcome(
    bool Started,
    int? ExitCode,
    bool StillInstalled,
    string Message,
    IReadOnlyList<ResidualFile> ResidualFiles,
    IReadOnlyList<ResidualRegistryEntry> ResidualRegistryEntries);

public sealed record ResidualCleanupResult(
    int DeletedFiles,
    int SkippedFiles,
    long DeletedBytes,
    IReadOnlyDictionary<CleanupSkipReason, int> SkippedByReason);

public sealed record ResidualRegistryCleanupResult(
    int DeletedEntries,
    int SkippedEntries,
    IReadOnlyDictionary<CleanupSkipReason, int> SkippedByReason);

internal sealed record DeleteAttemptResult(bool Succeeded, CleanupSkipReason Reason);
