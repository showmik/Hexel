using System;
using System.Collections.Generic;
using Hexprite.Core;
using Hexprite.Rendering;
using Hexprite.Services;
using Hexprite.ViewModels;

namespace Hexprite.Controllers
{
    /// <summary>
    /// Owns the tool input state machine: down/move/up dispatch, shape constraint
    /// logic, drawing-in-progress tracking, and preview orchestration.
    /// Extracted from MainViewModel to separate input handling from ViewModel state.
    /// </summary>
    public class ToolInputController
    {
        private readonly MainViewModel _vm;
        private readonly IDrawingService _drawingService;
        private readonly BitmapPreviewRenderer _preview;

        // ── Internal drawing tracking ─────────────────────────────────────
        private const int NoPosition = int.MinValue;
        private int _lineStartX = NoPosition;
        private int _lineStartY = NoPosition;
        private int _lineCurrentX = NoPosition;
        private int _lineCurrentY = NoPosition;
        private bool _lineDrawState = false;
        private int _lastClickedX = NoPosition;
        private int _lastClickedY = NoPosition;
        private bool _pendingTextUpdateDuringDrag;
        private bool _lastShiftDown;
        private bool _lastAltDown;
        private readonly List<(int x, int y)> _pixelPerfectStrokePath = new();
        private readonly Dictionary<int, bool> _pixelPerfectOriginalStates = new();

        // ── Shape drawing state ───────────────────────────────────────────
        // Replaces the five individual IsDrawingXxx booleans with a single
        // flag + active tool, eliminating massive duplication in handlers.
        private bool _isDrawingShape;
        private ToolMode _activeShapeTool;

        // Computed properties preserve the existing public API
        public bool IsDrawingLine => _isDrawingShape && _activeShapeTool == ToolMode.Line;
        public bool IsDrawingRectangle => _isDrawingShape && _activeShapeTool == ToolMode.Rectangle;
        public bool IsDrawingEllipse => _isDrawingShape && _activeShapeTool == ToolMode.Ellipse;
        public bool IsDrawingFilledRectangle => _isDrawingShape && _activeShapeTool == ToolMode.FilledRectangle;
        public bool IsDrawingFilledEllipse => _isDrawingShape && _activeShapeTool == ToolMode.FilledEllipse;

        // ── Shape tool dispatch tables ────────────────────────────────────
        // Maps each shape ToolMode to its preview callback and commit callback,
        // collapsing 5 copy-pasted branches into a single generic path.

        private delegate void PreviewAction(int x0, int y0, int x1, int y1, bool state);
        private delegate void CommitAction(SpriteState s, int x0, int y0, int x1, int y1, bool state);

        private readonly Dictionary<ToolMode, PreviewAction> _shapePreviewMap;
        private readonly Dictionary<ToolMode, CommitAction> _shapeCommitMap;

        // The set of tool modes that are handled as "shapes" (drag-to-draw)
        private static readonly HashSet<ToolMode> ShapeTools = new()
        {
            ToolMode.Line,
            ToolMode.Rectangle,
            ToolMode.Ellipse,
            ToolMode.FilledRectangle,
            ToolMode.FilledEllipse
        };

        public ToolInputController(MainViewModel vm, IDrawingService drawingService, BitmapPreviewRenderer preview)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));

            // Wire up shape tool dispatch tables
            _shapePreviewMap = new Dictionary<ToolMode, PreviewAction>
            {
                { ToolMode.Line,            _preview.PreviewLine },
                { ToolMode.Rectangle,       _preview.PreviewRectangle },
                { ToolMode.Ellipse,         _preview.PreviewEllipse },
                { ToolMode.FilledRectangle, _preview.PreviewFilledRectangle },
                { ToolMode.FilledEllipse,   _preview.PreviewFilledEllipse },
            };

            _shapeCommitMap = new Dictionary<ToolMode, CommitAction>
            {
                { ToolMode.Line,            (s, x0, y0, x1, y1, st) => _drawingService.DrawLine(s, x0, y0, x1, y1, st) },
                { ToolMode.Rectangle,       (s, x0, y0, x1, y1, st) => _drawingService.DrawRectangle(s, x0, y0, x1, y1, st) },
                { ToolMode.Ellipse,         (s, x0, y0, x1, y1, st) => _drawingService.DrawEllipse(s, x0, y0, x1, y1, st) },
                { ToolMode.FilledRectangle, (s, x0, y0, x1, y1, st) => _drawingService.DrawFilledRectangle(s, x0, y0, x1, y1, st) },
                { ToolMode.FilledEllipse,   (s, x0, y0, x1, y1, st) => _drawingService.DrawFilledEllipse(s, x0, y0, x1, y1, st) },
            };
        }

        /// <summary>
        /// Main entry point for all non-selection tool input from the View.
        /// Uses typed enums instead of the previous magic strings and bool? tri-state.
        /// </summary>
        public void ProcessToolInput(int x, int y, ToolAction action, DrawMode mode, bool isShiftDown, bool isAltDown = false)
        {
            if (_vm.CurrentTool == ToolMode.Marquee || _vm.CurrentTool == ToolMode.Lasso) return;

            // Only clip to the selection when one is active and NOT floating.
            // A floating layer has already been committed to the canvas by this point;
            // the residual non-floating mask is what constrains drawing.
            var sel = _vm.SelectionService;
            bool hasClip = sel.HasActiveSelection && !sel.IsFloating;
            _drawingService.SetSelectionClip(hasClip ? sel : null);

            switch (action)
            {
                case ToolAction.Down:
                    HandleToolDown(x, y, mode, isShiftDown);
                    break;
                case ToolAction.Move:
                    HandleToolMove(x, y, mode, isShiftDown, isAltDown);
                    break;
                case ToolAction.Up:
                    HandleToolUp(isShiftDown, isAltDown);
                    break;
            }
        }

        /// <summary>
        /// Cancels any in-progress shape drawing and resets tracking state.
        /// Called by the View when switching tools to prevent stale draw flags.
        /// </summary>
        public void CancelInProgressDrawing()
        {
            if (_isDrawingShape)
            {
                _isDrawingShape = false;
                ResetLineTracking();
                _vm.RedrawGridFromMemory();  // remove any shape preview
            }
            ClearPixelPerfectStrokeState();
            _pendingTextUpdateDuringDrag = false;
        }

        // ── Private: tool dispatch ────────────────────────────────────────

        private void HandleToolDown(int x, int y, DrawMode mode, bool isShiftDown)
        {
            if (_vm.IsActiveLayerLocked)
                return;

            bool newState = mode == DrawMode.Draw;

            // ── Shape tools (Line, Rectangle, Ellipse, FilledRectangle, FilledEllipse) ──
            if (ShapeTools.Contains(_vm.CurrentTool))
            {
                _isDrawingShape = true;
                _activeShapeTool = _vm.CurrentTool;
                _lineStartX = x;
                _lineStartY = y;
                _lineCurrentX = x;
                _lineCurrentY = y;
                _lineDrawState = newState;
                _shapePreviewMap[_activeShapeTool](x, y, x, y, newState);
                return;
            }

            switch (_vm.CurrentTool)
            {
                case ToolMode.Fill:
                    // BUG FIX: previously SaveStateForUndo() was called here AND inside
                    // ApplyFloodFill, resulting in two undo entries per fill operation.
                    // History is now saved exactly once, here at the call site.
                    _vm.SaveStateForUndo();
                    _drawingService.ApplyFloodFill(_vm.SpriteState, x, y, newState);
                    _lastClickedX = x;
                    _lastClickedY = y;
                    _vm.RedrawGridFromMemory();
                    _vm.MarkCodeStale();
                    break;

                case ToolMode.Pencil:
                    _vm.BeginStrokeRenderSession();
                    ClearPixelPerfectStrokeState();
                    if (isShiftDown && _lastClickedX != NoPosition)
                    {
                        _vm.SaveStateForUndo();
                        if (ShouldUsePixelPerfect())
                        {
                            ApplyPixelPerfectSegment(_lastClickedX, _lastClickedY, x, y, newState, skipFirstPoint: false);
                        }
                        else
                        {
                            _drawingService.DrawLine(_vm.SpriteState, _lastClickedX, _lastClickedY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);

                            int brushMargin1 = ComputeDirtyMargin();
                            _vm.RedrawRegion(
                                Math.Min(_lastClickedX, x) - brushMargin1,
                                Math.Min(_lastClickedY, y) - brushMargin1,
                                Math.Max(_lastClickedX, x) + brushMargin1,
                                Math.Max(_lastClickedY, y) + brushMargin1,
                                updatePreviewSimulation: false);
                        }

                        _lastClickedX = x;
                        _lastClickedY = y;
                        _pendingTextUpdateDuringDrag = true;
                    }
                    else
                    {
                        _vm.SaveStateForUndo();
                        if (ShouldUsePixelPerfect())
                        {
                            PlotPixelPerfectPoint(x, y, newState);
                        }
                        else
                        {
                            _drawingService.DrawBrushStamp(_vm.SpriteState, x, y, _vm.BrushSize, newState, _vm.BrushShape, _vm.BrushAngle);
                        }
                        _lastClickedX = x;
                        _lastClickedY = y;

                        if (!ShouldUsePixelPerfect())
                        {
                            int brushMargin2 = ComputeDirtyMargin();
                            _vm.RedrawRegion(
                                x - brushMargin2,
                                y - brushMargin2,
                                x + brushMargin2,
                                y + brushMargin2,
                                updatePreviewSimulation: false);
                        }
                        _pendingTextUpdateDuringDrag = true;
                    }
                    break;
            }
        }

        private void HandleToolMove(int x, int y, DrawMode mode, bool isShiftDown, bool isAltDown)
        {
            using var perfScope = _vm.BeginMovePerfScope();
            if (_vm.IsActiveLayerLocked)
                return;

            bool newState = mode == DrawMode.Draw;

            // ── Shape tool move (generic for all shape tools) ──
            if (_isDrawingShape && _vm.CurrentTool == _activeShapeTool)
            {
                if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                {
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lastShiftDown = isShiftDown;
                    _lastAltDown = isAltDown;
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _activeShapeTool, isShiftDown, isAltDown);
                    _shapePreviewMap[_activeShapeTool](x0, y0, x1, y1, _lineDrawState);
                }
                return;
            }

            // ── Pencil drag ──
            if (_vm.CurrentTool == ToolMode.Pencil && mode != DrawMode.None)
            {
                if (_lastClickedX != NoPosition && (_lastClickedX != x || _lastClickedY != y))
                {
                    int prevX = _lastClickedX, prevY = _lastClickedY;

                    // Continuous pencil drag: draw line segment but don't push undo
                    // (the undo entry was already pushed on Down)
                    if (ShouldUsePixelPerfect())
                    {
                        ApplyPixelPerfectSegment(prevX, prevY, x, y, newState, skipFirstPoint: true);
                    }
                    else
                    {
                        _drawingService.DrawLine(_vm.SpriteState, prevX, prevY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);
                    }
                    _lastClickedX = x;
                    _lastClickedY = y;
                    _pendingTextUpdateDuringDrag = true;

                    if (!ShouldUsePixelPerfect())
                    {
                        // Partial redraw: only update the stroke segment footprint plus
                        // a conservative 1px safety margin for brush edge rounding.
                        int brushMargin = ComputeDirtyMargin();
                        int dirtyMinX = Math.Min(prevX, x) - brushMargin;
                        int dirtyMinY = Math.Min(prevY, y) - brushMargin;
                        int dirtyMaxX = Math.Max(prevX, x) + brushMargin;
                        int dirtyMaxY = Math.Max(prevY, y) + brushMargin;
                        _vm.RedrawRegion(dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY, updatePreviewSimulation: false);
                    }
                }
            }
        }

        private void HandleToolUp(bool isShiftDown, bool isAltDown)
        {
            // ── Shape tool commit (generic for all shape tools) ──
            if (_isDrawingShape)
            {
                var tool = _activeShapeTool;
                _isDrawingShape = false;

                if (_lineStartX != NoPosition && _shapeCommitMap.TryGetValue(tool, out var commitAction))
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, tool, isShiftDown, isAltDown);
                    commitAction(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }

                ResetLineTracking();
                _vm.MarkCodeStale();
                return;
            }

            // ── Pencil deferred text update ──
            if (_vm.CurrentTool == ToolMode.Pencil && _pendingTextUpdateDuringDrag)
            {
                _pendingTextUpdateDuringDrag = false;
                ClearPixelPerfectStrokeState();
                _vm.MarkCodeStale();
                _vm.EndStrokeRenderSession();
                _vm.UpdatePreviewSimulation();
            }
        }

        // ── Shape constraint logic ────────────────────────────────────────

        private (int x0, int y0, int x1, int y1) GetConstrainedShapeBounds(int startX, int startY, int currentX, int currentY, ToolMode tool, bool isShift, bool isAlt)
        {
            int targetX = currentX;
            int targetY = currentY;

            if (tool == ToolMode.Line)
            {
                if (isShift)
                {
                    double angle = Math.Atan2(targetY - startY, targetX - startX);
                    angle = Math.Round(angle / (Math.PI / 12.0)) * (Math.PI / 12.0);
                    double dist = Math.Sqrt(Math.Pow(targetX - startX, 2) + Math.Pow(targetY - startY, 2));
                    targetX = startX + (int)Math.Round(Math.Cos(angle) * dist);
                    targetY = startY + (int)Math.Round(Math.Sin(angle) * dist);
                }

                int x0 = startX;
                int y0 = startY;
                if (isAlt)
                {
                    x0 = 2 * startX - targetX;
                    y0 = 2 * startY - targetY;
                }
                return (x0, y0, targetX, targetY);
            }
            else
            {
                if (isShift)
                {
                    int dx = currentX - startX;
                    int dy = currentY - startY;
                    int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    targetX = startX + (dx >= 0 ? side : -side);
                    targetY = startY + (dy >= 0 ? side : -side);
                }

                int x0 = startX;
                int y0 = startY;
                if (isAlt)
                {
                    x0 = 2 * startX - targetX;
                    y0 = 2 * startY - targetY;
                }
                return (x0, y0, targetX, targetY);
            }
        }

        private void ResetLineTracking()
        {
            _lineStartX = NoPosition;
            _lineStartY = NoPosition;
            _lineCurrentX = NoPosition;
            _lineCurrentY = NoPosition;
        }

        private int ComputeDirtyMargin()
        {
            int brushRadius = (_vm.BrushSize - 1) / 2;
            return Math.Max(2, brushRadius + 2);
        }

        private bool ShouldUsePixelPerfect()
            => _vm.CurrentTool == ToolMode.Pencil && _vm.IsPixelPerfectEnabled && _vm.BrushSize == 1;

        private void ClearPixelPerfectStrokeState()
        {
            _pixelPerfectStrokePath.Clear();
            _pixelPerfectOriginalStates.Clear();
        }

        private void ApplyPixelPerfectSegment(int x0, int y0, int x1, int y1, bool newState, bool skipFirstPoint)
        {
            bool first = true;
            foreach (var (x, y) in EnumerateLinePoints(x0, y0, x1, y1))
            {
                if (skipFirstPoint && first)
                {
                    first = false;
                    continue;
                }

                first = false;
                PlotPixelPerfectPoint(x, y, newState);
            }
        }

        private void PlotPixelPerfectPoint(int x, int y, bool newState)
        {
            var state = _vm.SpriteState;
            if (x < 0 || x >= state.Width || y < 0 || y >= state.Height)
                return;

            int index = (y * state.Width) + x;
            bool before = state.Pixels[index];
            if (!_pixelPerfectOriginalStates.ContainsKey(index))
                _pixelPerfectOriginalStates[index] = before;

            _drawingService.DrawBrushStamp(state, x, y, 1, newState);
            bool after = state.Pixels[index];
            if (after != newState)
                return;

            // If the pixel was already in the desired state before this stroke,
            // it wasn't actually changed — don't treat it as part of the stroke path.
            // Otherwise the corner-removal algorithm may erase genuinely drawn pixels.
            if (before == newState)
                return;

            if (_pixelPerfectStrokePath.Count == 0 || _pixelPerfectStrokePath[^1] != (x, y))
                _pixelPerfectStrokePath.Add((x, y));

            _vm.RedrawRegion(x, y, x, y, updatePreviewSimulation: false);
            TryRemovePixelPerfectCorner();
        }

        private void TryRemovePixelPerfectCorner()
        {
            if (_pixelPerfectStrokePath.Count < 3)
                return;

            var a = _pixelPerfectStrokePath[^3];
            var b = _pixelPerfectStrokePath[^2];
            var c = _pixelPerfectStrokePath[^1];

            int abDx = b.x - a.x;
            int abDy = b.y - a.y;
            int bcDx = c.x - b.x;
            int bcDy = c.y - b.y;
            int acDx = c.x - a.x;
            int acDy = c.y - a.y;

            bool isAxisAlignedSteps =
                Math.Abs(abDx) + Math.Abs(abDy) == 1 &&
                Math.Abs(bcDx) + Math.Abs(bcDy) == 1;
            bool isRightAngleTurn = (abDx != bcDx || abDy != bcDy) && ((abDx == 0 && bcDx != 0) || (abDy == 0 && bcDy != 0));
            bool isStaircaseCorner = Math.Abs(acDx) == 1 && Math.Abs(acDy) == 1;
            if (!isAxisAlignedSteps || !isRightAngleTurn || !isStaircaseCorner)
                return;

            int indexB = (b.y * _vm.SpriteState.Width) + b.x;
            if (_pixelPerfectOriginalStates.TryGetValue(indexB, out bool originalState))
            {
                _drawingService.DrawBrushStamp(_vm.SpriteState, b.x, b.y, 1, originalState);
                _vm.RedrawRegion(b.x, b.y, b.x, b.y, updatePreviewSimulation: false);
                _pixelPerfectStrokePath.RemoveAt(_pixelPerfectStrokePath.Count - 2);
            }
        }

        private static IEnumerable<(int x, int y)> EnumerateLinePoints(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                yield return (x0, y0);
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }
    }
}
