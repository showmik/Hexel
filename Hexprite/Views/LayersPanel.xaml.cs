using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Hexprite.ViewModels;
using System;

namespace Hexprite.Views
{
    public partial class LayersPanel : UserControl
    {
        private MainViewModel? ViewModel => DataContext as MainViewModel;
        private Point _layerDragStart;

        public LayersPanel()
        {
            InitializeComponent();
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

        private void LayerName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || sender is not TextBox tb) return;
            int index = LayerList.Items.IndexOf(tb.DataContext);
            if (index >= 0)
                ViewModel.UpdateLayerName(index, tb.Text);
        }

        private void LayerName_KeyDown(object sender, KeyEventArgs e)
        {
            if (ViewModel == null || sender is not TextBox tb) return;
            int index = LayerList.Items.IndexOf(tb.DataContext);
            if (index < 0) return;

            if (e.Key == Key.Enter)
            {
                ViewModel.UpdateLayerName(index, tb.Text);
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (tb.DataContext is LayerItemViewModel item)
                    tb.Text = item.Name;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void LayerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _layerDragStart = e.GetPosition(null);
        }

        private void LayerList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _layerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _layerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            if (LayerList.SelectedItem is LayerItemViewModel item)
                DragDrop.DoDragDrop(LayerList, item, DragDropEffects.Move);
        }

        private void LayerList_Drop(object sender, DragEventArgs e)
        {
            if (ViewModel == null || !e.Data.GetDataPresent(typeof(LayerItemViewModel))) return;
            var sourceItem = (LayerItemViewModel)e.Data.GetData(typeof(LayerItemViewModel));
            int from = LayerList.Items.IndexOf(sourceItem);
            if (from < 0) return;

            var target = (DependencyObject)e.OriginalSource;
            while (target != null && target is not ListBoxItem)
                target = VisualTreeHelper.GetParent(target);

            int to = target is ListBoxItem lbi
                ? LayerList.ItemContainerGenerator.IndexFromContainer(lbi)
                : LayerList.Items.Count - 1;

            if (to >= 0)
                ViewModel.MoveLayer(from, to);
        }
    }
}
