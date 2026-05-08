using System;
using System.Buffers;
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
        public bool IsTransforming { get; private set; }
        public TransformHandle ActiveTransformHandle { get; private set; }

        // ── Transform snapshot (nearest-neighbor resize source) ───────────
        private bool[,]? _originalFloatingPixels;
        private int _originalFloatingX;
        private int _originalFloatingY;
        private int _originalFloatingW;
        private int _originalFloatingH;

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
        private void Notify()
        {
            ValidateState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void ValidateState()
        {
            // Floating requires an active selection
            if (IsFloating && !HasActiveSelection)
                throw new InvalidOperationException("Invalid state: IsFloating=true but HasActiveSelection=false");

            // Transforming requires floating state
            if (IsTransforming && !IsFloating)
                throw new InvalidOperationException("Invalid state: IsTransforming=true but IsFloating=false");

            // Cannot be selecting and dragging simultaneously
            if (IsSelecting && IsDragging)
                throw new InvalidOperationException("Invalid state: IsSelecting=true and IsDragging=true");

            // Cannot be selecting and transforming simultaneously
            if (IsSelecting && IsTransforming)
                throw new InvalidOperationException("Invalid state: IsSelecting=true and IsTransforming=true");
        }

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

            int minX = _baseMinX;
            int maxX = _baseMaxX;
            int minY = _baseMinY;
            int maxY = _baseMaxY;

            if (mode == SelectionMode.Add)
            {
                minX = Math.Min(_baseMinX, nMinX);
                maxX = Math.Max(_baseMaxX, nMaxX);
                minY = Math.Min(_baseMinY, nMinY);
                maxY = Math.Max(_baseMaxY, nMaxY);
            }
            else if (mode == SelectionMode.Intersect)
            {
                minX = Math.Max(_baseMinX, nMinX);
                maxX = Math.Min(_baseMaxX, nMaxX);
                minY = Math.Max(_baseMinY, nMinY);
                maxY = Math.Min(_baseMaxY, nMaxY);
            }

            if (minX > maxX || minY > maxY)
            {
                MinX = MaxX = MinY = MaxY = -1;
                Mask = null;
                return;
            }

            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            int total = w * h;
            bool[] combined = ArrayPool<bool>.Shared.Rent(total);
            Array.Clear(combined, 0, total);
            bool anyTrue = false;
            int trueMinX = int.MaxValue, trueMinY = int.MaxValue, trueMaxX = int.MinValue, trueMaxY = int.MinValue;

            try
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        bool inBase = false;
                        if (x >= _baseMinX && x <= _baseMaxX && y >= _baseMinY && y <= _baseMaxY)
                            inBase = _baseMask![x - _baseMinX, y - _baseMinY];

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

                        int localX = x - minX;
                        int localY = y - minY;
                        if (result)
                        {
                            combined[(localY * w) + localX] = true;
                            anyTrue = true;
                            trueMinX = Math.Min(trueMinX, x);
                            trueMaxX = Math.Max(trueMaxX, x);
                            trueMinY = Math.Min(trueMinY, y);
                            trueMaxY = Math.Max(trueMaxY, y);
                        }
                    }
                }

                if (!anyTrue)
                {
                    MinX = MaxX = MinY = MaxY = -1;
                    Mask = null;
                    return;
                }

                int finalW = trueMaxX - trueMinX + 1;
                int finalH = trueMaxY - trueMinY + 1;
                var trimmed = new bool[finalW, finalH];
                for (int y = trueMinY; y <= trueMaxY; y++)
                {
                    for (int x = trueMinX; x <= trueMaxX; x++)
                    {
                        int srcX = x - minX;
                        int srcY = y - minY;
                        trimmed[x - trueMinX, y - trueMinY] = combined[(srcY * w) + srcX];
                    }
                }

                MinX = trueMinX;
                MaxX = trueMaxX;
                MinY = trueMinY;
                MaxY = trueMaxY;
                Mask = trimmed;
                HasActiveSelection = true;
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(combined);
            }
        }

        public void ApplyMask(bool[,] mask, int minX, int minY, int maxX, int maxY, SelectionMode mode)
        {
            // When nothing is selected, ApplyMask(Add, …) should behave like Replace (magic wand etc.);
            // rectangle/lasso never hits ApplyMask during drag—they use CombineWithBase only.
            if (mode == SelectionMode.Replace || !HasActiveSelection)
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
            if (_lassoPoints.Count > 0)
            {
                if (_lassoPoints.Count < 3)
                {
                    if (_currentMode == SelectionMode.Replace)
                    {
                        Cancel();
                        return;
                    }
                    else
                    {
                        if (_baseMask != null)
                        {
                            MinX = _baseMinX; MaxX = _baseMaxX;
                            MinY = _baseMinY; MaxY = _baseMaxY;
                            Mask = _baseMask;
                            HasActiveSelection = true;
                        }
                        else
                        {
                            Cancel();
                            return;
                        }
                        IsSelecting = false;
                        _lassoPoints.Clear();
                        Notify();
                        return;
                    }
                }
                
                RecomputeCombinedSelection();
            }

            if (MinX == -1 || MaxX == -1 || MinY == -1 || MaxY == -1)
            {
                Cancel();
                return;
            }

            if (Mask != null && _currentMode == SelectionMode.Replace)
            {
                bool hasAnyPixel = false;
                int w = Mask.GetLength(0);
                int h = Mask.GetLength(1);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (Mask[x, y])
                        {
                            hasAnyPixel = true;
                            break;
                        }
                    }
                    if (hasAnyPixel) break;
                }

                if (!hasAnyPixel)
                {
                    Cancel();
                    return;
                }
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
            int h = state.Height;
            for (int y = MinY; y <= MaxY; y++)
            {
                for (int x = MinX; x <= MaxX; x++)
                {
                    if (IsPixelInSelection(x, y))
                    {
                        if (x >= 0 && x < w && y >= 0 && y < h)
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

                MinX = FloatingX;
                MinY = FloatingY;
                MaxX = FloatingX + FloatingWidth - 1;
                MaxY = FloatingY + FloatingHeight - 1;

                FloatingPixels = null;
                IsFloating = false;
                FloatingX = FloatingY = FloatingWidth = FloatingHeight = 0;

                _baseMask = null;
                _baseMinX = _baseMaxX = _baseMinY = _baseMaxY = -1;
                _dragMinX = _dragMaxX = _dragMinY = _dragMaxY = -1;
                _lassoPoints.Clear();

                Notify();
            }
        }

        public void DeleteSelection(SpriteState state)
        {
            if (!HasActiveSelection) return;

            if (IsFloating)
            {
                // Just drop the floating pixels — lifting already cleared pixels from the canvas.
                ResetState(notify: false);
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

                // Match expected UX: deleting a selection should also clear
                // selection state (marquee/lasso preview goes away).
                ResetState(notify: false);
            }

            Notify();
        }

        public void Cancel() => ResetState(notify: true);

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
            ResetState(notify: false);

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
        // ── Snapshots ────────────────────────────────────────────────────

        public SelectionSnapshot CreateSnapshot()
        {
            return new SelectionSnapshot
            {
                HasActiveSelection = HasActiveSelection,
                IsSelecting = IsSelecting,
                IsFloating = IsFloating,
                IsDragging = IsDragging,
                IsTransforming = IsTransforming,
                ActiveTransformHandle = ActiveTransformHandle,
                OriginalFloatingPixels = _originalFloatingPixels != null ? (bool[,])_originalFloatingPixels.Clone() : null,
                OriginalFloatingX = _originalFloatingX,
                OriginalFloatingY = _originalFloatingY,
                OriginalFloatingWidth = _originalFloatingW,
                OriginalFloatingHeight = _originalFloatingH,
                MinX = MinX,
                MaxX = MaxX,
                MinY = MinY,
                MaxY = MaxY,
                Mask = Mask != null ? (bool[,])Mask.Clone() : null,
                FloatingPixels = FloatingPixels != null ? (bool[,])FloatingPixels.Clone() : null,
                FloatingX = FloatingX,
                FloatingY = FloatingY,
                FloatingWidth = FloatingWidth,
                FloatingHeight = FloatingHeight,
                LassoPoints = new List<PixelPoint>(_lassoPoints)
            };
        }

        public void RestoreSnapshot(SelectionSnapshot snapshot)
        {
            HasActiveSelection = snapshot.HasActiveSelection;
            IsSelecting = snapshot.IsSelecting;
            IsFloating = snapshot.IsFloating;
            IsDragging = snapshot.IsDragging;
            IsTransforming = snapshot.IsTransforming;
            ActiveTransformHandle = snapshot.ActiveTransformHandle;
            _originalFloatingPixels = snapshot.OriginalFloatingPixels != null ? (bool[,])snapshot.OriginalFloatingPixels.Clone() : null;
            _originalFloatingX = snapshot.OriginalFloatingX;
            _originalFloatingY = snapshot.OriginalFloatingY;
            _originalFloatingW = snapshot.OriginalFloatingWidth;
            _originalFloatingH = snapshot.OriginalFloatingHeight;
            MinX = snapshot.MinX;
            MaxX = snapshot.MaxX;
            MinY = snapshot.MinY;
            MaxY = snapshot.MaxY;
            Mask = snapshot.Mask != null ? (bool[,])snapshot.Mask.Clone() : null;
            FloatingPixels = snapshot.FloatingPixels != null ? (bool[,])snapshot.FloatingPixels.Clone() : null;
            FloatingX = snapshot.FloatingX;
            FloatingY = snapshot.FloatingY;
            FloatingWidth = snapshot.FloatingWidth;
            FloatingHeight = snapshot.FloatingHeight;

            _lassoPoints.Clear();
            if (snapshot.LassoPoints != null)
            {
                _lassoPoints.AddRange(snapshot.LassoPoints);
            }

            // Clear intermediate boolean operation state
            _baseMask = null;
            _baseMinX = _baseMaxX = _baseMinY = _baseMaxY = -1;

            Notify();
        }

        // ── Transform (resize floating selection) ─────────────────────────

        public void BeginTransform(TransformHandle handle)
        {
            if (!HasActiveSelection || handle == TransformHandle.None)
                return;
            if (!IsFloating || FloatingPixels == null)
                return;

            ActiveTransformHandle = handle;
            IsTransforming = true;
            _originalFloatingPixels = (bool[,])FloatingPixels.Clone();
            _originalFloatingX = FloatingX;
            _originalFloatingY = FloatingY;
            _originalFloatingW = FloatingWidth;
            _originalFloatingH = FloatingHeight;
            Notify();
        }

        public void UpdateTransform(int newX, int newY, int newW, int newH, bool flipX = false, bool flipY = false)
        {
            if (!IsTransforming || _originalFloatingPixels == null)
                return;

            newW = Math.Max(1, newW);
            newH = Math.Max(1, newH);

            FloatingX = newX;
            FloatingY = newY;
            FloatingWidth = newW;
            FloatingHeight = newH;

            FloatingPixels = ResampleNearestNeighbor(_originalFloatingPixels, _originalFloatingW, _originalFloatingH, newW, newH);
            if (FloatingPixels != null)
            {
                if (flipX) MirrorXInPlace(FloatingPixels, newW, newH);
                if (flipY) MirrorYInPlace(FloatingPixels, newW, newH);
            }

            MinX = newX;
            MinY = newY;
            MaxX = newX + newW - 1;
            MaxY = newY + newH - 1;
            Notify();
        }

        public void CommitTransform()
        {
            if (!IsTransforming)
                return;

            IsTransforming = false;
            ActiveTransformHandle = TransformHandle.None;
            _originalFloatingPixels = null;

            MinX = FloatingX;
            MinY = FloatingY;
            MaxX = FloatingX + FloatingWidth - 1;
            MaxY = FloatingY + FloatingHeight - 1;
            Mask = null;
            Notify();
        }

        public void CancelTransform()
        {
            if (!IsTransforming || _originalFloatingPixels == null)
                return;

            FloatingPixels = (bool[,])_originalFloatingPixels.Clone();
            FloatingX = _originalFloatingX;
            FloatingY = _originalFloatingY;
            FloatingWidth = _originalFloatingW;
            FloatingHeight = _originalFloatingH;

            MinX = FloatingX;
            MinY = FloatingY;
            MaxX = FloatingX + FloatingWidth - 1;
            MaxY = FloatingY + FloatingHeight - 1;

            IsTransforming = false;
            ActiveTransformHandle = TransformHandle.None;
            _originalFloatingPixels = null;
            Notify();
        }

        // ── Flip (floating selection only) ─────────────────────────────

        public void FlipFloatingHorizontally()
        {
            if (!IsFloating || FloatingPixels == null) return;

            // Don't allow resampling-based resize to continue after a flip;
            // commit now to end transform mode (and avoid stale _originalFloatingPixels).
            if (IsTransforming)
                CommitTransform();

            int fw = FloatingWidth;
            int fh = FloatingHeight;
            var flipped = new bool[fw, fh];

            for (int y = 0; y < fh; y++)
            {
                for (int x = 0; x < fw; x++)
                {
                    flipped[fw - 1 - x, y] = FloatingPixels[x, y];
                }
            }

            FloatingPixels = flipped;

            // Keep the per-pixel boundary mask aligned with the flipped content.
            if (Mask != null && Mask.GetLength(0) == fw && Mask.GetLength(1) == fh)
            {
                var flippedMask = new bool[fw, fh];
                for (int y = 0; y < fh; y++)
                {
                    for (int x = 0; x < fw; x++)
                        flippedMask[fw - 1 - x, y] = Mask[x, y];
                }
                Mask = flippedMask;
            }

            Notify();
        }

        public void FlipFloatingVertically()
        {
            if (!IsFloating || FloatingPixels == null) return;

            if (IsTransforming)
                CommitTransform();

            int fw = FloatingWidth;
            int fh = FloatingHeight;
            var flipped = new bool[fw, fh];

            for (int y = 0; y < fh; y++)
            {
                int ny = fh - 1 - y;
                for (int x = 0; x < fw; x++)
                {
                    flipped[x, ny] = FloatingPixels[x, y];
                }
            }

            FloatingPixels = flipped;

            if (Mask != null && Mask.GetLength(0) == fw && Mask.GetLength(1) == fh)
            {
                var flippedMask = new bool[fw, fh];
                for (int y = 0; y < fh; y++)
                {
                    int ny = fh - 1 - y;
                    for (int x = 0; x < fw; x++)
                        flippedMask[x, ny] = Mask[x, y];
                }
                Mask = flippedMask;
            }

            Notify();
        }

        private static bool[,] ResampleNearestNeighbor(bool[,] src, int sw, int sh, int dw, int dh)
        {
            var dst = new bool[dw, dh];
            if (sw <= 0 || sh <= 0)
                return dst;

            for (int ty = 0; ty < dh; ty++)
            {
                int sy = dh <= 1 ? 0 : ty * (sh - 1) / (dh - 1);
                for (int tx = 0; tx < dw; tx++)
                {
                    int sx = dw <= 1 ? 0 : tx * (sw - 1) / (dw - 1);
                    dst[tx, ty] = src[sx, sy];
                }
            }

            return dst;
        }

        private static void MirrorXInPlace(bool[,] pixels, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w / 2; x++)
                {
                    int nx = w - 1 - x;
                    (pixels[x, y], pixels[nx, y]) = (pixels[nx, y], pixels[x, y]);
                }
            }
        }

        private static void MirrorYInPlace(bool[,] pixels, int w, int h)
        {
            for (int y = 0; y < h / 2; y++)
            {
                int ny = h - 1 - y;
                for (int x = 0; x < w; x++)
                    (pixels[x, y], pixels[x, ny]) = (pixels[x, ny], pixels[x, y]);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────

        private void ResetState(bool notify)
        {
            HasActiveSelection = false;
            IsSelecting = false;
            IsFloating = false;
            IsDragging = false;
            IsTransforming = false;
            ActiveTransformHandle = TransformHandle.None;
            _originalFloatingPixels = null;
            MinX = MaxX = MinY = MaxY = -1;
            Mask = null;
            FloatingPixels = null;
            FloatingX = FloatingY = FloatingWidth = FloatingHeight = 0;
            _lassoPoints.Clear();

            _baseMask = null;
            _baseMinX = _baseMaxX = _baseMinY = _baseMaxY = -1;
            _dragMinX = _dragMaxX = _dragMinY = _dragMaxY = -1;

            if (notify)
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
