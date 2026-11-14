namespace Cleaner;

/// <summary>
/// Поиск и удаление пользовательских остатков программ на Linux (~/.config, ~/.local/share, ~/.cache, ~/.local/state).
/// </summary>
public sealed class LinuxResidualCleanupService : IResidualCleanupService
{
    private readonly ISafeDeleteService _safeDeleteService;

    public LinuxResidualCleanupService() : this(PlatformServices.CreateSafeDeleteService())
    {
    }

    internal LinuxResidualCleanupService(ISafeDeleteService safeDeleteService)
    {
        _safeDeleteService = safeDeleteService;
    }

    public IReadOnlyList<ResidualFile> Find(InstalledProgram program)
    {
        var roots = GetLinuxRoots(program);
        return ResidualCleanupService.EnumerateResidualsInRoots(roots);
    }

    public IReadOnlyList<ResidualRegistryEntry> FindRegistryEntries(InstalledProgram program) => [];

    public ResidualCleanupResult Delete(IEnumerable<ResidualFile> files) =>
        Delete(files, CancellationToken.None, null);

    public Task<ResidualCleanupResult> DeleteAsync(
        IEnumerable<ResidualFile> files,
        CancellationToken cancellationToken = default,
        IProgress<CleanupProgress>? progress = null) =>
        Task.Run(() => Delete(files, cancellationToken, progress), cancellationToken);

    public ResidualCleanupResult Delete(
        IEnumerable<ResidualFile> files,
        CancellationToken cancellationToken,
        IProgress<CleanupProgress>? progress = null)
    {
        return new ResidualCleanupService(_safeDeleteService).Delete(files, cancellationToken, progress);
    }

    public ResidualRegistryCleanupResult DeleteRegistryEntries(IEnumerable<ResidualRegistryEntry> entries) =>
        new(0, 0, new Dictionary<CleanupSkipReason, int>());

    internal static IReadOnlyList<string> GetLinuxRoots(InstalledProgram program, string? customHome = null)
    {
        var roots = new List<string>();
        var home = !string.IsNullOrWhiteSpace(customHome)
            ? customHome
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(program.InstallLocation) &&
            !string.IsNullOrWhiteSpace(home) &&
            program.InstallLocation.StartsWith(home, StringComparison.Ordinal) &&
            Directory.Exists(program.InstallLocation))
        {
            ResidualCleanupService.AddInstallRoot(roots, program.InstallLocation);
        }

        if (string.IsNullOrWhiteSpace(home))
        {
            return roots;
        }

        var installLeaf = !string.IsNullOrWhiteSpace(program.PackageId)
            ? program.PackageId
            : Path.GetFileName(program.InstallLocation.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(installLeaf))
        {
            installLeaf = program.Name;
        }

        var profileRoots = new[]
        {
            Path.Combine(home, ".config"),
            Path.Combine(home, ".local", "share"),
            Path.Combine(home, ".cache"),
            Path.Combine(home, ".local", "state"),
            Path.Combine(home, ".var", "app")
        };

        foreach (var profileRoot in profileRoots.Distinct(CleanerPaths.PathComparer))
        {
            if (Directory.Exists(profileRoot))
            {
                ResidualCleanupService.FindRelatedDirectories(profileRoot, installLeaf, program.Name, roots);
            }
        }

        return roots.Distinct(CleanerPaths.PathComparer).ToArray();
    }
}
