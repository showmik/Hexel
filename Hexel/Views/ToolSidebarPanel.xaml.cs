using System.Windows.Controls;

namespace Hexel.Views
{
    /// <summary>
    /// Tool selection sidebar. Tool state is owned entirely by
    /// <see cref="ViewModels.MainViewModel.CurrentTool"/>; each RadioButton's
    /// IsChecked binds two-way via EnumToBoolConverter — no code-behind required.
    /// </summary>
    public partial class ToolSidebarPanel : UserControl
    {
        public ToolSidebarPanel() { InitializeComponent(); }
    }
}
