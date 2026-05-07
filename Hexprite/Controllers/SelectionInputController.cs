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
        private bool _wasSelectionActiveOnDown;

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
        public bool CommitIfActive(bool saveHistory = true)
        {
            if (!_selection.HasActiveSelection) return false;

            if (_selection.IsTransforming)
                CommitTransformIfActive();

            bool wasFloating = _selection.IsFloating;

            if (saveHistory && wasFloating)
                _vm.SaveStateForUndo();

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

        /// <summary>
        /// Lifts the active selection in place (without moving it) so transform handles
        /// appear immediately. No-op if there is no active selection or it is already floating.
        /// </summary>
        public void EnterTransformMode()
        {
            if (!_selection.HasActiveSelection) return;
            if (_selection.IsFloating) return;

            _vm.SaveStateForUndo();
            _selection.LiftSelection(_vm.SpriteState);
            _vm.RedrawGridFromMemory();
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

            // Capture if a selection exists BEFORE we potentially commit it in Replace mode
            _wasSelectionActiveOnDown = _selection.HasActiveSelection;

            if (mode == SelectionMode.Replace)
            {
                CommitIfActive();
                // If we committed, the new selection is starting fresh, so we allow constraint
                _wasSelectionActiveOnDown = false;
            }
            else if (_selection.IsFloating)
            {
                CommitIfActive();
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
                // Constrain to square when Shift is held, but only if we didn't start 
                // with an existing selection (in which case Shift is purely for 'Add' mode)
                if (isShiftDown && !_wasSelectionActiveOnDown && _selectionAnchorX != -1 && _selectionAnchorY != -1)
                {
                    int dx = x - _selectionAnchorX;
                    int dy = y - _selectionAnchorY;
                    int maxDist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int signX = dx == 0 ? 1 : Math.Sign(dx);
                    int signY = dy == 0 ? 1 : Math.Sign(dy);
                    x = _selectionAnchorX + signX * maxDist;
                    y = _selectionAnchorY + signY * maxDist;
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

        // ── Floating selection resize (transform handles) ─────────────────

        private int _transformAnchorOx;
        private int _transformAnchorOy;
        private int _transformAnchorOw;
        private int _transformAnchorOh;

        /// <summary>
        /// Hit-tests transform handles in image-local coordinates (same space as <see cref="MainWindow.GetPixelCoordinates"/> input).
        /// </summary>
        public TransformHandle HitTestHandle(double mouseImgX, double mouseImgY, double actualW, double actualH)
        {
            if (!_selection.IsFloating || !_selection.HasActiveSelection || actualW <= 0 || actualH <= 0)
                return TransformHandle.None;

            int cw = _vm.SpriteState.Width;
            int ch = _vm.SpriteState.Height;

            double px = mouseImgX / actualW * cw - 0.5;
            double py = mouseImgY / actualH * ch - 0.5;

            int fx = _selection.FloatingX;
            int fy = _selection.FloatingY;
            int fw = _selection.FloatingWidth;
            int fh = _selection.FloatingHeight;

            double cellMin = Math.Min((double)cw, (double)ch);
            double hitR = Math.Max(cellMin * 0.10, Math.Max(14.0 / actualW * cw, 1.25));

            bool Near(double hx, double hy)
            {
                double dx = px - hx, dy = py - hy;
                return dx * dx + dy * dy <= hitR * hitR;
            }

            double midX = fx + (fw - 1) * 0.5;
            double midY = fy + (fh - 1) * 0.5;
            int right = fx + fw - 1;
            int bottom = fy + fh - 1;

            if (Near(fx, fy)) return TransformHandle.NW;
            if (Near(right, fy)) return TransformHandle.NE;
            if (Near(fx, bottom)) return TransformHandle.SW;
            if (Near(right, bottom)) return TransformHandle.SE;
            if (Near(midX, fy)) return TransformHandle.N;
            if (Near(midX, bottom)) return TransformHandle.S;
            if (Near(fx, midY)) return TransformHandle.W;
            if (Near(right, midY)) return TransformHandle.E;

            return TransformHandle.None;
        }

        /// <summary>
        /// Starts a resize drag from a handle. Lifts the selection if it is not already floating.
        /// </summary>
        public bool TryBeginTransform(TransformHandle handle)
        {
            if (handle == TransformHandle.None || !_selection.HasActiveSelection)
                return false;

            if (!_selection.IsFloating)
            {
                _vm.SaveStateForUndo();
                _selection.LiftSelection(_vm.SpriteState);
            }
            else
            {
                _vm.SaveStateForUndo();
            }

            _transformAnchorOx = _selection.FloatingX;
            _transformAnchorOy = _selection.FloatingY;
            _transformAnchorOw = _selection.FloatingWidth;
            _transformAnchorOh = _selection.FloatingHeight;

            _selection.BeginTransform(handle);
            _vm.RedrawGridFromMemory();
            return true;
        }

        /// <summary>
        /// Applies resize delta in pixel space relative to the mouse-down anchor (same as marquee drag deltas).
        /// </summary>
        public void UpdateTransformFromDelta(int deltaX, int deltaY, bool shiftAspect, bool altFromCenter)
        {
            if (!_selection.IsTransforming)
                return;

            int oldX = _selection.FloatingX;
            int oldY = _selection.FloatingY;
            int oldW = _selection.FloatingWidth;
            int oldH = _selection.FloatingHeight;

            var handle = _selection.ActiveTransformHandle;
            var (x, y, w, h, flipX, flipY) = ComputeResizeRect(
                handle,
                _transformAnchorOx, _transformAnchorOy, _transformAnchorOw, _transformAnchorOh,
                deltaX, deltaY,
                shiftAspect, altFromCenter);

            _selection.UpdateTransform(x, y, w, h, flipX, flipY);

            int newX = _selection.FloatingX;
            int newY = _selection.FloatingY;
            int newW = _selection.FloatingWidth;
            int newH = _selection.FloatingHeight;

            int minX = Math.Min(oldX, newX);
            int minY = Math.Min(oldY, newY);
            int maxX = Math.Max(oldX + oldW - 1, newX + newW - 1);
            int maxY = Math.Max(oldY + oldH - 1, newY + newH - 1);

            const int padding = 1;
            _vm.RedrawRegion(minX - padding, minY - padding, maxX + padding, maxY + padding, updatePreviewSimulation: false);
        }

        public void CommitTransformIfActive()
        {
            if (!_selection.IsTransforming)
                return;

            _selection.CommitTransform();
            _vm.MarkCodeStale();
            _vm.RedrawGridFromMemory();
        }

        public void CancelTransformIfActive()
        {
            if (!_selection.IsTransforming)
                return;

            _selection.CancelTransform();
            _vm.RedrawGridFromMemory();
        }

        /// <summary>Computes the floating bounding rect after a resize drag.</summary>
        internal static (int x, int y, int w, int h, bool flipX, bool flipY) ComputeResizeRect(
            TransformHandle handle,
            int ox, int oy, int ow, int oh,
            int dx, int dy,
            bool shiftAspect, bool altFromCenter)
        {
            int left = ox;
            int top = oy;
            int right = ox + ow - 1;
            int bottom = oy + oh - 1;

            bool moveLeft = handle is TransformHandle.NW or TransformHandle.SW or TransformHandle.W;
            bool moveRight = handle is TransformHandle.NE or TransformHandle.SE or TransformHandle.E;
            bool moveTop = handle is TransformHandle.NW or TransformHandle.NE or TransformHandle.N;
            bool moveBottom = handle is TransformHandle.SW or TransformHandle.SE or TransformHandle.S;

            int newLeft = left;
            int newRight = right;
            int newTop = top;
            int newBottom = bottom;

            if (moveLeft)
            {
                newLeft = left + dx;
                if (altFromCenter) newRight = right - dx;
            }
            else if (moveRight)
            {
                newRight = right + dx;
                if (altFromCenter) newLeft = left - dx;
            }

            if (moveTop)
            {
                newTop = top + dy;
                if (altFromCenter) newBottom = bottom - dy;
            }
            else if (moveBottom)
            {
                newBottom = bottom + dy;
                if (altFromCenter) newTop = top - dy;
            }

            bool flipX = newRight < newLeft;
            bool flipY = newBottom < newTop;

            int normLeft = Math.Min(newLeft, newRight);
            int normRight = Math.Max(newLeft, newRight);
            int normTop = Math.Min(newTop, newBottom);
            int normBottom = Math.Max(newTop, newBottom);

            int x = normLeft;
            int y = normTop;
            int w = Math.Max(1, normRight - normLeft + 1);
            int h = Math.Max(1, normBottom - normTop + 1);

            if (shiftAspect && ow > 0 && oh > 0 && handle != TransformHandle.None)
            {
                double sx = w / (double)ow;
                double sy = h / (double)oh;
                double s = Math.Max(Math.Abs(sx), Math.Abs(sy));

                int nw = Math.Max(1, (int)Math.Round(ow * s));
                int nh = Math.Max(1, (int)Math.Round(oh * s));

                if (altFromCenter)
                {
                    double cx = ox + ow / 2.0;
                    double cy = oy + oh / 2.0;
                    x = (int)Math.Round(cx - nw / 2.0);
                    y = (int)Math.Round(cy - nh / 2.0);
                    w = nw;
                    h = nh;
                    return (x, y, w, h, flipX, flipY);
                }

                // Re-anchor to the same fixed edges (opposite the dragged edges),
                // taking flips into account.
                if (moveLeft)
                {
                    int fixedRight = right;
                    x = flipX ? fixedRight : fixedRight - nw + 1;
                }
                else if (moveRight)
                {
                    int fixedLeft = left;
                    x = flipX ? fixedLeft - nw + 1 : fixedLeft;
                }
                else
                {
                    x = left;
                }

                if (moveTop)
                {
                    int fixedBottom = bottom;
                    y = flipY ? fixedBottom : fixedBottom - nh + 1;
                }
                else if (moveBottom)
                {
                    int fixedTop = top;
                    y = flipY ? fixedTop - nh + 1 : fixedTop;
                }
                else
                {
                    y = top;
                }

                w = nw;
                h = nh;
            }

            return (x, y, w, h, flipX, flipY);
        }
    }
}
