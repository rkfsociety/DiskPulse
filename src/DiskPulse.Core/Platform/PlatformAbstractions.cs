namespace Cleaner;

public interface IStorageVolumeService
{
    string GetSystemDriveRoot();
    IReadOnlyList<string> GetDefaultDriveRoots();
    IReadOnlyList<StorageVolumeInfo> GetAvailableVolumes();
    IReadOnlyList<(string Name, string[] RelativePaths, string Color)> GetUsageCategories(string volumeRoot);
}

public interface IRecycleBinService
{
    RecycleBinInfo GetInfo(IEnumerable<string>? roots = null);
    IReadOnlyDictionary<string, RecycleBinInfo> GetInfoPerRoot(IEnumerable<string>? roots = null);
    bool Empty(string? root);
}

internal interface ISafeDeleteService
{
    DeleteAttemptResult TryDelete(string fullPath, string root, DateTime? maxLastWriteTimeUtc = null);
}

internal interface IScanRootsProvider
{
    IReadOnlyList<ScanRoot> BuildUserCleanupCategorizedRoots(IEnumerable<string> drives);
    IReadOnlyList<ScanRoot> BuildSystemCleanupCategorizedRoots(IEnumerable<string> drives);
    IReadOnlyList<ScanRoot> BuildDevCacheCategorizedRoots(IEnumerable<string> drives);
    List<ScanFile> ScanExtraUserFiles(IEnumerable<string> drives, CancellationToken cancellationToken);
    IReadOnlyList<string> GetExtraUserDeleteRoots(IEnumerable<string> drives);
    bool IsAllowedExtraUserFile(string fullPath, string root);
    bool ShouldIncludeScannedFile(string rootPath, string fullPath, string fileName);
}

public interface IInstalledProgramsService
{
    Task<IReadOnlyList<InstalledProgram>> LoadAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<InstalledProgram> Load(CancellationToken cancellationToken = default);
    bool IsStillInstalled(InstalledProgram program);
}

public interface IResidualCleanupService
{
    IReadOnlyList<ResidualFile> Find(InstalledProgram program);
    IReadOnlyList<ResidualRegistryEntry> FindRegistryEntries(InstalledProgram program);
    ResidualCleanupResult Delete(IEnumerable<ResidualFile> files);
    ResidualCleanupResult Delete(IEnumerable<ResidualFile> files, CancellationToken cancellationToken, IProgress<CleanupProgress>? progress = null);
    Task<ResidualCleanupResult> DeleteAsync(IEnumerable<ResidualFile> files, CancellationToken cancellationToken = default, IProgress<CleanupProgress>? progress = null);
    ResidualRegistryCleanupResult DeleteRegistryEntries(IEnumerable<ResidualRegistryEntry> entries);
}

public static class PlatformServices
{
    public static IStorageVolumeService CreateStorageVolumeService() =>
        OperatingSystem.IsWindows() ? new WindowsStorageVolumeService() : new LinuxStorageService();

    public static IRecycleBinService CreateRecycleBinService() =>
        OperatingSystem.IsWindows() ? new RecycleBinService() : new LinuxTrashService();

    internal static ISafeDeleteService CreateSafeDeleteService() =>
        OperatingSystem.IsWindows() ? new WindowsSafeDeleteServiceAdapter() : new UnixSafeDeleteService();

    internal static IScanRootsProvider CreateScanRootsProvider() =>
        OperatingSystem.IsWindows() ? new WindowsScanRootsProvider() : new LinuxScanRootsProvider();

    public static IInstalledProgramsService CreateInstalledProgramsService() =>
        OperatingSystem.IsWindows() ? new InstalledProgramsService() : new LinuxInstalledProgramsService();

    public static IResidualCleanupService CreateResidualCleanupService() =>
        OperatingSystem.IsWindows() ? new ResidualCleanupService() : new LinuxResidualCleanupService();
}
