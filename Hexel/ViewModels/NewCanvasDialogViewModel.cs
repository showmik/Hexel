using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using System;

namespace Hexel.ViewModels
{
    /// <summary>
    /// ViewModel for <see cref="Hexel.Views.NewCanvasDialog"/>.
    /// Encapsulates preset parsing, dimension validation and the Create/Cancel commands.
    /// The View sets this as its DataContext and reads <see cref="Result"/> after
    /// <see cref="CreateCommand"/> fires.
    /// </summary>
    public class NewCanvasDialogViewModel : ObservableObject
    {
        // ── Callbacks ─────────────────────────────────────────────────────
        private readonly Action<(int Width, int Height)> _onConfirm;
        private readonly Action _onCancel;

        // ── Properties ────────────────────────────────────────────────────

        private string _selectedPreset = "Custom";
        public string SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (SetProperty(ref _selectedPreset, value))
                    ApplyPreset(value);
            }
        }

        private int _width = 16;
        public int Width
        {
            get => _width;
            set => SetProperty(ref _width, Math.Clamp(value, 1, SpriteState.MaxDimension));
        }

        private int _height = 16;
        public int Height
        {
            get => _height;
            set => SetProperty(ref _height, Math.Clamp(value, 1, SpriteState.MaxDimension));
        }

        // ── Error state ───────────────────────────────────────────────────

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            private set => SetProperty(ref _errorMessage, value);
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand CreateCommand { get; }
        public IRelayCommand CancelCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────

        public NewCanvasDialogViewModel(
            Action<(int Width, int Height)> onConfirm,
            Action onCancel)
        {
            _onConfirm = onConfirm ?? throw new ArgumentNullException(nameof(onConfirm));
            _onCancel  = onCancel  ?? throw new ArgumentNullException(nameof(onCancel));

            CreateCommand = new RelayCommand(ExecuteCreate);
            CancelCommand = new RelayCommand(() => _onCancel());
        }

        // ── Private helpers ───────────────────────────────────────────────

        private void ApplyPreset(string preset)
        {
            if (preset == "Custom") return;

            // Format: "128×64 SSD1306" → take first token, split on "×"
            var label = preset.Split(' ')[0];
            var parts = label.Split(new[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int pw) &&
                int.TryParse(parts[1], out int ph))
            {
                Width  = pw;
                Height = ph;
            }
        }

        private void ExecuteCreate()
        {
            ErrorMessage = string.Empty;

            if (Width <= 0 || Height <= 0)
            {
                ErrorMessage = "Please enter valid positive dimensions.";
                return;
            }
            if (Width > SpriteState.MaxDimension || Height > SpriteState.MaxDimension)
            {
                ErrorMessage = $"Maximum canvas size is {SpriteState.MaxDimension}×{SpriteState.MaxDimension}.";
                return;
            }

            _onConfirm((Width, Height));
        }
    }
}
