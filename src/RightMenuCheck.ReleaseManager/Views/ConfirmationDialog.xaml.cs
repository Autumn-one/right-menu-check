using System.Windows;

namespace RightMenuCheck.ReleaseManager.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(string title, string message, string confirmationLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationLabel);
        DialogTitle = title;
        Message = message;
        ConfirmationLabel = confirmationLabel;
        DataContext = this;
        InitializeComponent();
        Title = title;
    }

    public string DialogTitle { get; }

    public string Message { get; }

    public string ConfirmationLabel { get; }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
