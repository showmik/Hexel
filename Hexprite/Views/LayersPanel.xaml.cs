using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hexprite.ViewModels;

namespace Hexprite.Views
{
    public partial class LayersPanel : UserControl
    {
        private MainViewModel? ViewModel => DataContext as MainViewModel;

        private Point _layerDragArmStart;
        private LayerItemViewModel? _armedLayerDragItem;

        internal const string LayerDragDataFormat = "Hexprite.LayerItemViewModel";

        internal static LayerItemViewModel? TryReadLayerDragPayload(IDataObject d)
        {
            if (d.GetData(LayerDragDataFormat) is LayerItemViewModel keyed)
                return keyed;
            if (d is DataObject dbo && dbo.GetData(typeof(LayerItemViewModel)) is LayerItemViewModel typed)
                return typed;
            return null;
        }

        internal static bool LayerDragPayloadPresent(IDataObject d)
        {
            return d.GetDataPresent(LayerDragDataFormat)
                   || (d is DataObject dbo && dbo.GetDataPresent(typeof(LayerItemViewModel)));
        }

        internal static DataObject WrapLayerDragPayload(LayerItemViewModel item)
        {
            var obj = new DataObject();
            obj.SetData(typeof(LayerItemViewModel), item);
            obj.SetData(LayerDragDataFormat, item);
            return obj;
        }

        private ListBoxItem? _opaqueDragRow;
        private double _opaqueStored = 1.0;

        public LayersPanel()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                // DynamicResource resolves late; fallback if brush missing.
                if (LayerInsertionGuide.Background == null)
                {
                    LayerInsertionGuide.Background = LayerList.TryFindResource("Brush.Canvas.Drawing") as Brush
                        ?? LayerList.TryFindResource("Brush.Border.Separator") as Brush
                        ?? Brushes.DeepSkyBlue;
                }
            };
        }

        private void HideInsertionGuide() => LayerInsertionGuide.Visibility = Visibility.Collapsed;

        /// <summary>Positions the drop-indicator line in ListBox viewport coordinates.</summary>
        private void ShowInsertionGuideAt(double centerYInLayerListViewport)
        {
            const double h = 3;
            const double inset = 6;
            double hostH = LayerListClipHost.ActualHeight;
            if (hostH <= 0 || double.IsNaN(hostH))
                hostH = LayerList.ActualHeight;
            double maxY = Math.Max(0, hostH - h);
            double y = Math.Clamp(centerYInLayerListViewport - h / 2.0, 0, maxY);
            LayerInsertionGuide.Margin = new Thickness(inset, y, inset, 0);
            LayerInsertionGuide.Visibility = Visibility.Visible;
        }

        private void LayerVisible_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || sender is not FrameworkElement fe) return;
            int index = LayerList.ItemContainerGenerator.IndexFromContainer(
                LayerList.ContainerFromElement(fe) as DependencyObject);
            if (index < 0 && fe.DataContext is LayerItemViewModel item)
                index = LayerList.Items.IndexOf(item);
            if (index < 0) return;
            bool isVisible = (fe as CheckBox)?.IsChecked ?? true;
            ViewModel.SetLayerVisibility(index, isVisible);
        }

        private void LayerLocked_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || sender is not FrameworkElement fe) return;
            int index = LayerList.ItemContainerGenerator.IndexFromContainer(
                LayerList.ContainerFromElement(fe) as DependencyObject);
            if (index < 0 && fe.DataContext is LayerItemViewModel item)
                index = LayerList.Items.IndexOf(item);
            if (index < 0) return;
            bool isLocked = (fe as CheckBox)?.IsChecked ?? false;
            ViewModel.SetLayerLocked(index, isLocked);
        }

        private void EndRenamingUi(LayerItemViewModel item)
        {
            item.IsRenaming = false;
        }

        private void LayerName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || sender is not TextBox tb) return;
            if (tb.DataContext is not LayerItemViewModel item) return;

            int index = LayerList.Items.IndexOf(item);
            if (index >= 0)
                ViewModel.UpdateLayerName(index, tb.Text);

            EndRenamingUi(item);
        }

        private void LayerName_KeyDown(object sender, KeyEventArgs e)
        {
            if (ViewModel == null || sender is not TextBox tb) return;
            if (tb.DataContext is not LayerItemViewModel item) return;

            int index = LayerList.Items.IndexOf(item);
            if (index < 0) return;

            if (e.Key == Key.Enter)
            {
                ViewModel.UpdateLayerName(index, tb.Text);
                EndRenamingUi(item);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                tb.Text = item.Name;
                EndRenamingUi(item);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void LayerName_TextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not TextBox tb || !tb.IsVisible) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }), DispatcherPriority.Loaded);
        }

        private void LayerDisplayName_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || ViewModel == null) return;
            if ((sender as FrameworkElement)?.DataContext is not LayerItemViewModel item) return;
            int index = LayerList.Items.IndexOf(item);
            if (index < 0) return;
            ViewModel.BeginLayerRename(index);
            e.Handled = true;
        }

        private void LayerList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F2 || ViewModel == null) return;
            int idx = LayerList.SelectedIndex;
            if (idx < 0) return;
            ViewModel.BeginLayerRename(idx);
            e.Handled = true;
        }

        private static LayerItemViewModel? GetLayerRowDataContext(DependencyObject? leaf)
        {
            var cur = leaf;
            while (cur != null && cur is not ListBoxItem)
                cur = VisualTreeHelper.GetParent(cur);
            return (cur as FrameworkElement)?.DataContext as LayerItemViewModel;
        }

        private void LayerGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Capture the whole list so PreviewMouseMove still fires once the cursor leaves the narrow grip.
            _armedLayerDragItem = GetLayerRowDataContext(e.OriginalSource as DependencyObject)
                                  ?? ((sender as FrameworkElement)?.DataContext as LayerItemViewModel);
            _layerDragArmStart = e.GetPosition(null);
            Mouse.Capture(LayerList);
        }

        private void LayerList_PreviewMouseMove_ArmDrag(object sender, MouseEventArgs e)
        {
            if (_armedLayerDragItem == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _layerDragArmStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _layerDragArmStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var dragged = _armedLayerDragItem;
            _armedLayerDragItem = null;
            Mouse.Capture(null);

            if (dragged == null || ViewModel == null)
                return;

            ApplyDragSourceGhost(dragged);
            try
            {
                DragDrop.DoDragDrop(LayerList, WrapLayerDragPayload(dragged), DragDropEffects.Move);
            }
            finally
            {
                ClearLayerDragFx();
            }

            e.Handled = true;
        }

        private void LayerList_PreviewMouseLeftButtonUp_ArmDrag(object sender, MouseButtonEventArgs e)
        {
            if (_armedLayerDragItem != null || Mouse.Captured == LayerList)
            {
                if (Mouse.Captured == LayerList)
                    Mouse.Capture(null);
                _armedLayerDragItem = null;
            }
        }

        private void ApplyDragSourceGhost(LayerItemViewModel payload)
        {
            int i = LayerList.Items.IndexOf(payload);
            if (i < 0) return;
            if (LayerList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row) return;
            _opaqueDragRow = row;
            _opaqueStored = row.Opacity;
            row.Opacity = 0.45;
        }

        private void ClearLayerDragFx()
        {
            if (_opaqueDragRow != null)
                _opaqueDragRow.Opacity = _opaqueStored;
            _opaqueDragRow = null;
            _opaqueStored = 1.0;
            HideInsertionGuide();
        }

        private void LayerList_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!LayerDragPayloadPresent(e.Data))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var payload = TryReadLayerDragPayload(e.Data);
            int from = payload != null ? LayerList.Items.IndexOf(payload) : -1;

            var pList = e.GetPosition(LayerList);
            if (LayerList.Items.Count == 0)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                HideInsertionGuide();
                return;
            }

            if (!TryComputeInsertionGap(pList, out int slotVisual, out double lineY))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                HideInsertionGuide();
                return;
            }

            int adjustedPreview = AdjustInsertIndex(slotVisual, from);
            ShowInsertionGuideAt(lineY);

            if (adjustedPreview == from)
            {
                e.Effects = DragDropEffects.None;
                HideInsertionGuide();
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private static int AdjustInsertIndex(int gapBeforeRow, int from)
        {
            if (from >= 0 && from < gapBeforeRow)
                return gapBeforeRow - 1;
            return gapBeforeRow;
        }

        /// <returns>False if computation failed (fallback to callers).</returns>
        private bool TryComputeInsertionGap(Point mouseInLayerList, out int slotBeforeRowOrCount, out double lineYPixelsInLayerList)
        {
            slotBeforeRowOrCount = 0;
            lineYPixelsInLayerList = 0;
            Rect? lastBounds = null;

            int n = LayerList.Items.Count;
            for (int i = 0; i < n; i++)
            {
                if (LayerList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row)
                    return false;

                double w = row.ActualWidth > 0 ? row.ActualWidth : row.RenderSize.Width;
                double h = row.ActualHeight > 0 ? row.ActualHeight : row.RenderSize.Height;
                var t = row.TransformToAncestor(LayerList);
                var bounds = t.TransformBounds(new Rect(0, 0, w, h));
                double mid = bounds.Top + bounds.Height / 2.0;

                if (mouseInLayerList.Y < mid)
                {
                    slotBeforeRowOrCount = i;
                    lineYPixelsInLayerList = bounds.Top;
                    lastBounds = null;
                    return true;
                }

                lastBounds = bounds;
            }

            slotBeforeRowOrCount = n;
            if (lastBounds.HasValue)
            {
                lineYPixelsInLayerList = lastBounds.Value.Bottom;
                return true;
            }

            lineYPixelsInLayerList = 0;
            return true;
        }

        private void LayerList_DragLeave(object sender, DragEventArgs e)
        {
            if (!LayerDragPayloadPresent(e.Data)) return;
            HideInsertionGuide();
        }

        private void LayerList_Drop(object sender, DragEventArgs e)
        {
            if (ViewModel == null)
            {
                ClearLayerDragFx();
                return;
            }

            if (!LayerDragPayloadPresent(e.Data))
            {
                ClearLayerDragFx();
                return;
            }

            var sourceItem = TryReadLayerDragPayload(e.Data);
            if (sourceItem == null)
            {
                ClearLayerDragFx();
                return;
            }

            int from = LayerList.Items.IndexOf(sourceItem);
            if (from < 0)
            {
                ClearLayerDragFx();
                return;
            }

            if (LayerList.Items.Count == 0 || !TryComputeInsertionGap(e.GetPosition(LayerList), out int slotVisual, out _))
            {
                ClearLayerDragFx();
                return;
            }

            int to = AdjustInsertIndex(slotVisual, from);
            if (to != from)
                ViewModel.MoveLayer(from, to);

            ClearLayerDragFx();
            e.Handled = true;
        }
    }
}
