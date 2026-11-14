using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PCPulse.Contracts;

namespace Cleaner.App.Views;

public partial class AppDialogHost : UserControl, IPulseDialogService
{
    private TaskCompletionSource<bool>? _pending;

    public AppDialogHost()
    {
        InitializeComponent();
    }

    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(title, message, "Понятно", null, isDestructive: false, cancellationToken);

    public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(title, message, "Закрыть", null, isDestructive: true, cancellationToken);

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "Продолжить",
        string cancelText = "Отмена",
        bool isDestructive = false,
        CancellationToken cancellationToken = default) =>
        ShowDialogAsync(title, message, confirmText, cancelText, isDestructive, cancellationToken);

    private async Task<bool> ShowDialogAsync(
        string title,
        string message,
        string confirmText,
        string? cancelText,
        bool isDestructive,
        CancellationToken cancellationToken)
    {
        if (_pending is not null)
        {
            _pending.TrySetResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Background = Brush.Parse(isDestructive ? "#DC2626" : "#7C6BFF");

        if (string.IsNullOrEmpty(cancelText))
        {
            CancelButton.IsVisible = false;
        }
        else
        {
            CancelButton.Content = cancelText;
            CancelButton.IsVisible = true;
        }

        IsVisible = true;

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            IsVisible = false;
            if (ReferenceEquals(_pending, tcs))
            {
                _pending = null;
            }
        }
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        _pending?.TrySetResult(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        _pending?.TrySetResult(false);
    }
}
