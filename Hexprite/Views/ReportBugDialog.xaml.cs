using System.Windows;
using Hexprite.Services;

namespace Hexprite.Views
{
    public partial class ReportBugDialog : Window
    {
        public BugReportInput? Result { get; private set; }

        public ReportBugDialog()
        {
            InitializeComponent();
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SummaryTextBox.Text))
            {
                MessageBox.Show(this, "Please enter a short summary before submitting.", "Report a Bug", MessageBoxButton.OK, MessageBoxImage.Information);
                SummaryTextBox.Focus();
                return;
            }

            Result = new BugReportInput
            {
                Summary = SummaryTextBox.Text.Trim(),
                StepsToReproduce = StepsTextBox.Text.Trim(),
                ExpectedBehavior = ExpectedTextBox.Text.Trim(),
                ActualBehavior = ActualTextBox.Text.Trim(),
                ContactEmail = string.IsNullOrWhiteSpace(EmailTextBox.Text) ? null : EmailTextBox.Text.Trim(),
                IncludeRecentLogs = IncludeLogsCheckBox.IsChecked == true
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CaptionClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
