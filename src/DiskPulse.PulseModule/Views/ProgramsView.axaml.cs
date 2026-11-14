using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class ProgramsView : UserControl
{
    private readonly IInstalledProgramsService _programsService = PlatformServices.CreateInstalledProgramsService();
    private readonly IResidualCleanupService _residualService = PlatformServices.CreateResidualCleanupService();
    private readonly ProgramUninstallService _uninstallService;
    private readonly IPulseHostContext _hostContext = null!;
    private readonly DispatcherTimer _searchDebounceTimer;
    private IReadOnlyList<InstalledProgram> _allPrograms = [];
    private IReadOnlyList<InstalledProgram> _filteredPrograms = [];
    private ProgramSortMode _sortMode = ProgramSortMode.LeastRecentlyUsed;
    private SortDirection _sortDirection = SortDirection.Ascending;
    private CancellationTokenSource? _loadCts;
    private bool _isLoading;
    private bool _isUninstalling;

    public ProgramsView()
    {
        InitializeComponent();
        _uninstallService = new ProgramUninstallService(_programsService, _residualService);
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
    }

    public ProgramsView(IPulseHostContext hostContext)
    {
        InitializeComponent();
        _hostContext = hostContext;
        _uninstallService = new ProgramUninstallService(_programsService, _residualService);
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        OpenSystemAppsButton.IsVisible = OperatingSystem.IsWindows();
        RegistryKeyLabel.Text = OperatingSystem.IsWindows() ? "Ключ реестра:" : "Запись / .desktop:";

        _ = LoadProgramsAsync();
    }

    public void DisposeResources()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private async Task LoadProgramsAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        SetLoadingState(true);
        SummaryText.Text = OperatingSystem.IsWindows()
            ? "Читаем список программ из реестра, UserAssist и Prefetch..."
            : "Читаем установленные пакеты (dpkg/rpm/pacman/flatpak) и .desktop...";

        try
        {
            _allPrograms = await _programsService.LoadAsync(token);
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _hostContext.Logger.Error("Не удалось загрузить список программ", ex);
            SummaryText.Text = "Не удалось прочитать список установленных программ.";
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void SetLoadingState(bool loading)
    {
        _isLoading = loading;
        RefreshButton.IsEnabled = !loading && !_isUninstalling;
        ExportProgramsButton.IsEnabled = !loading && _filteredPrograms.Count > 0;
        UpdateSelectionActions();
    }

    private void ApplyFilters()
    {
        var search = SearchBox.Text;
        var unusedDays = 0;
        if (IdleFilterCombo.SelectedItem is ComboBoxItem idleItem &&
            int.TryParse(idleItem.Tag?.ToString(), out var parsedDays))
        {
            unusedDays = parsedDays;
        }

        var archFilter = ArchFilterCombo.SelectedItem is ComboBoxItem archItem
            ? archItem.Tag?.ToString()
            : null;

        var filtered = InstalledProgramsService.Filter(_allPrograms, search, unusedDays, DateTimeOffset.Now, archFilter);
        _filteredPrograms = InstalledProgramsService.Sort(filtered, _sortMode, _sortDirection);

        var now = DateTimeOffset.Now;
        ProgramsList.ItemsSource = _filteredPrograms.Select(p => new ProgramRow(
            p,
            p.Name,
            string.IsNullOrWhiteSpace(p.Publisher) ? "—" : p.Publisher,
            p.Architecture,
            p.EstimatedBytes > 0 ? ByteSizeFormatter.Format(p.EstimatedBytes) : "—",
            p.InstalledAt?.LocalDateTime.ToString("dd.MM.yyyy") ?? "—",
            p.LastUsedAt?.LocalDateTime.ToString("dd.MM.yyyy") ?? "нет данных",
            FormatIdle(p.LastUsedAt, now),
            p.LastUsedSource)).ToArray();

        UpdateSortHeaders();
        ExportProgramsButton.IsEnabled = !_isLoading && _filteredPrograms.Count > 0;
        SummaryText.Text = $"Показано {_filteredPrograms.Count:N0} из {_allPrograms.Count:N0} программ (отсортировано по выбранному столбцу).";
        UpdateSelectionActions();
    }

    private static string FormatIdle(DateTimeOffset? lastUsed, DateTimeOffset now)
    {
        if (lastUsed is null)
        {
            return "—";
        }

        var days = Math.Max(0, (int)(now - lastUsed.Value).TotalDays);
        return days == 0 ? "сегодня" : $"{days} дн.";
    }

    private void UpdateSortHeaders()
    {
        var arrow = _sortDirection == SortDirection.Descending ? " ▼" : " ▲";
        NameHeaderButton.Content = _sortMode == ProgramSortMode.Name ? $"Программа{arrow}" : "Программа";
        PublisherHeaderButton.Content = _sortMode == ProgramSortMode.Publisher ? $"Издатель{arrow}" : "Издатель";
        SizeHeaderButton.Content = _sortMode == ProgramSortMode.LargestFirst ? $"Размер{arrow}" : "Размер";
        InstalledHeaderButton.Content = _sortMode == ProgramSortMode.NewestInstall ? $"Установлена{arrow}" : "Установлена";
        LastUsedHeaderButton.Content = _sortMode == ProgramSortMode.LeastRecentlyUsed ? $"Последний запуск{arrow}" : "Последний запуск";
        IdleHeaderButton.Content = _sortMode == ProgramSortMode.LeastRecentlyUsed ? $"Простой{arrow}" : "Простой";
        SourceHeaderButton.Content = _sortMode == ProgramSortMode.Source ? $"Источник{arrow}" : "Источник";
    }

    private void ProgramColumnHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<ProgramSortMode>(tag, out var mode))
        {
            return;
        }

        if (_sortMode == mode)
        {
            _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        }
        else
        {
            _sortMode = mode;
            _sortDirection = mode is ProgramSortMode.LargestFirst or ProgramSortMode.NewestInstall
                ? SortDirection.Descending
                : SortDirection.Ascending;
        }

        ApplyFilters();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        ApplyFilters();
    }

    private void ArchFilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_allPrograms.Count > 0)
        {
            ApplyFilters();
        }
    }

    private void IdleFilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_allPrograms.Count > 0)
        {
            ApplyFilters();
        }
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => _ = LoadProgramsAsync();

    private void ProgramsList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private void ProgramsList_DoubleTapped(object? sender, TappedEventArgs e) => _ = UninstallSelectedProgramAsync();

    private void UpdateSelectionActions()
    {
        var selected = (ProgramsList.SelectedItem as ProgramRow)?.Program;
        if (selected is null)
        {
            RegistryKeyBox.Text = "Выберите программу, чтобы увидеть детали";
            UninstallCommandBox.Text = "—";
            OpenFolderButton.IsEnabled = false;
            ScanResidualsButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            return;
        }

        RegistryKeyBox.Text = string.IsNullOrWhiteSpace(selected.RegistryKeyPath) ? "—" : selected.RegistryKeyPath;
        UninstallCommandBox.Text = string.IsNullOrWhiteSpace(selected.UninstallString) ? "—" : selected.UninstallString;
        OpenFolderButton.IsEnabled = !_isUninstalling && !string.IsNullOrWhiteSpace(selected.InstallLocation) && Directory.Exists(selected.InstallLocation);
        ScanResidualsButton.IsEnabled = !_isUninstalling;
        UninstallButton.IsEnabled = !_isUninstalling && (!string.IsNullOrWhiteSpace(selected.UninstallString) || !string.IsNullOrWhiteSpace(selected.PackageId));
    }

    private async void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ProgramsList.SelectedItem is not ProgramRow { Program: var program })
        {
            return;
        }

        if (!DesktopIntegrationService.OpenInFileManager(program.InstallLocation, selectFile: false))
        {
            await _hostContext.Dialogs.ShowInfoAsync("Папка не найдена", $"Каталог не найден: {program.InstallLocation}");
        }
    }

    private async void OpenSystemAppsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!DesktopIntegrationService.OpenSystemAppsSettings())
        {
            await _hostContext.Dialogs.ShowErrorAsync("Не удалось открыть настройки Windows", "Функция доступна только в Windows.");
        }
    }

    private async void ScanResidualsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isUninstalling || ProgramsList.SelectedItem is not ProgramRow { Program: var program })
        {
            return;
        }

        if (_programsService.IsStillInstalled(program))
        {
            var proceed = await _hostContext.Dialogs.ConfirmAsync(
                "Программа ещё установлена",
                $"«{program.Name}» всё ещё числится установленной. Поиск остатков безопаснее выполнять после её удаления.\n\nВсё равно найти связанные файлы/кэш?",
                confirmText: "Найти",
                cancelText: "Отмена");
            if (!proceed)
            {
                return;
            }
        }

        var files = await Task.Run(() => _residualService.Find(program));
        var regEntries = OperatingSystem.IsWindows()
            ? await Task.Run(() => _residualService.FindRegistryEntries(program))
            : [];

        await OfferResidualCleanupAsync(program, files, regEntries);
    }

    private void UninstallButton_Click(object? sender, RoutedEventArgs e) => _ = UninstallSelectedProgramAsync();

    private async Task UninstallSelectedProgramAsync()
    {
        if (_isUninstalling || ProgramsList.SelectedItem is not ProgramRow { Program: var program })
        {
            return;
        }

        var needsLinuxRootHint = !OperatingSystem.IsWindows() &&
                                 !string.IsNullOrWhiteSpace(program.PackageManager) &&
                                 !(program.PackageManager == "flatpak" && program.RegistryScope.Contains("Текущий пользователь", StringComparison.OrdinalIgnoreCase));

        var prompt = needsLinuxRootHint
            ? $"Удалить пакет «{program.Name}» ({program.PackageManager}: {program.PackageId})?\n\nДля удаления системного пакета на Linux будут запрошены права администратора (pkexec)."
            : $"Запустить штатное удаление программы «{program.Name}»?";

        var confirmed = await _hostContext.Dialogs.ConfirmAsync(
            "Удаление программы",
            prompt,
            confirmText: "Удалить",
            cancelText: "Отмена",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        _isUninstalling = true;
        SetLoadingState(_isLoading);
        SummaryText.Text = $"Выполняем удаление «{program.Name}»...";

        try
        {
            var outcome = await _uninstallService.RunAsync(
                program,
                CancellationToken.None,
                _hostContext.Elevation.RunElevatedHelperAsync,
                _hostContext.ModuleDataDirectory);

            await _hostContext.Dialogs.ShowInfoAsync("Результат удаления", outcome.Message);

            var hasRegistryResiduals = OperatingSystem.IsWindows() && outcome.ResidualRegistryEntries.Count > 0;
            if (!outcome.StillInstalled && (outcome.ResidualFiles.Count > 0 || hasRegistryResiduals))
            {
                await OfferResidualCleanupAsync(program, outcome.ResidualFiles, outcome.ResidualRegistryEntries);
            }

            await LoadProgramsAsync();
        }
        finally
        {
            _isUninstalling = false;
            SetLoadingState(_isLoading);
        }
    }

    private async Task OfferResidualCleanupAsync(
        InstalledProgram program,
        IReadOnlyList<ResidualFile> files,
        IReadOnlyList<ResidualRegistryEntry> registryEntries)
    {
        var registryCount = OperatingSystem.IsWindows() ? registryEntries.Count : 0;
        if (files.Count == 0 && registryCount == 0)
        {
            await _hostContext.Dialogs.ShowInfoAsync("Остатки не найдены", $"Остаточных файлов или записей для «{program.Name}» не обнаружено.");
            return;
        }

        var totalBytes = files.Sum(f => f.Size);
        var message = $"Для «{program.Name}» найдено:\n" +
                      $"• файлов: {files.Count:N0} ({ByteSizeFormatter.Format(totalBytes)})\n" +
                      (registryCount > 0 ? $"• записей реестра: {registryCount}\n" : string.Empty) +
                      "\nУдалить найденные остатки?";

        var confirmed = await _hostContext.Dialogs.ConfirmAsync(
            "Удаление остатков",
            message,
            confirmText: "Удалить остатки",
            cancelText: "Оставить",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        var fileResult = files.Count > 0 ? await _residualService.DeleteAsync(files) : new ResidualCleanupResult(0, 0, 0, new Dictionary<CleanupSkipReason, int>());
        var regResult = OperatingSystem.IsWindows() && registryEntries.Count > 0
            ? _residualService.DeleteRegistryEntries(registryEntries)
            : new ResidualRegistryCleanupResult(0, 0, new Dictionary<CleanupSkipReason, int>());

        var accessDeniedCount = fileResult.SkippedByReason.GetValueOrDefault(CleanupSkipReason.AccessDenied);
        if (accessDeniedCount > 0 && !_hostContext.Elevation.IsElevated && files.Count > 0)
        {
            var retryElevated = await _hostContext.Dialogs.ConfirmAsync(
                "Требуются повышенные права",
                $"Не удалось удалить {accessDeniedCount:N0} защищённых файлов без повышенных прав.\nЗапустить DiskPulse.Helper с правами администратора для удаления оставшихся файлов?",
                confirmText: "Запросить права",
                cancelText: "Пропустить",
                isDestructive: true);

            if (retryElevated)
            {
                var helperResult = await RunElevatedResidualsCleanupAsync(files);
                if (helperResult is not null)
                {
                    fileResult = new ResidualCleanupResult(
                        fileResult.DeletedFiles + helperResult.DeletedFiles,
                        helperResult.SkippedFiles,
                        fileResult.DeletedBytes + helperResult.DeletedBytes,
                        helperResult.SkippedByReason);
                }
            }
        }

        var skipNote = CleanerScanService.FormatSkipReasons(fileResult.SkippedByReason, minimumAgeHours: 0);
        var summary = $"Удалено файлов: {fileResult.DeletedFiles:N0} ({ByteSizeFormatter.Format(fileResult.DeletedBytes)})" +
                      (regResult.DeletedEntries > 0 ? $"\nУдалено записей реестра: {regResult.DeletedEntries}" : string.Empty) +
                      (fileResult.SkippedFiles > 0 ? $"\nПропущено файлов: {fileResult.SkippedFiles:N0}" + (string.IsNullOrEmpty(skipNote) ? "" : $" ({skipNote})") : string.Empty);

        await _hostContext.Dialogs.ShowInfoAsync("Очистка остатков завершена", summary);
    }

    private async Task<ResidualCleanupResult?> RunElevatedResidualsCleanupAsync(IReadOnlyList<ResidualFile> files)
    {
        Directory.CreateDirectory(_hostContext.ModuleDataDirectory);
        var id = Guid.NewGuid().ToString("N");
        var reqPath = Path.Combine(_hostContext.ModuleDataDirectory, $"helper-res-req-{id}.json");
        var respPath = Path.Combine(_hostContext.ModuleDataDirectory, $"helper-res-resp-{id}.json");

        try
        {
            var existingFiles = files.Where(f => File.Exists(f.Path)).ToArray();
            if (existingFiles.Length == 0)
            {
                return null;
            }

            HelperProtocol.WriteJson(reqPath, new HelperResidualCleanupRequest(existingFiles));
            await _hostContext.Elevation.RunElevatedHelperAsync(
                ["--cleanup-residuals", "--input", reqPath, "--output", respPath],
                CancellationToken.None);

            var resp = HelperProtocol.TryReadResponse(respPath);
            if (resp is null)
            {
                return null;
            }

            var parsed = HelperProtocol.ToCleanupResult(resp);
            return new ResidualCleanupResult(
                parsed.DeletedFiles,
                parsed.SkippedFiles,
                parsed.ReclaimedBytes,
                parsed.SkipReasons ?? new Dictionary<CleanupSkipReason, int>());
        }
        finally
        {
            HelperProtocol.TryDeleteFileQuietly(reqPath);
            HelperProtocol.TryDeleteFileQuietly(respPath);
        }
    }

    private async void ExportProgramsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_filteredPrograms.Count == 0)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт списка установленных программ",
            SuggestedFileName = $"diskpulse-programs-{DateTime.Now:yyyy-MM-dd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV (таблица)") { Patterns = ["*.csv"] },
                new FilePickerFileType("JSON (структура)") { Patterns = ["*.json"] }
            ]
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ReportExportService.ExportProgramsJson(path, _filteredPrograms);
            }
            else
            {
                ReportExportService.ExportProgramsCsv(path, _filteredPrograms);
            }

            await _hostContext.Dialogs.ShowInfoAsync("Экспорт выполнен", $"Сохранено программ: {_filteredPrograms.Count:N0}\n{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _hostContext.Logger.Error("Не удалось экспортировать список программ", ex);
            await _hostContext.Dialogs.ShowErrorAsync("Ошибка экспорта", $"Не удалось сохранить отчёт: {ex.Message}");
        }
    }

    private sealed record ProgramRow(
        InstalledProgram Program,
        string Name,
        string Publisher,
        string Arch,
        string Size,
        string Installed,
        string LastUsed,
        string Idle,
        string Source);
}
