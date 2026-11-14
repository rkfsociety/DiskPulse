using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class DriveSelectionView : UserControl
{
    private readonly Action<IReadOnlyList<string>> _apply = _ => { };
    private readonly Action _back = () => { };
    private readonly IPulseDialogService? _dialogs;
    private readonly List<CheckBox> _boxes = [];

    public DriveSelectionView()
    {
        InitializeComponent();
    }

    public DriveSelectionView(
        IEnumerable<string> selectedDrives,
        IStorageVolumeService storageService,
        Action<IReadOnlyList<string>> apply,
        Action back,
        IPulseDialogService dialogs)
    {
        InitializeComponent();
        _apply = apply;
        _back = back;
        _dialogs = dialogs;

        var selected = new HashSet<string>(selectedDrives, CleanerPaths.PathComparer);
        var volumes = storageService.GetAvailableVolumes();

        foreach (var vol in volumes)
        {
            var freePercent = vol.TotalBytes > 0 ? Math.Round(vol.FreeBytes * 100d / vol.TotalBytes) : 0;
            var subtitle = vol.TotalBytes > 0
                ? $"{vol.FileSystem}  •  свободно {ByteSizeFormatter.Format(vol.FreeBytes)} из {ByteSizeFormatter.Format(vol.TotalBytes)} ({freePercent:0}%)"
                : vol.FileSystem;

            var box = new CheckBox
            {
                Content = $"{vol.DisplayName} — {subtitle}",
                Tag = vol.Root,
                IsChecked = selected.Contains(vol.Root) || (selected.Count == 0 && vol.IsSystem),
                FontSize = 14,
                Margin = new Thickness(0, 6)
            };
            _boxes.Add(box);
            DrivePanel.Children.Add(box);
        }
    }

    private void SelectAllButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var box in _boxes)
        {
            box.IsChecked = true;
        }
    }

    private void ClearAllButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var box in _boxes)
        {
            box.IsChecked = false;
        }
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e) => _back();

    private async void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var drives = _boxes
            .Where(box => box.IsChecked == true)
            .Select(box => box.Tag as string ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (drives.Length == 0)
        {
            if (_dialogs is not null)
            {
                await _dialogs.ShowInfoAsync("Выберите том", "Отметьте хотя бы один диск или раздел для проверки.");
            }

            return;
        }

        _apply(drives);
    }
}
