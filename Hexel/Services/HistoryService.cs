using System.Collections.Generic;
using Hexel.Core;

namespace Hexel.Services
{
    public class HistoryService : IHistoryService
    {
        /// <summary>
        /// Maximum number of undo steps retained. Each entry is a cloned bool[]
        /// so memory cost is (Width * Height) bytes per entry. At 256×256 that is
        /// 65 KB per entry; 100 entries is ~6.5 MB worst-case.
        /// </summary>
        private const int MaxHistory = 100;

        private readonly Stack<SpriteState> _undoStack = new();
        private readonly Stack<SpriteState> _redoStack = new();

        public void SaveState(SpriteState state)
        {
            _undoStack.Push(state.Clone());
            _redoStack.Clear();

            // Drop the oldest entry when the cap is exceeded
            if (_undoStack.Count > MaxHistory)
                TrimStack(_undoStack, MaxHistory);
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

        // Stack<T> has no RemoveAt / indexed access, so we rebuild it when trimming.
        private static void TrimStack(Stack<SpriteState> stack, int maxCount)
        {
            var temp = new SpriteState[stack.Count];
            int i = 0;
            foreach (var s in stack) temp[i++] = s; // top-first order

            stack.Clear();
            // Re-push only the most-recent maxCount entries (they are at the start of temp)
            for (int j = Math.Min(maxCount, temp.Length) - 1; j >= 0; j--)
                stack.Push(temp[j]);
        }
    }
}
