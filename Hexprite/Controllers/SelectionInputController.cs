using System;
using Hexprite.Core;
using Hexprite.Services;
using Hexprite.ViewModels;

namespace Hexprite.Controllers
{
    /// <summary>
    /// Owns the selection-tool input state machine: marquee/lasso/magic-wand
    /// down/move dispatch, selection mode logic (Add/Subtract/Intersect),
    /// floating layer management, and commit/cancel orchestration.
    /// Extracted from MainWindow.xaml.cs to move domain logic out of the View.
    /// </summary>
    public class SelectionInputController
    {
        private readonly MainViewModel _vm;
        private readonly ISelectionService _selection;
        private readonly IDrawingService _drawingService;

        private int _selectionAnchorX = -1;
        private int _selectionAnchorY = -1;

        public SelectionInputController(
            MainViewModel vm,
            ISelectionService selectionService,
            IDrawingService drawingService)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _selection = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
        }

        /// <summary>
        /// Main entry point for selection tool input from the View.
        /// The View only provides pixel coordinates and modifier key state.
        /// </summary>
        public void ProcessInput(int x, int y, ToolAction action, bool isShiftDown, bool isAltDown, bool isInverse = false)
        {
            switch (action)
            {
                case ToolAction.Down:
                    HandleDown(x, y, isShiftDown, isAltDown, isInverse);
                    break;
                case ToolAction.Move:
                    HandleMove(x, y, isShiftDown);
                    break;
                case ToolAction.Up:
                    HandleUp();
                    break;
            }
        }

        /// <summary>
        /// Commits the current selection (stamps floating pixels) and clears overlays.
        /// Returns true if there was an active selection that was committed.
        /// </summary>
        public bool CommitIfActive()
        {
            if (!_selection.HasActiveSelection) return false;
            
            bool wasFloating = _selection.IsFloating;
            
            _selection.CommitSelection(_vm.SpriteState);
            _vm.RedrawGridFromMemory();
            
            if (wasFloating)
            {
                _vm.MarkCodeStale();
            }
            
            return true;
        }

        /// <summary>
        /// Begins a floating-layer drag from the specified screen-space anchor.
        /// Called by the View when a click lands inside an active selection.
        /// Returns true if the drag was started.
        /// </summary>
        public bool TryBeginDrag(int pixelX, int pixelY)
        {
            if (!_selection.HasActiveSelection || !_selection.IsPixelInSelection(pixelX, pixelY))
                return false;

            _vm.SaveStateForUndo();
            _selection.LiftSelection(_vm.SpriteState);
            _selection.BeginDrag();
            _vm.RedrawGridFromMemory();
            return true;
        }

        // ── Private: input dispatch ────────────────────────────────────────

        private void HandleDown(int x, int y, bool isShiftDown, bool isAltDown, bool isInverse = false)
        {
            var mode = DetermineSelectionMode(isShiftDown, isAltDown);

            // If clicking inside an active selection with Replace mode, lift & drag
            if (_selection.HasActiveSelection && _selection.IsPixelInSelection(x, y) && mode == SelectionMode.Replace)
            {
                // Drag is handled by TryBeginDrag, called from the View with screen-space coords
                return;
            }

            if (mode == SelectionMode.Replace)
            {
                CommitIfActive();
            }
            else if (_selection.IsFloating)
            {
                // Preserve the existing mask before committing, so we can apply the modifier mode
                var oldMask = _selection.Mask;
                var oldMinX = _selection.MinX;
                var oldMinY = _selection.MinY;
                var oldMaxX = _selection.MaxX;
                var oldMaxY = _selection.MaxY;
                CommitIfActive();
                if (oldMinX != -1 && oldMask != null)
                    _selection.ApplyMask(oldMask, oldMinX, oldMinY, oldMaxX, oldMaxY, SelectionMode.Replace);
            }

            if (_vm.CurrentTool == ToolMode.MagicWand)
            {
                var fillMask = _drawingService.GetFloodFillMask(_vm.SpriteState, x, y,
                    out int minX, out int minY, out int maxX, out int maxY);

                if (isInverse)
                {
                    int w = _vm.SpriteState.Width;
                    int h = _vm.SpriteState.Height;
                    var inverseMask = new bool[w, h];
                    int fw = fillMask.GetLength(0);
                    int fh = fillMask.GetLength(1);

                    for (int cy = 0; cy < h; cy++)
                    {
                        for (int cx = 0; cx < w; cx++)
                        {
                            int fx = cx - minX;
                            int fy = cy - minY;
                            bool isFilled = fx >= 0 && fx < fw && fy >= 0 && fy < fh && fillMask[fx, fy];
                            inverseMask[cx, cy] = !isFilled;
                        }
                    }
                    _selection.ApplyMask(inverseMask, 0, 0, w - 1, h - 1, mode);
                }
                else
                {
                    _selection.ApplyMask(fillMask, minX, minY, maxX, maxY, mode);
                }
                _vm.RedrawGridFromMemory();
            }
            else
            {
                _selectionAnchorX = x;
                _selectionAnchorY = y;
                if (_vm.CurrentTool == ToolMode.Lasso)
                    _selection.BeginLassoSelection(x, y, mode);
                else
                    _selection.BeginRectangleSelection(x, y, mode);
            }
        }

        private void HandleMove(int x, int y, bool isShiftDown)
        {
            if (!_selection.IsSelecting) return;

            if (_vm.CurrentTool == ToolMode.Lasso)
            {
                _selection.AddLassoPoint(x, y);
            }
            else
            {
                // Constrain to square when Shift is held
                if (isShiftDown && _selectionAnchorX != -1 && _selectionAnchorY != -1)
                {
                    int dx = x - _selectionAnchorX;
                    int dy = y - _selectionAnchorY;
                    int maxDist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    x = _selectionAnchorX + Math.Sign(dx) * maxDist;
                    y = _selectionAnchorY + Math.Sign(dy) * maxDist;
                }
                _selection.UpdateRectangleSelection(x, y);
            }
        }

        private void HandleUp()
        {
            if (_selection.IsSelecting)
                _selection.FinalizeSelection();
        }

        private static SelectionMode DetermineSelectionMode(bool isShiftDown, bool isAltDown)
        {
            if (isShiftDown && isAltDown) return SelectionMode.Intersect;
            if (isShiftDown) return SelectionMode.Add;
            if (isAltDown) return SelectionMode.Subtract;
            return SelectionMode.Replace;
        }
    }
}
