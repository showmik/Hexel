using System.Collections.Generic;
using Hexprite.Core;

namespace Hexprite.Services
{
    public class HistoryService : IHistoryService
    {
        private const int MaxHistory = 100;
        private readonly Stack<SpriteState> _undoStack = new();
        private readonly Stack<SpriteState> _redoStack = new();

        public void SaveState(SpriteState state)
        {
            if (state == null) return;

            _undoStack.Push(state.Clone());
            _redoStack.Clear();
            if (_undoStack.Count > MaxHistory)
                TrimStack(_undoStack, MaxHistory);
        }

        public SpriteState Undo(SpriteState currentState)
        {
            if (_undoStack.Count == 0) return currentState;

            _redoStack.Push(currentState.Clone());
            var restored = _undoStack.Pop();
            restored.EnsureLayers();
            return restored;
        }

        public SpriteState Redo(SpriteState currentState)
        {
            if (_redoStack.Count == 0) return currentState;

            _undoStack.Push(currentState.Clone());
            var restored = _redoStack.Pop();
            restored.EnsureLayers();
            return restored;
        }

        private static void TrimStack(Stack<SpriteState> stack, int maxCount)
        {
            var temp = stack.ToArray(); // top -> bottom
            stack.Clear();
            for (int i = Math.Min(maxCount, temp.Length) - 1; i >= 0; i--)
                stack.Push(temp[i]);
        }
    }
}
