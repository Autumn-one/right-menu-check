using System.ComponentModel;
using System.Windows;

namespace RightMenuCheck.App;

public partial class VersionStatusWindow : Window
{
    private bool _allowClose;

    public VersionStatusWindow(string detail)
    {
        InitializeComponent();
        DetailText.Text = detail;
        Closing += VersionStatusWindow_Closing;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        DialogResult = true;
    }

    private void VersionStatusWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Activate();
        }
    }
}
