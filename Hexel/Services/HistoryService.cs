using System.Collections.Generic;
using Hexel.Core;

namespace Hexel.Services
{
    public class HistoryService : IHistoryService
    {
        private readonly Stack<SpriteState> _undoStack = new Stack<SpriteState>();
        private readonly Stack<SpriteState> _redoStack = new Stack<SpriteState>();

        public void SaveState(SpriteState state)
        {
            _undoStack.Push(state.Clone());
            _redoStack.Clear();
        }

        public SpriteState Undo(SpriteState currentState)
        {
            if (_undoStack.Count == 0) return currentState;

            _redoStack.Push(currentState.Clone());
            return _undoStack.Pop();
        }

        public SpriteState Redo(SpriteState currentState)
        {
            if (_redoStack.Count == 0) return currentState;

            _undoStack.Push(currentState.Clone());
            return _redoStack.Pop();
        }
    }
}