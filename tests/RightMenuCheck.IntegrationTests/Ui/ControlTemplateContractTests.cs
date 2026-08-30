using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RightMenuCheck.IntegrationTests.Ui;

public sealed class ControlTemplateContractTests
{
    [Fact]
    public void ComboBoxAndScrollBarHonorRequiredTemplateContracts()
    {
        RunOnSta(() =>
        {
            using var application = new RightMenuCheck.App.App();
            application.InitializeComponent();

            var comboBox = new ComboBox
            {
                Style = Assert.IsType<Style>(application.FindResource(typeof(ComboBox))),
            };
            Assert.True(comboBox.ApplyTemplate());
            Assert.IsType<Popup>(comboBox.Template.FindName("PART_Popup", comboBox));

            var scrollBar = new ScrollBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 40,
                ViewportSize = 20,
                Style = Assert.IsType<Style>(application.FindResource(typeof(ScrollBar))),
            };
            Assert.True(scrollBar.ApplyTemplate());
            var track = Assert.IsType<Track>(
                scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.Equal(0, track.Minimum);
            Assert.Equal(100, track.Maximum);
            Assert.Equal(40, track.Value);
            Assert.Equal(20, track.ViewportSize);

            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 40,
                Style = Assert.IsType<Style>(application.FindResource(typeof(ProgressBar))),
            };
            Assert.True(progressBar.ApplyTemplate());
            Assert.NotNull(progressBar.Template.FindName("PART_Track", progressBar));
            Assert.NotNull(progressBar.Template.FindName("PART_Indicator", progressBar));

            Assert.IsType<Style>(application.FindResource(typeof(DataGrid)));
            Assert.IsType<Style>(application.FindResource(typeof(DataGridColumnHeader)));
            Assert.IsType<Style>(application.FindResource(typeof(DataGridRow)));
            Assert.IsType<Style>(application.FindResource(typeof(DataGridCell)));

            application.Shutdown();
        });
    }

    private static void RunOnSta(Action assertion)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                assertion();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "RightMenuCheck.ControlTemplateContracts",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF template test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
