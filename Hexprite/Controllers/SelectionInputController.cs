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

            double hitR = Math.Max(10.0 / actualW * cw, 1.25);

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

            var handle = _selection.ActiveTransformHandle;
            var (x, y, w, h) = ComputeResizeRect(
                handle,
                _transformAnchorOx, _transformAnchorOy, _transformAnchorOw, _transformAnchorOh,
                deltaX, deltaY,
                shiftAspect, altFromCenter);

            _selection.UpdateTransform(x, y, w, h);
            _vm.RedrawGridFromMemory();
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
        internal static (int x, int y, int w, int h) ComputeResizeRect(
            TransformHandle handle,
            int ox, int oy, int ow, int oh,
            int dx, int dy,
            bool shiftAspect, bool altFromCenter)
        {
            int x = ox, y = oy, w = ow, h = oh;

            switch (handle)
            {
                case TransformHandle.NW:
                    if (altFromCenter)
                    {
                        w = ow - 2 * dx;
                        h = oh - 2 * dy;
                        x = ox + dx;
                        y = oy + dy;
                    }
                    else
                    {
                        x = ox + dx;
                        y = oy + dy;
                        w = ow - dx;
                        h = oh - dy;
                    }
                    break;
                case TransformHandle.NE:
                    if (altFromCenter)
                    {
                        w = ow + 2 * dx;
                        h = oh - 2 * dy;
                        x = ox - dx;
                        y = oy + dy;
                    }
                    else
                    {
                        x = ox;
                        y = oy + dy;
                        w = ow + dx;
                        h = oh - dy;
                    }
                    break;
                case TransformHandle.SW:
                    if (altFromCenter)
                    {
                        w = ow - 2 * dx;
                        h = oh + 2 * dy;
                        x = ox + dx;
                        y = oy - dy;
                    }
                    else
                    {
                        x = ox + dx;
                        y = oy;
                        w = ow - dx;
                        h = oh + dy;
                    }
                    break;
                case TransformHandle.SE:
                    if (altFromCenter)
                    {
                        w = ow + 2 * dx;
                        h = oh + 2 * dy;
                        x = ox - dx;
                        y = oy - dy;
                    }
                    else
                    {
                        x = ox;
                        y = oy;
                        w = ow + dx;
                        h = oh + dy;
                    }
                    break;
                case TransformHandle.N:
                    if (altFromCenter)
                    {
                        h = oh - 2 * dy;
                        y = oy + dy;
                        x = ox;
                        w = ow;
                    }
                    else
                    {
                        x = ox;
                        y = oy + dy;
                        w = ow;
                        h = oh - dy;
                    }
                    break;
                case TransformHandle.S:
                    if (altFromCenter)
                    {
                        h = oh + 2 * dy;
                        y = oy - dy;
                        x = ox;
                        w = ow;
                    }
                    else
                    {
                        x = ox;
                        y = oy;
                        w = ow;
                        h = oh + dy;
                    }
                    break;
                case TransformHandle.W:
                    if (altFromCenter)
                    {
                        w = ow - 2 * dx;
                        x = ox + dx;
                        y = oy;
                        h = oh;
                    }
                    else
                    {
                        x = ox + dx;
                        y = oy;
                        w = ow - dx;
                        h = oh;
                    }
                    break;
                case TransformHandle.E:
                    if (altFromCenter)
                    {
                        w = ow + 2 * dx;
                        x = ox - dx;
                        y = oy;
                        h = oh;
                    }
                    else
                    {
                        x = ox;
                        y = oy;
                        w = ow + dx;
                        h = oh;
                    }
                    break;
            }

            w = Math.Max(1, w);
            h = Math.Max(1, h);

            if (shiftAspect && ow > 0 && oh > 0 && handle != TransformHandle.None)
            {
                // When Alt is held we must keep the selection centered; the dragged
                // handle only determines the scale direction/magnitude.
                if (altFromCenter)
                {
                    double sx = w / (double)ow;
                    double sy = h / (double)oh;
                    double s = Math.Max(sx, sy);

                    w = Math.Max(1, (int)Math.Round(ow * s));
                    h = Math.Max(1, (int)Math.Round(oh * s));

                    // Keep original center fixed (not the currently-anchored side).
                    double cx = ox + ow / 2.0;
                    double cy = oy + oh / 2.0;
                    x = (int)Math.Round(cx - w / 2.0);
                    y = (int)Math.Round(cy - h / 2.0);

                    return (x, y, w, h);
                }

                bool isCorner = handle is TransformHandle.NW or TransformHandle.NE
                    or TransformHandle.SW or TransformHandle.SE;

                if (isCorner)
                {
                    double sx = w / (double)ow;
                    double sy = h / (double)oh;
                    double s = Math.Max(sx, sy);
                    w = Math.Max(1, (int)Math.Round(ow * s));
                    h = Math.Max(1, (int)Math.Round(oh * s));
                    (x, y) = AnchorFixedCorner(handle, ox, oy, ow, oh, w, h);
                }
                else
                {
                    double sx = w / (double)ow;
                    double sy = h / (double)oh;
                    double s = Math.Max(sx, sy);
                    w = Math.Max(1, (int)Math.Round(ow * s));
                    h = Math.Max(1, (int)Math.Round(oh * s));

                    switch (handle)
                    {
                        case TransformHandle.N:
                            x = ox;
                            y = oy + oh - h;
                            break;
                        case TransformHandle.S:
                            x = ox;
                            y = oy;
                            break;
                        case TransformHandle.W:
                            y = oy;
                            x = ox + ow - w;
                            break;
                        case TransformHandle.E:
                            y = oy;
                            x = ox;
                            break;
                    }
                }
            }

            return (x, y, w, h);
        }

        private static (int x, int y) AnchorFixedCorner(
            TransformHandle handle,
            int ox, int oy, int ow, int oh,
            int nw, int nh)
        {
            return handle switch
            {
                TransformHandle.NW => (ox, oy),
                TransformHandle.NE => (ox + ow - nw, oy),
                TransformHandle.SW => (ox, oy + oh - nh),
                TransformHandle.SE => (ox + ow - nw, oy + oh - nh),
                _ => (ox, oy)
            };
        }
    }
}
