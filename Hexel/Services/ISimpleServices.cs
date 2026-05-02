using Hexel.Core;

namespace Hexel.Services
{
    public interface IClipboardService
    {
        void SetText(string text);
    }

    public interface IDialogService
    {
        void ShowMessage(string message);
    }

    public interface IFileService
    {
        void SaveSprite(SpriteState state);
        SpriteState? LoadSprite();
    }
}
