using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class SettingsView : UserControl
{
    private readonly Action<int> _apply = _ => { };
    private readonly Action<IReadOnlyList<string>> _applyExclusions = _ => { };
    private readonly Action _openDrives = () => { };
    private readonly IPulseHostContext _hostContext = null!;
    private readonly bool _isStandaloneHost;
    private readonly ObservableCollection<string> _exclusions = [];
    private readonly UpdateService _updateService = new();
    private readonly UpdateInstallerService _installerService = new();
    private bool _updateOperationInProgress;
    private bool _initializing = true;

    public SettingsView()
    {
        InitializeComponent();
    }

    public SettingsView(
        int minimumFileAgeHours,
        int selectedDriveCount,
        IReadOnlyList<string> permanentExclusions,
        IStorageVolumeService storageService,
        IPulseHostContext hostContext,
        bool isStandaloneHost,
        Action<int> apply,
        Action<IReadOnlyList<string>> applyExclusions,
        Action openDrives)
    {
        InitializeComponent();
        _apply = apply;
        _applyExclusions = applyExclusions;
        _openDrives = openDrives;
        _hostContext = hostContext;
        _isStandaloneHost = isStandaloneHost;

        foreach (var item in permanentExclusions)
        {
            _exclusions.Add(item);
        }

        ExclusionsList.ItemsSource = _exclusions;
        AgeFilterCheckBox.IsChecked = minimumFileAgeHours > 0;
        _initializing = false;

        AdminStatusText.Text = hostContext.Elevation.IsElevated
            ? "Повышенные права: активны в текущей сессии"
            : "Режим прав: без UAC/root (повышение прав запрашивается по требованию через DiskPulse.Helper)";

        var systemRoot = CleanerPaths.FormatVolumeLabel(storageService.GetSystemDriveRoot());
        DriveStatusText.Text = $"Выбрано томов: {selectedDriveCount}; системный том: {systemRoot}";

        UpdateVersionText.Text = $"Текущая версия: {CleanerVersion.Current}";
        if (!isStandaloneHost)
        {
            UpdatesCard.IsVisible = false;
        }
        else if (!OperatingSystem.IsWindows())
        {
            InstallCurrentButton.IsVisible = false;
            UpdateStatusText.Text = "Проверка обновлений доступна на GitHub Releases.";
        }
        else
        {
            InstallCurrentButton.IsVisible = !_installerService.IsInstalled;
            InstallCurrentButton.IsEnabled = _installerService.CanInstallCurrentExecutable;
            if (_installerService.IsInnoInstalled)
            {
                UpdateStatusText.Text = "Приложение установлено в системе (per-user).";
            }
            else if (_installerService.IsInstalled)
            {
                UpdateStatusText.Text = "Приложение запускается из папки профиля пользователя.";
            }
            else if (!_installerService.CanInstallCurrentExecutable)
            {
                UpdateStatusText.Text = "Установка доступна из автономного DiskPulse.exe.";
            }
        }
    }

    private void AgeFilterCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        SaveCurrentAgeSetting();
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e) => SaveCurrentAgeSetting();

    private void SaveCurrentAgeSetting()
    {
        _apply(AgeFilterCheckBox.IsChecked == true ? 24 : 0);
        SaveStatusText.Text = $"Сохранено в {DateTime.Now:HH:mm}";
    }

    private void DrivesButton_Click(object? sender, RoutedEventArgs e) => _openDrives();

    private async void CheckUpdatesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress || !_isStandaloneHost)
        {
            return;
        }

        SetUpdateOperationInProgress(true);
        UpdateStatusText.Text = "Проверяем последний релиз на GitHub...";

        try
        {
            var release = await _updateService.GetLatestReleaseAsync();
            if (release is null)
            {
                UpdateStatusText.Text = "Опубликованных релизов пока нет.";
                await _hostContext.Dialogs.ShowInfoAsync("Обновления DiskPulse", "Пока нет опубликованного релиза DiskPulse.");
                return;
            }

            if (release.Version.CompareTo(CleanerVersion.Current) <= 0)
            {
                UpdateStatusText.Text = $"Установлена последняя версия {CleanerVersion.Current}.";
                await _hostContext.Dialogs.ShowInfoAsync("Обновления DiskPulse", $"Установлена последняя версия {CleanerVersion.Current}.");
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                UpdateStatusText.Text = $"Доступна новая версия {release.Version} на GitHub Releases.";
                await _hostContext.Dialogs.ShowInfoAsync(
                    "Обновление DiskPulse",
                    $"Доступна версия {release.Version}. Скачайте архив для Linux со страницы GitHub Releases.");
                return;
            }

            var useInstallerUpdate = _installerService.IsInnoInstalled &&
                release.Installer is not null &&
                release.InstallerChecksum is not null;
            UpdateStatusText.Text = $"Доступна версия {release.Version}.";
            var confirmPrompt = useInstallerUpdate
                ? $"Доступна версия {release.Version}. Скачать установщик, проверить SHA-256 и обновить DiskPulse?"
                : $"Доступна версия {release.Version}. Скачать, проверить SHA-256 и установить её в профиль пользователя?";
            var install = await _hostContext.Dialogs.ConfirmAsync(
                "Обновление DiskPulse",
                confirmPrompt,
                confirmText: "Установить",
                cancelText: "Позже");
            if (!install)
            {
                return;
            }

            var progress = new Progress<int>(percent => UpdateStatusText.Text = $"Загрузка обновления {release.Version}: {percent}%...");
            UpdateStatusText.Text = $"Загрузка обновления {release.Version}...";

            if (useInstallerUpdate)
            {
                var downloadedInstaller = await _updateService.DownloadAndVerifyInstallerAsync(release, progress);
                UpdateStatusText.Text = "Запускаем установщик обновления DiskPulse...";
                _installerService.StartInstallerUpdate(downloadedInstaller);
            }
            else
            {
                var downloadedExecutable = await _updateService.DownloadAndVerifyAsync(release, progress);
                UpdateStatusText.Text = "Запускаем обновление DiskPulse...";
                _installerService.StartInstall(downloadedExecutable);
            }

            Environment.Exit(0);
        }
        catch (Exception ex) when (ex is UpdateException or InvalidDataException or InvalidOperationException or IOException)
        {
            _hostContext.Logger.Error("Ошибка проверки/установки обновлений", ex);
            UpdateStatusText.Text = ex.Message;
            await _hostContext.Dialogs.ShowErrorAsync("Ошибка обновления", ex.Message);
        }
        finally
        {
            SetUpdateOperationInProgress(false);
        }
    }

    private async void InstallCurrentButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (!_installerService.CanInstallCurrentExecutable)
        {
            await _hostContext.Dialogs.ShowInfoAsync("Установка DiskPulse", "Эта копия уже установлена или запущена не из автономного exe.");
            return;
        }

        var confirmed = await _hostContext.Dialogs.ConfirmAsync(
            "Установка DiskPulse",
            "Установить DiskPulse в профиль пользователя и создать ярлык в меню «Пуск»?",
            confirmText: "Установить",
            cancelText: "Отмена");
        if (!confirmed)
        {
            return;
        }

        SetUpdateOperationInProgress(true);
        UpdateStatusText.Text = "Подготавливаем установку DiskPulse...";
        string? staged = null;
        try
        {
            staged = _installerService.StageCurrentExecutable();
            _installerService.StartInstall(staged);
            Environment.Exit(0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (staged is not null)
            {
                _installerService.DeleteStagedExecutable(staged);
            }

            _hostContext.Logger.Error("Ошибка установки в профиль", ex);
            UpdateStatusText.Text = ex.Message;
            await _hostContext.Dialogs.ShowErrorAsync("Ошибка установки", ex.Message);
        }
        finally
        {
            SetUpdateOperationInProgress(false);
        }
    }

    private void SetUpdateOperationInProgress(bool inProgress)
    {
        _updateOperationInProgress = inProgress;
        CheckUpdatesButton.IsEnabled = !inProgress;
        if (!inProgress && _isStandaloneHost && OperatingSystem.IsWindows())
        {
            InstallCurrentButton.IsEnabled = _installerService.CanInstallCurrentExecutable;
        }
        else
        {
            InstallCurrentButton.IsEnabled = false;
        }
    }

    private async void AddExclusionFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку для добавления в белый список исключений",
            AllowMultiple = false
        });

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            AddExclusionPath(localPath);
        }
    }

    private async void AddExclusionFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите файл для добавления в белый список исключений",
            AllowMultiple = false
        });

        var localPath = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            AddExclusionPath(localPath);
        }
    }

    private void AddExclusionPath(string rawPath)
    {
        var normalized = ExclusionListService.NormalizePath(rawPath);
        if (normalized is null || _exclusions.Contains(normalized, CleanerPaths.PathComparer))
        {
            return;
        }

        _exclusions.Add(normalized);
        SaveExclusions();
    }

    private void RemoveExclusionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ExclusionsList.SelectedItem is string selected)
        {
            _exclusions.Remove(selected);
            SaveExclusions();
        }
    }

    private void ExclusionsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RemoveExclusionButton.IsEnabled = ExclusionsList.SelectedItem is string;
    }

    private void SaveExclusions()
    {
        _applyExclusions(_exclusions.ToArray());
        SaveStatusText.Text = $"Исключения обновлены в {DateTime.Now:HH:mm}";
    }
}
