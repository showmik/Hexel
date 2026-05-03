using System;
using System.Collections.Generic;
using Hexel.Core;
using Hexel.Rendering;
using Hexel.Services;
using Hexel.ViewModels;

namespace Hexel.Controllers
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
            // Marquee and Lasso are handled entirely in the View via SelectionService
            if (_vm.CurrentTool == ToolMode.Marquee || _vm.CurrentTool == ToolMode.Lasso) return;

            // Activate selection clipping so drawing only affects selected pixels
            _drawingService.SetSelectionClip(_vm.SelectionService);

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
            _pendingTextUpdateDuringDrag = false;
        }

        // ── Private: tool dispatch ────────────────────────────────────────

        private void HandleToolDown(int x, int y, DrawMode mode, bool isShiftDown)
        {
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
                    _vm.UpdateTextOutputs();
                    break;

                case ToolMode.Pencil:
                    if (isShiftDown && _lastClickedX != NoPosition)
                    {
                        _vm.SaveStateForUndo();
                        _drawingService.DrawLine(_vm.SpriteState, _lastClickedX, _lastClickedY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);

                        // Partial redraw for the line segment — use full brush size
                        // as margin to account for rotated Square/Line brush offsets
                        int brushMargin1 = _vm.BrushSize + 1;
                        _vm.RedrawRegion(
                            Math.Min(_lastClickedX, x) - brushMargin1,
                            Math.Min(_lastClickedY, y) - brushMargin1,
                            Math.Max(_lastClickedX, x) + brushMargin1,
                            Math.Max(_lastClickedY, y) + brushMargin1);

                        _lastClickedX = x;
                        _lastClickedY = y;
                        _pendingTextUpdateDuringDrag = true;
                    }
                    else
                    {
                        _vm.SaveStateForUndo();
                        _drawingService.DrawBrushStamp(_vm.SpriteState, x, y, _vm.BrushSize, newState, _vm.BrushShape, _vm.BrushAngle);
                        _lastClickedX = x;
                        _lastClickedY = y;

                        // Partial redraw for the single stamp — use full brush size
                        // as margin to account for rotated Square/Line brush offsets
                        int brushMargin2 = _vm.BrushSize + 1;
                        _vm.RedrawRegion(x - brushMargin2, y - brushMargin2, x + brushMargin2, y + brushMargin2);
                        _pendingTextUpdateDuringDrag = true;
                    }
                    break;
            }
        }

        private void HandleToolMove(int x, int y, DrawMode mode, bool isShiftDown, bool isAltDown)
        {
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
                    _drawingService.DrawLine(_vm.SpriteState, prevX, prevY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);
                    _lastClickedX = x;
                    _lastClickedY = y;
                    _pendingTextUpdateDuringDrag = true;

                    // Partial redraw: only update the bounding box of the stroke segment
                    // Use full brush size as margin to handle rotated brush offsets
                    int brushMargin = _vm.BrushSize + 1;
                    int dirtyMinX = Math.Min(prevX, x) - brushMargin;
                    int dirtyMinY = Math.Min(prevY, y) - brushMargin;
                    int dirtyMaxX = Math.Max(prevX, x) + brushMargin;
                    int dirtyMaxY = Math.Max(prevY, y) + brushMargin;
                    _vm.RedrawRegion(dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);
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
                _vm.UpdateTextOutputs();
                return;
            }

            // ── Pencil deferred text update ──
            if (_vm.CurrentTool == ToolMode.Pencil && _pendingTextUpdateDuringDrag)
            {
                _pendingTextUpdateDuringDrag = false;
                _vm.UpdateTextOutputs();
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
    }
}
