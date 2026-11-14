using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class HistoryView : UserControl
{
    private readonly CleanupHistoryService _history = null!;
    private readonly IPulseDialogService _dialogs = null!;
    private readonly IPulseLogger? _logger;
    private readonly Action _historyChanged = () => { };
    private IReadOnlyList<CleanupHistoryEntry> _entries = [];

    public HistoryView()
    {
        InitializeComponent();
    }

    public HistoryView(
        CleanupHistoryService history,
        IPulseDialogService dialogs,
        Action historyChanged,
        IPulseLogger? logger = null)
    {
        InitializeComponent();
        _history = history;
        _dialogs = dialogs;
        _historyChanged = historyChanged;
        _logger = logger;
        LoadEntries();
    }

    private void LoadEntries()
    {
        _entries = _history.LoadAll();
        HistoryList.ItemsSource = _entries.Select(entry => new HistoryRow(
            entry.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm"),
            entry.Scope,
            entry.DeletedFiles.ToString("N0"),
            ByteSizeFormatter.Format(entry.ReclaimedBytes))).ToArray();

        if (_entries.Count == 0)
        {
            SummaryText.Text = "Очисток пока не было";
            ExportHistoryButton.IsEnabled = false;
            ClearHistoryButton.IsEnabled = false;
            return;
        }

        ExportHistoryButton.IsEnabled = true;
        ClearHistoryButton.IsEnabled = true;
        var total = _entries.Sum(entry => entry.ReclaimedBytes);
        SummaryText.Text = $"Всего записей: {_entries.Count}, суммарно освобождено {ByteSizeFormatter.Format(total)}";
    }

    private async void ExportHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
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
            Title = "Экспорт истории очисток",
            SuggestedFileName = $"diskpulse-history-{DateTime.Now:yyyy-MM-dd}.csv",
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
                ReportExportService.ExportHistoryJson(path, _entries);
            }
            else
            {
                ReportExportService.ExportHistoryCsv(path, _entries);
            }

            await _dialogs.ShowInfoAsync("Экспорт выполнен", $"История очисток сохранена в файл:\n{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Error("Не удалось экспортировать историю очисток", ex);
            await _dialogs.ShowErrorAsync("Ошибка экспорта", $"Не удалось сохранить отчёт: {ex.Message}");
        }
    }

    private async void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Очистка истории",
            "Удалить сохранённый список прошлых очисток?",
            confirmText: "Очистить",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        if (!_history.Clear())
        {
            await _dialogs.ShowErrorAsync("Не удалось очистить историю", "Файл истории занят или недоступен.");
            return;
        }

        LoadEntries();
        _historyChanged();
    }

    private sealed record HistoryRow(string Date, string Scope, string DeletedFiles, string Reclaimed);
}
