using System;
using System.Collections.Generic;
using Hexprite.Core;

namespace Hexprite.Services
{
    /// <summary>
    /// Owns all selection state. Previously this state was duplicated between
    /// MainViewModel (SyncFloatingState / SetSelectionBounds) and MainWindow
    /// (a dozen private fields). The View now subscribes to SelectionChanged
    /// and reads from this service to update its overlays.
    /// </summary>
    public interface ISelectionService
    {
        // ── Status flags ──────────────────────────────────────────────────
        bool HasActiveSelection { get; }
        bool IsSelecting { get; }
        bool IsFloating { get; }
        bool IsDragging { get; }

        // ── Selection bounds (pixel coordinates) ─────────────────────────
        int MinX { get; }
        int MaxX { get; }
        int MinY { get; }
        int MaxY { get; }

        /// <summary>
        /// Per-pixel inclusion mask relative to (MinX, MinY).
        /// Null for a marquee selection (every pixel in bounds is selected).
        /// Populated by FinalizeSelection() after a lasso is drawn.
        /// </summary>
        bool[,]? Mask { get; }

        // ── Floating layer ────────────────────────────────────────────────
        bool[,]? FloatingPixels { get; }
        int FloatingX { get; }
        int FloatingY { get; }
        int FloatingWidth { get; }
        int FloatingHeight { get; }

        /// <summary>Lasso path in pixel coordinates, built up during mouse drag.</summary>
        IReadOnlyList<PixelPoint> LassoPoints { get; }

        // ── Events ────────────────────────────────────────────────────────
        /// <summary>
        /// Raised after any state change. The View subscribes to this to know
        /// when to redraw selection overlays.
        /// </summary>
        event EventHandler SelectionChanged;

        // ── Building a selection ──────────────────────────────────────────
        void BeginRectangleSelection(int x, int y, SelectionMode mode = SelectionMode.Replace);
        void UpdateRectangleSelection(int currentX, int currentY);

        void BeginLassoSelection(int x, int y, SelectionMode mode = SelectionMode.Replace);
        void AddLassoPoint(int x, int y);

        /// <summary>
        /// Called on mouse-up after drawing a lasso. Computes the Mask from
        /// LassoPoints and marks the selection as active.
        /// </summary>
        void FinalizeSelection();
        
        void ApplyMask(bool[,] mask, int minX, int minY, int maxX, int maxY, SelectionMode mode);

        // ── Querying ──────────────────────────────────────────────────────
        /// <summary>Returns true if the pixel at (x, y) falls inside the active selection.</summary>
        bool IsPixelInSelection(int x, int y);

        /// <summary>Returns true if the pixel at (x, y) falls inside the current lasso polygon.</summary>
        bool IsPointInLasso(int x, int y);

        // ── Committing / cancelling ───────────────────────────────────────
        /// <summary>
        /// Picks up the selected pixels from the sprite into the floating layer,
        /// clearing them from the canvas. After this call IsFloating is true.
        /// </summary>
        void LiftSelection(SpriteState state);

        /// <summary>
        /// Stamps the floating layer back onto the sprite and clears all selection state.
        /// </summary>
        void CommitSelection(SpriteState state);

        /// <summary>Erases the selected pixels from the sprite and clears selection state.</summary>
        void DeleteSelection(SpriteState state);

        /// <summary>Clears all selection state without modifying the sprite.</summary>
        void Cancel();

        // ── Clipboard integration ────────────────────────────────────────
        /// <summary>
        /// Returns a copy of the selected pixels from the sprite (or from the
        /// floating layer if one is active). Does not modify the sprite state.
        /// Returns null if there is no active selection.
        /// </summary>
        PixelClipboardData? CopySelection(SpriteState state);

        /// <summary>
        /// Creates a new floating selection from the given pixel data,
        /// centered on the canvas. Any existing selection is committed first
        /// by the caller.
        /// </summary>
        void PasteAsFloating(PixelClipboardData data, int canvasWidth, int canvasHeight);

        // ── Drag ─────────────────────────────────────────────────────────
        void BeginDrag();
        void MoveFloatingTo(int newX, int newY);
        void EndDrag();

        // ── Snapshots ────────────────────────────────────────────────────
        SelectionSnapshot CreateSnapshot();
        void RestoreSnapshot(SelectionSnapshot snapshot);

        // ── Transform (resize floating selection) ───────────────────────
        bool IsTransforming { get; }
        TransformHandle ActiveTransformHandle { get; }
        void BeginTransform(TransformHandle handle);
        void UpdateTransform(int newX, int newY, int newW, int newH);
        void CommitTransform();
        void CancelTransform();

        // ── Flip (floating selection only) ─────────────────────────────
        void FlipFloatingHorizontally();
        void FlipFloatingVertically();
    }
}
