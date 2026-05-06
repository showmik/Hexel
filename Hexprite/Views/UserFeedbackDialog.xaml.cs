using System.Windows;
using System.Windows.Controls;
using Hexprite.Services;

namespace Hexprite.Views
{
    public partial class UserFeedbackDialog : Window
    {
        public UserFeedbackInput? Result { get; private set; }

        public UserFeedbackDialog()
        {
            InitializeComponent();
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageTextBox.Text))
            {
                MessageBox.Show(this, "Please enter your feedback before submitting.", "Send Feedback", MessageBoxButton.OK, MessageBoxImage.Information);
                MessageTextBox.Focus();
                return;
            }

            string category = "General";
            if (CategoryCombo.SelectedItem is ComboBoxItem selected && selected.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                category = tag.Trim();
            }

            Result = new UserFeedbackInput
            {
                Category = category,
                Message = MessageTextBox.Text.Trim(),
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
