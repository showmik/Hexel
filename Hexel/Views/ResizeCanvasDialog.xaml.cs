using Hexel.ViewModels;
using System.Windows;

namespace Hexel.Views
{
    public partial class ResizeCanvasDialog : Window
    {
        public (int Width, int Height, Core.ResizeAnchor Anchor)? Result { get; private set; }

        public ResizeCanvasDialog(int currentWidth, int currentHeight)
        {
            InitializeComponent();
            DataContext = new ResizeCanvasDialogViewModel(
                currentWidth, currentHeight,
                result => { Result = result; DialogResult = true; },
                () => { DialogResult = false; });
        }
    }
}
