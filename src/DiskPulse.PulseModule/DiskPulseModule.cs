using System.Reflection;
using Avalonia.Controls;
using Cleaner;
using DiskPulse.PulseModule.Views;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule;

/// <summary>
/// Точка входа модуля DiskPulse для экосистемы PCPulse и standalone-оболочки.
/// </summary>
public sealed class DiskPulseModule : IPulseModule
{
    private IPulseHostContext? _context;
    private DiskPulseRootView? _cachedView;
    private readonly bool _isStandaloneHost;

    public DiskPulseModule() : this(isStandaloneHost: false)
    {
    }

    public DiskPulseModule(bool isStandaloneHost)
    {
        _isStandaloneHost = isStandaloneHost;
    }

    public PulseModuleDescriptor Descriptor { get; } = new(
        Id: "diskpulse",
        DisplayName: "DiskPulse",
        Description: "Анализ дисков, безопасная очистка временных файлов, кэшей и управление установленными программами.",
        ModuleVersion: ResolveModuleVersion(),
        ContractVersion: PulseContractVersion.Current,
        SupportedPlatforms: PulsePlatform.All,
        RequiresElevationForSomeActions: true);

    public Task InitializeAsync(IPulseHostContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        _context = context;
        Directory.CreateDirectory(context.ModuleDataDirectory);
        context.Logger.Info($"Модуль DiskPulse {Descriptor.ModuleVersion} инициализирован (каталог данных: {context.ModuleDataDirectory}).");
        return Task.CompletedTask;
    }

    public Control CreateView()
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Модуль DiskPulse не инициализирован. Сначала вызовите InitializeAsync.");
        }

        if (_cachedView is null)
        {
            _cachedView = new DiskPulseRootView(_context, _isStandaloneHost);
            _cachedView.ScanOrCleanupCompleted += OnModuleStateChanged;
        }

        return _cachedView;
    }

    public Task<PulseStatusSummary?> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cachedView is not null)
        {
            var summary = _cachedView.BuildStatusSummary();
            if (summary is not null)
            {
                return Task.FromResult<PulseStatusSummary?>(summary);
            }
        }

        var dataDir = _context?.ModuleDataDirectory ?? CleanerPaths.GetDefaultStandaloneDataDirectory();
        var latestHistory = CleanupHistoryService.FromDataDirectory(dataDir).LoadLatest();
        var storage = PlatformServices.CreateStorageVolumeService();
        var systemRoot = storage.GetSystemDriveRoot();
        var volumes = storage.GetAvailableVolumes();
        var totalFree = volumes.Sum(v => v.FreeBytes);

        var headline = totalFree > 0
            ? $"Свободно на дисках: {ByteSizeFormatter.Format(totalFree)}"
            : $"Системный том {CleanerPaths.FormatVolumeLabel(systemRoot)} готов к проверке";

        var details = latestHistory is not null
            ? $"Последняя очистка ({latestHistory.Timestamp.LocalDateTime:dd.MM HH:mm}): освобождено {ByteSizeFormatter.Format(latestHistory.ReclaimedBytes)}."
            : "Запустите проверку в модуле DiskPulse для поиска временных файлов и кэшей.";

        return Task.FromResult<PulseStatusSummary?>(new PulseStatusSummary(
            Headline: headline,
            Details: details,
            Severity: PulseStatusSeverity.Normal));
    }

    public ValueTask DisposeAsync()
    {
        if (_cachedView is not null)
        {
            _cachedView.ScanOrCleanupCompleted -= OnModuleStateChanged;
            _cachedView.DisposeResources();
            _cachedView = null;
        }

        _context?.Logger.Info("Модуль DiskPulse выгружен.");
        return ValueTask.CompletedTask;
    }

    private void OnModuleStateChanged()
    {
        _context?.RequestSummaryRefresh();
    }

    private static Version ResolveModuleVersion()
    {
        var v = typeof(DiskPulseModule).Assembly.GetName().Version;
        if (v is null)
        {
            return new Version(0, 1, 0);
        }

        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
