using EW_Assistant.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EW_Assistant.Views
{
    /// <summary>
    /// 预警中心视图。
    /// </summary>
    public partial class WarningCenterView : UserControl
    {
        public WarningCenterViewModel ViewModel { get; }

        public WarningCenterView()
        {
            ViewModel = new WarningCenterViewModel();
            InitializeComponent();
            DataContext = ViewModel;
            AiMarkdownViewer.PreviewMouseWheel += AiMarkdownViewer_PreviewMouseWheel;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateFilterButtons(ViewModel.FilterStatus);
            Loaded += WarningCenterView_Loaded;
            Unloaded += WarningCenterView_Unloaded;
        }

        private void WarningCenterView_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Attach();
        }

        private void WarningCenterView_Unloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Detach();
        }

        private void WarningList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ScrollAiCardToTop), DispatcherPriority.Background);
        }

        private void ScrollAiCardToTop()
        {
            if (AiMarkdownViewer == null)
            {
                return;
            }

            AiMarkdownViewer.UpdateLayout();
            var scroller = FindChildScrollViewer(AiMarkdownViewer);
            if (scroller != null)
            {
                scroller.ScrollToVerticalOffset(0);
            }
        }

        private void AiMarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scroller = FindChildScrollViewer(AiMarkdownViewer);
            if (scroller == null)
            {
                return;
            }

            var multiplier = 1.5;
            var target = scroller.VerticalOffset - e.Delta * multiplier;
            scroller.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private static ScrollViewer FindChildScrollViewer(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv)
                {
                    return sv;
                }

                var result = FindChildScrollViewer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void BtnProcessed_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.MarkProcessedSelected();
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not ToggleButton btn)
                {
                    return;
                }

                var tag = btn.Tag as string;
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    ViewModel.FilterStatus = tag;
                    UpdateFilterButtons(tag);
                }
            }
            catch
            {
                // ignore UI errors
            }
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.FilterStatus))
            {
                UpdateFilterButtons(ViewModel.FilterStatus);
            }
        }

        private void UpdateFilterButtons(string status)
        {
            var s = (status ?? string.Empty).Trim().ToLowerInvariant();
            BtnFilterPending.IsChecked = s == "pending" || string.IsNullOrEmpty(s);
            BtnFilterProcessed.IsChecked = s == "processed";
            BtnFilterResolved.IsChecked = s == "resolved";
        }
    }
}
