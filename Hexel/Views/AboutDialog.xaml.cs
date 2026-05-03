using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace Hexel.Views
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

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
