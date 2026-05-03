using System;
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

        // Flags read by the View to know whether a shape preview is in progress
        public bool IsDrawingLine { get; private set; }
        public bool IsDrawingRectangle { get; private set; }
        public bool IsDrawingEllipse { get; private set; }
        public bool IsDrawingFilledRectangle { get; private set; }
        public bool IsDrawingFilledEllipse { get; private set; }

        public ToolInputController(MainViewModel vm, IDrawingService drawingService, BitmapPreviewRenderer preview)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        }

        /// <summary>
        /// Main entry point for all non-selection tool input from the View.
        /// Uses typed enums instead of the previous magic strings and bool? tri-state.
        /// </summary>
        public void ProcessToolInput(int x, int y, ToolAction action, DrawMode mode, bool isShiftDown, bool isAltDown = false)
        {
            // Marquee and Lasso are handled entirely in the View via SelectionService
            if (_vm.CurrentTool == ToolMode.Marquee || _vm.CurrentTool == ToolMode.Lasso) return;

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
            if (IsDrawingLine || IsDrawingRectangle || IsDrawingEllipse ||
                IsDrawingFilledRectangle || IsDrawingFilledEllipse)
            {
                IsDrawingLine = false;
                IsDrawingRectangle = false;
                IsDrawingEllipse = false;
                IsDrawingFilledRectangle = false;
                IsDrawingFilledEllipse = false;
                ResetLineTracking();
                _vm.RedrawGridFromMemory();  // remove any shape preview
            }
            _pendingTextUpdateDuringDrag = false;
        }

        // ── Private: tool dispatch ────────────────────────────────────────

        private void HandleToolDown(int x, int y, DrawMode mode, bool isShiftDown)
        {
            bool newState = mode == DrawMode.Draw;

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

                case ToolMode.Line:
                    IsDrawingLine = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    _preview.PreviewLine(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Rectangle:
                    IsDrawingRectangle = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    _preview.PreviewRectangle(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Ellipse:
                    IsDrawingEllipse = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    _preview.PreviewEllipse(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.FilledRectangle:
                    IsDrawingFilledRectangle = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    _preview.PreviewFilledRectangle(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.FilledEllipse:
                    IsDrawingFilledEllipse = true;
                    _lineStartX = x;
                    _lineStartY = y;
                    _lineCurrentX = x;
                    _lineCurrentY = y;
                    _lineDrawState = newState;
                    _preview.PreviewFilledEllipse(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, _lineDrawState);
                    break;

                case ToolMode.Pencil:
                    if (isShiftDown && _lastClickedX != NoPosition)
                    {
                        _vm.SaveStateForUndo();
                        _drawingService.DrawLine(_vm.SpriteState, _lastClickedX, _lastClickedY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        _vm.RedrawGridFromMemory();
                        _vm.UpdateTextOutputs();
                    }
                    else
                    {
                        _vm.SaveStateForUndo();
                        _drawingService.DrawBrushStamp(_vm.SpriteState, x, y, _vm.BrushSize, newState, _vm.BrushShape, _vm.BrushAngle);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        _vm.RedrawGridFromMemory();
                        _vm.UpdateTextOutputs();
                    }
                    break;
            }
        }

        private void HandleToolMove(int x, int y, DrawMode mode, bool isShiftDown, bool isAltDown)
        {
            bool newState = mode == DrawMode.Draw;

            switch (_vm.CurrentTool)
            {
                case ToolMode.Line when IsDrawingLine:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Line, isShiftDown, isAltDown);
                        _preview.PreviewLine(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Rectangle when IsDrawingRectangle:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Rectangle, isShiftDown, isAltDown);
                        _preview.PreviewRectangle(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Ellipse when IsDrawingEllipse:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Ellipse, isShiftDown, isAltDown);
                        _preview.PreviewEllipse(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.FilledRectangle when IsDrawingFilledRectangle:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.FilledRectangle, isShiftDown, isAltDown);
                        _preview.PreviewFilledRectangle(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.FilledEllipse when IsDrawingFilledEllipse:
                    if (_lineCurrentX != x || _lineCurrentY != y || _lastShiftDown != isShiftDown || _lastAltDown != isAltDown)
                    {
                        _lineCurrentX = x;
                        _lineCurrentY = y;
                        _lastShiftDown = isShiftDown;
                        _lastAltDown = isAltDown;
                        var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.FilledEllipse, isShiftDown, isAltDown);
                        _preview.PreviewFilledEllipse(x0, y0, x1, y1, _lineDrawState);
                    }
                    break;

                case ToolMode.Pencil when mode != DrawMode.None:
                    if (_lastClickedX != NoPosition && (_lastClickedX != x || _lastClickedY != y))
                    {
                        // Continuous pencil drag: draw line segment but don't push undo
                        // (the undo entry was already pushed on Down)
                        _drawingService.DrawLine(_vm.SpriteState, _lastClickedX, _lastClickedY, x, y, newState, _vm.BrushSize, _vm.BrushShape, _vm.BrushAngle);
                        _lastClickedX = x;
                        _lastClickedY = y;
                        _pendingTextUpdateDuringDrag = true;
                        _vm.RedrawGridFromMemory();
                    }
                    break;
            }
        }

        private void HandleToolUp(bool isShiftDown, bool isAltDown)
        {
            if (IsDrawingLine)
            {
                IsDrawingLine = false;
                if (_lineStartX != NoPosition)
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Line, isShiftDown, isAltDown);
                    _drawingService.DrawLine(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }
                ResetLineTracking();
                _vm.UpdateTextOutputs();
            }

            if (IsDrawingRectangle)
            {
                IsDrawingRectangle = false;
                if (_lineStartX != NoPosition)
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Rectangle, isShiftDown, isAltDown);
                    _drawingService.DrawRectangle(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }
                ResetLineTracking();
                _vm.UpdateTextOutputs();
            }

            if (IsDrawingEllipse)
            {
                IsDrawingEllipse = false;
                if (_lineStartX != NoPosition)
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.Ellipse, isShiftDown, isAltDown);
                    _drawingService.DrawEllipse(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }
                ResetLineTracking();
                _vm.UpdateTextOutputs();
            }

            if (IsDrawingFilledRectangle)
            {
                IsDrawingFilledRectangle = false;
                if (_lineStartX != NoPosition)
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.FilledRectangle, isShiftDown, isAltDown);
                    _drawingService.DrawFilledRectangle(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }
                ResetLineTracking();
                _vm.UpdateTextOutputs();
            }

            if (IsDrawingFilledEllipse)
            {
                IsDrawingFilledEllipse = false;
                if (_lineStartX != NoPosition)
                {
                    _vm.SaveStateForUndo();
                    var (x0, y0, x1, y1) = GetConstrainedShapeBounds(_lineStartX, _lineStartY, _lineCurrentX, _lineCurrentY, ToolMode.FilledEllipse, isShiftDown, isAltDown);
                    _drawingService.DrawFilledEllipse(_vm.SpriteState, x0, y0, x1, y1, _lineDrawState);
                    _lastClickedX = x1;
                    _lastClickedY = y1;
                    _vm.RedrawGridFromMemory();
                }
                ResetLineTracking();
                _vm.UpdateTextOutputs();
            }

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
