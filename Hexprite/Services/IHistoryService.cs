using Hexprite.Core;

namespace Hexprite.Services
{
    public interface IHistoryService
    {
        void SaveState(SpriteState state);
        SpriteState Undo(SpriteState currentState);
        SpriteState Redo(SpriteState currentState);
    }
}
