using System.Windows;
using System.Windows.Controls;

namespace Hexel.Views
{
    public partial class ToolSidebarPanel : UserControl
    {
        public ToolSidebarPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Delegates tool selection to the ViewModel's SelectToolCommand.
        /// The RadioButton's Tag carries the tool name string.
        /// </summary>
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is null) return;
            if (DataContext is not ViewModels.MainViewModel vm) return;
            if (vm.SelectToolCommand.CanExecute(rb.Tag.ToString()))
                vm.SelectToolCommand.Execute(rb.Tag.ToString());
        }
    }
}
