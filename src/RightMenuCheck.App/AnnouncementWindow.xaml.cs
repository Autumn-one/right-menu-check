using System.Windows;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.App;

public partial class AnnouncementWindow : Window
{
    public AnnouncementWindow(AnnouncementMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        InitializeComponent();
        KindText.Text = message.Kind switch
        {
            AnnouncementKind.Warning => "重要提醒",
            AnnouncementKind.Maintenance => "维护通知",
            _ => "产品通知",
        };
        TitleText.Text = message.Title;
        BodyText.Text = message.Body;
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
