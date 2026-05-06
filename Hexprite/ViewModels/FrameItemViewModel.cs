using CommunityToolkit.Mvvm.ComponentModel;

namespace Hexprite.ViewModels
{
    public partial class FrameItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _name = "Frame";

        [ObservableProperty]
        private bool _isActive;
    }
}
