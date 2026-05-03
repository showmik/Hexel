using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexel.Core;
using System;

namespace Hexel.ViewModels
{
    /// <summary>
    /// ViewModel for <see cref="Hexel.Views.ResizeCanvasDialog"/>.
    /// Encapsulates preset parsing, anchor selection, dimension validation
    /// and the Resize/Cancel commands.
    /// </summary>
    public class ResizeCanvasDialogViewModel : ObservableObject
    {
        // ── Callbacks ─────────────────────────────────────────────────────
        private readonly Action<(int Width, int Height, ResizeAnchor Anchor)> _onConfirm;
        private readonly Action _onCancel;

        // ── Current size (display-only) ───────────────────────────────────
        public string CurrentSizeText { get; }

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

        private int _newWidth;
        public int NewWidth
        {
            get => _newWidth;
            set => SetProperty(ref _newWidth, Math.Clamp(value, 1, SpriteState.MaxDimension));
        }

        private int _newHeight;
        public int NewHeight
        {
            get => _newHeight;
            set => SetProperty(ref _newHeight, Math.Clamp(value, 1, SpriteState.MaxDimension));
        }

        private ResizeAnchor _selectedAnchor = ResizeAnchor.TopLeft;
        public ResizeAnchor SelectedAnchor
        {
            get => _selectedAnchor;
            set => SetProperty(ref _selectedAnchor, value);
        }

        // ── Error state ───────────────────────────────────────────────────

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        // ── Commands ──────────────────────────────────────────────────────
        public IRelayCommand ResizeCommand { get; }
        public IRelayCommand CancelCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────

        public ResizeCanvasDialogViewModel(
            int currentWidth,
            int currentHeight,
            Action<(int Width, int Height, ResizeAnchor Anchor)> onConfirm,
            Action onCancel)
        {
            _onConfirm  = onConfirm ?? throw new ArgumentNullException(nameof(onConfirm));
            _onCancel   = onCancel  ?? throw new ArgumentNullException(nameof(onCancel));
            CurrentSizeText = $"{currentWidth} × {currentHeight}";
            _newWidth   = currentWidth;
            _newHeight  = currentHeight;

            ResizeCommand = new RelayCommand(ExecuteResize);
            CancelCommand = new RelayCommand(() => _onCancel());
        }

        // ── Private helpers ───────────────────────────────────────────────

        private void ApplyPreset(string preset)
        {
            if (preset == "Custom") return;

            var label = preset.Split(' ')[0];
            var parts = label.Split(new[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int pw) &&
                int.TryParse(parts[1], out int ph))
            {
                NewWidth  = pw;
                NewHeight = ph;
            }
        }

        private void ExecuteResize()
        {
            ErrorMessage = string.Empty;

            if (NewWidth <= 0 || NewHeight <= 0)
            {
                ErrorMessage = "Please enter valid positive dimensions.";
                return;
            }
            if (NewWidth > SpriteState.MaxDimension || NewHeight > SpriteState.MaxDimension)
            {
                ErrorMessage = $"Maximum canvas size is {SpriteState.MaxDimension}×{SpriteState.MaxDimension}.";
                return;
            }

            _onConfirm((NewWidth, NewHeight, SelectedAnchor));
        }
    }
}
