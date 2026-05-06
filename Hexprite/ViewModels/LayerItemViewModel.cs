using CommunityToolkit.Mvvm.ComponentModel;

namespace Hexprite.ViewModels
{
    public partial class LayerItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _name = "Layer";

        [ObservableProperty]
        private bool _isVisible = true;

        [ObservableProperty]
        private bool _isLocked;

        [ObservableProperty]
        private bool _isActive;
    }
}
