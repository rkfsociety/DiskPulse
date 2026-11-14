using Avalonia.Controls;

namespace DiskPulse.PulseModule.Views;

public partial class HelpView : UserControl
{
    public HelpView() : this(string.Empty)
    {
    }

    public HelpView(string dataDirectory)
    {
        InitializeComponent();
        DataPathText.Text = string.IsNullOrWhiteSpace(dataDirectory)
            ? "Настройки, исключения и история очисток хранятся в каталоге данных модуля (app.log ведётся там же)."
            : $"Настройки, белый список исключений и история очисток хранятся в папке:\n{dataDirectory}";
    }
}
