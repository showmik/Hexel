using System;
using System.Collections.Generic;
using Hexel.Core;

namespace Hexel.Services
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

        // ── Building a selection ──────────────────────────────────────────

        public void BeginRectangleSelection(int x, int y)
        {
            _anchorX = x;
            _anchorY = y;
            MinX = MaxX = x;
            MinY = MaxY = y;
            Mask = null;
            _lassoPoints.Clear();
            IsSelecting = true;
            HasActiveSelection = false;
            Notify();
        }

        public void UpdateRectangleSelection(int currentX, int currentY)
        {
            MinX = Math.Min(_anchorX, currentX);
            MaxX = Math.Max(_anchorX, currentX);
            MinY = Math.Min(_anchorY, currentY);
            MaxY = Math.Max(_anchorY, currentY);
            Notify();
        }

        public void BeginLassoSelection(int x, int y)
        {
            _lassoPoints.Clear();
            _lassoPoints.Add(new PixelPoint(x, y));
            MinX = MaxX = x;
            MinY = MaxY = y;
            Mask = null;
            IsSelecting = true;
            HasActiveSelection = false; // not active until FinalizeSelection
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
            MinX = Math.Min(MinX, x);
            MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y);
            MaxY = Math.Max(MaxY, y);
            Notify();
        }

        /// <summary>
        /// Called on mouse-up. Computes the per-pixel mask from the accumulated
        /// lasso points and activates the selection.
        /// </summary>
        public void FinalizeSelection()
        {
            // Marquee selection
            if (_lassoPoints.Count == 0)
            {
                if (MinX == MaxX && MinY == MaxY)
                {
                    Cancel();
                    return;
                }
                IsSelecting = false;
                HasActiveSelection = true;
                Notify();
                return;
            }

            // Lasso selection
            if (_lassoPoints.Count < 3)
            {
                Cancel();
                return;
            }

            int w = MaxX - MinX + 1;
            int h = MaxY - MinY + 1;
            Mask = new bool[w, h];
            bool anyTrue = false;

            for (int y = MinY; y <= MaxY; y++)
                for (int x = MinX; x <= MaxX; x++)
                {
                    Mask[x - MinX, y - MinY] = IsPointInPolygon(x, y);
                    if (Mask[x - MinX, y - MinY]) anyTrue = true;
                }

            if (!anyTrue)
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
