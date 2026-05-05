using System;
using System.Collections.Generic;
using Hexprite.Core;

namespace Hexprite.Services
{
    public class SelectionService : ISelectionService
    {
        // ── Status flags ──────────────────────────────────────────────────
        public bool HasActiveSelection { get; private set; }
        public bool IsSelecting { get; private set; }
        public bool IsFloating { get; private set; }
        public bool IsDragging { get; private set; }

        // ── Selection bounds ──────────────────────────────────────────────
        public int MinX { get; private set; } = -1;
        public int MaxX { get; private set; } = -1;
        public int MinY { get; private set; } = -1;
        public int MaxY { get; private set; } = -1;
        public bool[,]? Mask { get; private set; }

        // ── Floating layer ────────────────────────────────────────────────
        public bool[,]? FloatingPixels { get; private set; }
        public int FloatingX { get; private set; }
        public int FloatingY { get; private set; }
        public int FloatingWidth { get; private set; }
        public int FloatingHeight { get; private set; }

        // ── Lasso path ────────────────────────────────────────────────────
        private readonly List<PixelPoint> _lassoPoints = new();
        public IReadOnlyList<PixelPoint> LassoPoints => _lassoPoints;

        // ── Rectangle anchor (set on BeginRectangleSelection) ────────────
        private int _anchorX;
        private int _anchorY;

        // ── Event ─────────────────────────────────────────────────────────
        public event EventHandler? SelectionChanged;
        private void Notify() => SelectionChanged?.Invoke(this, EventArgs.Empty);

        // ── State for Boolean Operations ──────────────────────────────────
        private SelectionMode _currentMode = SelectionMode.Replace;
        private bool[,]? _baseMask;
        private int _baseMinX = -1, _baseMaxX = -1, _baseMinY = -1, _baseMaxY = -1;
        private int _dragMinX = -1, _dragMaxX = -1, _dragMinY = -1, _dragMaxY = -1;

        // ── Building a selection ──────────────────────────────────────────

        private void SnapshotBaseSelection()
        {
            if (!HasActiveSelection)
            {
                _baseMask = null;
                _baseMinX = _baseMaxX = _baseMinY = _baseMaxY = -1;
                return;
            }

            _baseMinX = MinX;
            _baseMaxX = MaxX;
            _baseMinY = MinY;
            _baseMaxY = MaxY;

            if (Mask == null)
            {
                int w = MaxX - MinX + 1;
                int h = MaxY - MinY + 1;
                _baseMask = new bool[w, h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        _baseMask[x, y] = true;
            }
            else
            {
                int w = Mask.GetLength(0);
                int h = Mask.GetLength(1);
                _baseMask = new bool[w, h];
                Array.Copy(Mask, _baseMask, Mask.Length);
            }
        }

        public void BeginRectangleSelection(int x, int y, SelectionMode mode = SelectionMode.Replace)
        {
            _anchorX = x;
            _anchorY = y;
            _currentMode = mode;
            SnapshotBaseSelection();
            
            _dragMinX = _dragMaxX = x;
            _dragMinY = _dragMaxY = y;
            _lassoPoints.Clear();
            
            IsSelecting = true;
            HasActiveSelection = mode != SelectionMode.Replace && _baseMask != null;
            
            RecomputeCombinedSelection();
            Notify();
        }

        public void UpdateRectangleSelection(int currentX, int currentY)
        {
            _dragMinX = Math.Min(_anchorX, currentX);
            _dragMaxX = Math.Max(_anchorX, currentX);
            _dragMinY = Math.Min(_anchorY, currentY);
            _dragMaxY = Math.Max(_anchorY, currentY);
            
            RecomputeCombinedSelection();
            Notify();
        }

        public void BeginLassoSelection(int x, int y, SelectionMode mode = SelectionMode.Replace)
        {
            _currentMode = mode;
            SnapshotBaseSelection();

            _lassoPoints.Clear();
            _lassoPoints.Add(new PixelPoint(x, y));
            
            _dragMinX = _dragMaxX = x;
            _dragMinY = _dragMaxY = y;
            
            IsSelecting = true;
            HasActiveSelection = mode != SelectionMode.Replace && _baseMask != null;
            
            RecomputeCombinedSelection();
            Notify();
        }

        public void AddLassoPoint(int x, int y)
        {
            if (_lassoPoints.Count > 0)
            {
                var last = _lassoPoints[_lassoPoints.Count - 1];
                if (last.X == x && last.Y == y) return; // skip duplicates
            }

            _lassoPoints.Add(new PixelPoint(x, y));
            _dragMinX = Math.Min(_dragMinX, x);
            _dragMaxX = Math.Max(_dragMaxX, x);
            _dragMinY = Math.Min(_dragMinY, y);
            _dragMaxY = Math.Max(_dragMaxY, y);
            
            RecomputeCombinedSelection();
            Notify();
        }

        private void RecomputeCombinedSelection()
        {
            bool[,]? dragMask = null;

            if (_lassoPoints.Count > 0)
            {
                if (_lassoPoints.Count >= 3)
                {
                    int w = _dragMaxX - _dragMinX + 1;
                    int h = _dragMaxY - _dragMinY + 1;
                    dragMask = new bool[w, h];
                    for (int y = _dragMinY; y <= _dragMaxY; y++)
                        for (int x = _dragMinX; x <= _dragMaxX; x++)
                            dragMask[x - _dragMinX, y - _dragMinY] = IsPointInPolygon(x, y);
                }
                else
                {
                    dragMask = new bool[_dragMaxX - _dragMinX + 1, _dragMaxY - _dragMinY + 1];
                }
            }
            else
            {
                int w = _dragMaxX - _dragMinX + 1;
                int h = _dragMaxY - _dragMinY + 1;
                dragMask = new bool[w, h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        dragMask[x, y] = true;
            }

            if (_currentMode == SelectionMode.Replace)
            {
                MinX = _dragMinX; MaxX = _dragMaxX;
                MinY = _dragMinY; MaxY = _dragMaxY;
                if (_lassoPoints.Count == 0)
                    Mask = null;
                else
                    Mask = dragMask;
                return;
            }

            CombineWithBase(dragMask, _dragMinX, _dragMinY, _dragMaxX, _dragMaxY, _currentMode);
        }

        private void CombineWithBase(bool[,] newMask, int nMinX, int nMinY, int nMaxX, int nMaxY, SelectionMode mode)
        {
            if (_baseMask == null && mode != SelectionMode.Replace)
            {
                if (mode == SelectionMode.Add)
                {
                    MinX = nMinX; MaxX = nMaxX;
                    MinY = nMinY; MaxY = nMaxY;
                    Mask = newMask;
                }
                else
                {
                    MinX = MaxX = MinY = MaxY = -1;
                    Mask = null;
                }
                return;
            }

            int minX = mode == SelectionMode.Intersect ? Math.Max(_baseMinX, nMinX) : Math.Min(_baseMinX, nMinX);
            int maxX = mode == SelectionMode.Intersect ? Math.Min(_baseMaxX, nMaxX) : Math.Max(_baseMaxX, nMaxX);
            int minY = mode == SelectionMode.Intersect ? Math.Max(_baseMinY, nMinY) : Math.Min(_baseMinY, nMinY);
            int maxY = mode == SelectionMode.Intersect ? Math.Min(_baseMaxY, nMaxY) : Math.Max(_baseMaxY, nMaxY);

            if (minX > maxX || minY > maxY)
            {
                MinX = MaxX = MinY = MaxY = -1;
                Mask = null;
                return;
            }

            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            bool[,] combined = new bool[w, h];
            bool anyTrue = false;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    bool inBase = false;
                    if (x >= _baseMinX && x <= _baseMaxX && y >= _baseMinY && y <= _baseMaxY)
                        inBase = _baseMask[x - _baseMinX, y - _baseMinY];

                    bool inNew = false;
                    if (x >= nMinX && x <= nMaxX && y >= nMinY && y <= nMaxY)
                        inNew = newMask[x - nMinX, y - nMinY];

                    bool result = false;
                    switch (mode)
                    {
                        case SelectionMode.Add: result = inBase || inNew; break;
                        case SelectionMode.Subtract: result = inBase && !inNew; break;
                        case SelectionMode.Intersect: result = inBase && inNew; break;
                    }

                    combined[x - minX, y - minY] = result;
                    if (result) anyTrue = true;
                }
            }

            if (!anyTrue)
            {
                MinX = MaxX = MinY = MaxY = -1;
                Mask = null;
                return;
            }

            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            Mask = combined;
            HasActiveSelection = true;
        }

        public void ApplyMask(bool[,] mask, int minX, int minY, int maxX, int maxY, SelectionMode mode)
        {
            if (mode == SelectionMode.Replace || (!HasActiveSelection && mode != SelectionMode.Add))
            {
                if (mode == SelectionMode.Intersect || mode == SelectionMode.Subtract)
                {
                    Cancel();
                    return;
                }
                MinX = minX; MaxX = maxX;
                MinY = minY; MaxY = maxY;
                Mask = mask;
                HasActiveSelection = true;
                IsSelecting = false;
                Notify();
                return;
            }

            SnapshotBaseSelection();
            CombineWithBase(mask, minX, minY, maxX, maxY, mode);
            IsSelecting = false;
            Notify();
        }

        public void FinalizeSelection()
        {
            if (MinX == -1 || MaxX == -1 || MinY == -1 || MaxY == -1)
            {
                Cancel();
                return;
            }

            IsSelecting = false;
            HasActiveSelection = true;
            Notify();
        }

        // ── Querying ──────────────────────────────────────────────────────

        public bool IsPixelInSelection(int x, int y)
        {
            if (!HasActiveSelection) return false;
            
            if (IsFloating && FloatingPixels != null)
            {
                int fx = x - FloatingX;
                int fy = y - FloatingY;
                if (fx < 0 || fx >= FloatingWidth || fy < 0 || fy >= FloatingHeight) return false;
                return FloatingPixels[fx, fy];
            }

            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            return Mask == null || Mask[x - MinX, y - MinY];
        }

        public bool IsPointInLasso(int x, int y) => IsPointInPolygon(x, y);

        // ── Committing / cancelling ───────────────────────────────────────

        public void LiftSelection(SpriteState state)
        {
            if (!HasActiveSelection || IsFloating) return;

            FloatingWidth = MaxX - MinX + 1;
            FloatingHeight = MaxY - MinY + 1;
            FloatingX = MinX;
            FloatingY = MinY;
            FloatingPixels = new bool[FloatingWidth, FloatingHeight];

            int w = state.Width;
            for (int y = MinY; y <= MaxY; y++)
            {
                for (int x = MinX; x <= MaxX; x++)
                {
                    if (IsPixelInSelection(x, y))
                    {
                        int idx = (y * w) + x;
                        if (state.Pixels[idx])
                        {
                            FloatingPixels[x - MinX, y - MinY] = true;
                            state.Pixels[idx] = false;
                        }
                    }
                }
            }

            IsFloating = true;
            Notify();
        }

        public void CommitSelection(SpriteState state)
        {
            if (!HasActiveSelection) return;

            if (IsFloating && FloatingPixels != null)
            {
                int w = state.Width;
                int h = state.Height;
                for (int fy = 0; fy < FloatingHeight; fy++)
                {
                    for (int fx = 0; fx < FloatingWidth; fx++)
                    {
                        if (!FloatingPixels[fx, fy]) continue;

                        int gx = FloatingX + fx;
                        int gy = FloatingY + fy;
                        if (gx >= 0 && gx < w && gy >= 0 && gy < h)
                            state.Pixels[(gy * w) + gx] = true;
                    }
                }
            }

            ResetState();
        }

        public void DeleteSelection(SpriteState state)
        {
            if (!HasActiveSelection) return;

            if (IsFloating)
            {
                // Just drop the floating pixels — nothing stamps back to the canvas
                FloatingPixels = null;
                IsFloating = false;
            }
            else
            {
                int w = state.Width;
                for (int i = 0; i < state.Pixels.Length; i++)
                {
                    int x = i % w;
                    int y = i / w;
                    if (IsPixelInSelection(x, y))
                        state.Pixels[i] = false;
                }
            }

            ResetState();
        }

        public void Cancel()
        {
            ResetState();
        }

        // ── Drag ─────────────────────────────────────────────────────────

        public void BeginDrag()
        {
            IsDragging = true;
            Notify();
        }

        public void MoveFloatingTo(int newX, int newY)
        {
            if (!IsFloating) return;
            FloatingX = newX;
            FloatingY = newY;
            Notify();
        }

        public void EndDrag()
        {
            IsDragging = false;
            Notify();
        }

        // ── Clipboard integration ────────────────────────────────────────

        public PixelClipboardData? CopySelection(SpriteState state)
        {
            if (!HasActiveSelection) return null;

            if (IsFloating && FloatingPixels != null)
            {
                // Copy from the floating layer
                var copy = new bool[FloatingWidth, FloatingHeight];
                Array.Copy(FloatingPixels, copy, FloatingPixels.Length);
                return new PixelClipboardData(copy, FloatingWidth, FloatingHeight);
            }

            // Copy from the canvas using the selection bounds + mask
            int w = MaxX - MinX + 1;
            int h = MaxY - MinY + 1;
            var pixels = new bool[w, h];

            int canvasW = state.Width;
            for (int y = MinY; y <= MaxY; y++)
            {
                for (int x = MinX; x <= MaxX; x++)
                {
                    if (!IsPixelInSelection(x, y)) continue;
                    if (x < 0 || x >= state.Width || y < 0 || y >= state.Height) continue;

                    int idx = (y * canvasW) + x;
                    pixels[x - MinX, y - MinY] = state.Pixels[idx];
                }
            }

            return new PixelClipboardData(pixels, w, h);
        }

        public void PasteAsFloating(PixelClipboardData data, int canvasWidth, int canvasHeight)
        {
            if (data == null) return;

            // Reset any existing selection state
            ResetState();

            // Center the pasted content on the canvas
            FloatingX = (canvasWidth - data.Width) / 2;
            FloatingY = (canvasHeight - data.Height) / 2;
            FloatingWidth = data.Width;
            FloatingHeight = data.Height;
            FloatingPixels = new bool[data.Width, data.Height];
            Array.Copy(data.Pixels, FloatingPixels, data.Pixels.Length);

            // Mark as a floating active selection
            IsFloating = true;
            HasActiveSelection = true;

            // Set selection bounds to match the floating layer
            MinX = FloatingX;
            MinY = FloatingY;
            MaxX = FloatingX + FloatingWidth - 1;
            MaxY = FloatingY + FloatingHeight - 1;

            Notify();
        }

        // ── Private helpers ───────────────────────────────────────────────

        private void ResetState()
        {
            HasActiveSelection = false;
            IsSelecting = false;
            IsFloating = false;
            IsDragging = false;
            MinX = MaxX = MinY = MaxY = -1;
            Mask = null;
            FloatingPixels = null;
            FloatingX = FloatingY = FloatingWidth = FloatingHeight = 0;
            _lassoPoints.Clear();

            _baseMask = null;
            _baseMinX = _baseMaxX = _baseMinY = _baseMaxY = -1;
            _dragMinX = _dragMaxX = _dragMinY = _dragMaxY = -1;

            Notify();
        }

        /// <summary>
        /// Ray-casting point-in-polygon test. Moved here from MainWindow.xaml.cs
        /// so it can be used by FinalizeSelection() and IsPixelInSelection().
        /// </summary>
        private bool IsPointInPolygon(int px, int py)
        {
            int count = _lassoPoints.Count;
            if (count == 0) return false;
            if (count == 1) return _lassoPoints[0].X == px && _lassoPoints[0].Y == py;
            if (count == 2)
                return _lassoPoints[0].Equals(new PixelPoint(px, py))
                    || _lassoPoints[1].Equals(new PixelPoint(px, py));

            // Fast exact-vertex check
            foreach (var v in _lassoPoints)
                if (v.X == px && v.Y == py) return true;

            // Offset slightly to avoid sitting exactly on an edge
            double tx = px + 0.01;
            double ty = py + 0.01;
            bool inside = false;

            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double xi = _lassoPoints[i].X, yi = _lassoPoints[i].Y;
                double xj = _lassoPoints[j].X, yj = _lassoPoints[j].Y;

                if ((yi > ty) != (yj > ty) &&
                    tx < (xj - xi) * (ty - yi) / (yj - yi) + xi)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
