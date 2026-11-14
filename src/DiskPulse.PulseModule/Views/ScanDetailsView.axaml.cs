using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class ScanDetailsView : UserControl
{
    private readonly ScanResult _result = null!;
    private readonly ISet<string> _excludedPaths = null!;
    private readonly Action _exclusionsChanged = () => { };
    private readonly Action _back = () => { };
    private readonly IPulseDialogService _dialogs = null!;
    private readonly IPulseLogger? _logger;
    private readonly List<DetailRow> _allRows = [];
    private List<DetailRow> _visibleRows = [];
    private string _sortColumn = "Size";
    private bool _sortDescending = true;

    public ScanDetailsView()
    {
        InitializeComponent();
    }

    public ScanDetailsView(
        ScanResult result,
        ISet<string> excludedPaths,
        Action exclusionsChanged,
        Action back,
        IPulseDialogService dialogs,
        IPulseLogger? logger = null)
    {
        InitializeComponent();
        _result = result;
        _excludedPaths = excludedPaths;
        _exclusionsChanged = exclusionsChanged;
        _back = back;
        _dialogs = dialogs;
        _logger = logger;

        AddGroup("Кэш пользователя", result.UserTempFiles);
        AddGroup("Системные файлы", result.WindowsTempFiles);
        AddGroup("Кэш разработки", result.DevCacheFilesOrEmpty);

        var categories = _allRows.Select(r => r.Category).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(c => c).ToList();
        categories.Insert(0, "Все категории");
        CategoryFilterCombo.ItemsSource = categories;
        CategoryFilterCombo.SelectedIndex = 0;
        ExportButton.IsEnabled = _allRows.Count > 0;

        RefreshView();
    }

    private void AddGroup(string defaultCategory, IReadOnlyList<ScanFile> files)
    {
        foreach (var file in files)
        {
            var category = string.IsNullOrWhiteSpace(file.Category) ? defaultCategory : file.Category;
            _allRows.Add(new DetailRow(file, category, ByteSizeFormatter.Format(file.Bytes), !_excludedPaths.Contains(file.Path)));
        }
    }

    private void RefreshView()
    {
        var queryText = SearchBox.Text?.Trim() ?? string.Empty;
        var selectedCategory = CategoryFilterCombo.SelectedItem as string;
        var filterByCategory = !string.IsNullOrWhiteSpace(selectedCategory) && selectedCategory != "Все категории";

        var filtered = _allRows.Where(row =>
            (!filterByCategory || string.Equals(row.Category, selectedCategory, StringComparison.CurrentCultureIgnoreCase)) &&
            (string.IsNullOrEmpty(queryText) ||
             row.Path.Contains(queryText, StringComparison.CurrentCultureIgnoreCase) ||
             row.Category.Contains(queryText, StringComparison.CurrentCultureIgnoreCase)));

        IEnumerable<DetailRow> sorted = _sortColumn switch
        {
            "Category" => _sortDescending
                ? filtered.OrderByDescending(r => r.Category, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Bytes)
                : filtered.OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Bytes),
            "Path" => _sortDescending
                ? filtered.OrderByDescending(r => r.Path, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase),
            _ => _sortDescending
                ? filtered.OrderByDescending(r => r.Bytes).ThenBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderBy(r => r.Bytes).ThenBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase)
        };

        _visibleRows = sorted.Take(15000).ToList();
        FilesList.ItemsSource = _visibleRows;
        UpdateHeaders();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var allCount = _allRows.Count;
        var visibleCount = _visibleRows.Count;
        var includedCount = _allRows.Count(r => r.IsIncluded);
        var includedBytes = _allRows.Where(r => r.IsIncluded).Sum(r => r.Bytes);
        var binPart = _result.RecycleBin.Items > 0
            ? $"; корзина: {_result.RecycleBin.Items:N0} элементов ({ByteSizeFormatter.Format(_result.RecycleBin.Bytes)})"
            : string.Empty;

        SummaryText.Text = visibleCount < allCount
            ? $"Показано {visibleCount:N0} из {allCount:N0} файлов. К удалению: {includedCount:N0} ({ByteSizeFormatter.Format(includedBytes)}){binPart}"
            : $"Файлов: {allCount:N0}. К удалению: {includedCount:N0} ({ByteSizeFormatter.Format(includedBytes)}){binPart}";
    }

    private void UpdateHeaders()
    {
        var arrow = _sortDescending ? " ▼" : " ▲";
        CategoryHeaderButton.Content = _sortColumn == "Category" ? $"Категория{arrow}" : "Категория";
        SizeHeaderButton.Content = _sortColumn == "Size" ? $"Размер{arrow}" : "Размер";
        PathHeaderButton.Content = _sortColumn == "Path" ? $"Путь{arrow}" : "Путь";
    }

    private void SortBy(string column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column == "Size";
        }

        RefreshView();
    }

    private void CategoryHeader_Click(object? sender, RoutedEventArgs e) => SortBy("Category");
    private void SizeHeader_Click(object? sender, RoutedEventArgs e) => SortBy("Size");
    private void PathHeader_Click(object? sender, RoutedEventArgs e) => SortBy("Path");

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshView();
    private void CategoryFilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshView();

    private void FilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        OpenLocationButton.IsEnabled = FilesList.SelectedItem is DetailRow;
    }

    private void ItemCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DetailRow row })
        {
            if (row.IsIncluded)
            {
                _excludedPaths.Remove(row.Path);
            }
            else
            {
                _excludedPaths.Add(row.Path);
            }

            UpdateSummary();
            _exclusionsChanged();
        }
    }

    private void SelectAllButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
        {
            row.IsIncluded = true;
            _excludedPaths.Remove(row.Path);
        }

        UpdateSummary();
        _exclusionsChanged();
    }

    private void ClearAllButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
        {
            row.IsIncluded = false;
            _excludedPaths.Add(row.Path);
        }

        UpdateSummary();
        _exclusionsChanged();
    }

    private async void OpenLocationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not DetailRow row)
        {
            return;
        }

        if (!DesktopIntegrationService.OpenInFileManager(row.Path, selectFile: true))
        {
            await _dialogs.ShowInfoAsync("Файл не найден", $"Не удалось открыть путь: {row.Path}");
        }
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_allRows.Count == 0)
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
            Title = "Экспорт списка найденных файлов",
            SuggestedFileName = $"diskpulse-scan-{DateTime.Now:yyyy-MM-dd}.csv",
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

        var exportFiles = _visibleRows.Select(r => r.File).ToArray();
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ReportExportService.ExportScanFilesJson(path, exportFiles);
            }
            else
            {
                ReportExportService.ExportScanFilesCsv(path, exportFiles);
            }

            await _dialogs.ShowInfoAsync("Экспорт выполнен", $"Сохранено записей: {exportFiles.Length:N0}\n{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Error("Не удалось экспортировать результаты сканирования", ex);
            await _dialogs.ShowErrorAsync("Ошибка экспорта", $"Не удалось сохранить отчёт: {ex.Message}");
        }
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e) => _back();

    private sealed class DetailRow(ScanFile file, string category, string size, bool isIncluded) : INotifyPropertyChanged
    {
        private bool _isIncluded = isIncluded;

        public ScanFile File { get; } = file;
        public string Category { get; } = category;
        public string Size { get; } = size;
        public long Bytes => File.Bytes;
        public string Path => File.Path;

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (_isIncluded != value)
                {
                    _isIncluded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
