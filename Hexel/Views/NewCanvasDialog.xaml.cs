using Hexel.ViewModels;
using System.Windows;

namespace Hexel.Views
{
    public partial class NewCanvasDialog : Window
    {
        /// <summary>Result dimensions if the user clicked Create, null if cancelled.</summary>
        public (int Width, int Height)? Result { get; private set; }

        public NewCanvasDialog()
        {
            InitializeComponent();
            DataContext = new NewCanvasDialogViewModel(
                result => { Result = result; DialogResult = true; },
                () => { DialogResult = false; });
        }
    }
}
