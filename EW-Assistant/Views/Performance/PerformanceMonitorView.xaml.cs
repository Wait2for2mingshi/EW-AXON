using EW_Assistant.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EW_Assistant.Views
{
    public partial class PerformanceMonitorView : UserControl
    {
        private readonly PerformanceMonitorViewModel _viewModel;

        public PerformanceMonitorView()
        {
            InitializeComponent();
            _viewModel = new PerformanceMonitorViewModel(Dispatcher);
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.Stop();
        }

        private async void OnTestCpuAlertClick(object sender, RoutedEventArgs e)
        {
            await _viewModel.TriggerTestCpuAlertAndAnalyzeAsync();
        }

        private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = sender as ScrollViewer ?? FindScrollViewer(sender as DependencyObject);
            if (sv == null) return;

            double factor = 1.5;
            double delta = -e.Delta * factor;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
            e.Handled = true;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject source)
        {
            if (source == null) return null;
            if (source is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(source);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
