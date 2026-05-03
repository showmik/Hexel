using System.Windows;
using System.Windows.Controls;
using Hexel.Core;

namespace Hexel.Views
{
    public partial class ToolSidebarPanel : UserControl
    {
        /// <summary>
        /// When true, the Tool_Checked handler is suppressed.
        /// Used by <see cref="SyncToTool"/> to avoid executing SelectToolCommand
        /// (and its side-effects like CommitIfActive) when programmatically
        /// syncing radio buttons on tab switch.
        /// </summary>
        private bool _suppressChecked;

        public ToolSidebarPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Programmatically sets the correct tool radio button to match the
        /// given <paramref name="tool"/> without triggering the SelectToolCommand.
        /// Called from MainWindow on tab switch.
        /// </summary>
        public void SyncToTool(ToolMode tool)
        {
            _suppressChecked = true;
            try
            {
                var target = tool switch
                {
                    ToolMode.Pencil => RbPencil,
                    ToolMode.Line => RbLine,
                    ToolMode.Rectangle => RbRectangle,
                    ToolMode.Ellipse => RbEllipse,
                    ToolMode.FilledRectangle => RbFilledRectangle,
                    ToolMode.FilledEllipse => RbFilledEllipse,
                    ToolMode.Fill => RbFill,
                    ToolMode.Marquee => RbMarquee,
                    ToolMode.Lasso => RbLasso,
                    ToolMode.MagicWand => RbMagicWand,
                    _ => RbPencil
                };
                target.IsChecked = true;
            }
            finally
            {
                _suppressChecked = false;
            }
        }

        /// <summary>
        /// Delegates tool selection to the ViewModel's SelectToolCommand.
        /// The RadioButton's Tag carries the tool name string.
        /// </summary>
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressChecked) return;
            if (sender is not RadioButton rb || rb.Tag is null) return;
            if (DataContext is not ViewModels.MainViewModel vm) return;
            if (vm.SelectToolCommand.CanExecute(rb.Tag.ToString()))
                vm.SelectToolCommand.Execute(rb.Tag.ToString());
        }
    }
}
