using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Hexprite.Services;

namespace Hexprite.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();

            // Pull version from assembly metadata
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                TxtVersion.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "AboutDialog.OpenHyperlink", new { e.Uri.AbsoluteUri });
                MessageBox.Show(
                    "Could not open the link.",
                    "Hexel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            e.Handled = true;
        }
    }
}
