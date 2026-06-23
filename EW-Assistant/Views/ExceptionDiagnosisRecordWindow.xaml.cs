using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EW_Assistant.Views
{
    public partial class ExceptionDiagnosisRecordWindow : Window
    {
        private ScrollViewer _aiReplyScrollViewer;

        public ExceptionDiagnosisRecordWindow(RecordDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? new RecordDialogViewModel();
            DataContext = ViewModel;
            Loaded += ExceptionDiagnosisRecordWindow_Loaded;
        }

        public RecordDialogViewModel ViewModel { get; }

        private static string L(string chineseText)
        {
            return UiLanguageService.Text(chineseText, ConfigService.Current);
        }

        private async void ExceptionDiagnosisRecordWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ExceptionDiagnosisRecordWindow_Loaded;
            await ViewModel.ReloadTemplatesAsync();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AiReplyMarkdownViewer?.UpdateLayout();
                MarkdownFontHelper.ApplyAppFont(AiReplyMarkdownViewer?.Document, 14);
            }), DispatcherPriority.ContextIdle);
        }

        private void AiReplyMarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var innerScroller = GetAiReplyScrollViewer();
            if (innerScroller == null)
            {
                return;
            }

            if (innerScroller.ScrollableHeight > 0)
            {
                var scrollingDown = e.Delta < 0;
                var atTop = innerScroller.VerticalOffset <= 0;
                var atBottom = innerScroller.VerticalOffset >= innerScroller.ScrollableHeight;

                if ((scrollingDown && !atBottom) || (!scrollingDown && !atTop))
                {
                    var delta = -e.Delta * 1.5;
                    innerScroller.ScrollToVerticalOffset(innerScroller.VerticalOffset + delta);
                    e.Handled = true;
                    return;
                }
            }

            if (RootScrollViewer != null)
            {
                var delta = -e.Delta * 1.5;
                RootScrollViewer.ScrollToVerticalOffset(RootScrollViewer.VerticalOffset + delta);
                e.Handled = true;
            }
        }

        private void VisionImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            e.Handled = true;
            OpenVisionImagePreviewWindow(ViewModel.VisionImagePathText);
        }

        private void OpenVisionImagePreviewWindow(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                MessageBox.Show(this, L("图片文件无法打开：") + imagePath, L("图片预览"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ImageSource imageSource;
            try
            {
                imageSource = LoadOriginalImage(imagePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("图片文件无法打开：") + ex.Message, L("图片预览"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var scale = new ScaleTransform(1, 1);
            var image = new Image
            {
                Source = imageSource,
                Stretch = Stretch.None,
                LayoutTransform = scale,
                SnapsToDevicePixels = true,
                Cursor = Cursors.Hand
            };

            var scroll = new ScrollViewer
            {
                Content = image,
                Background = Brushes.Black,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var window = new Window
            {
                Title = string.IsNullOrWhiteSpace(Path.GetFileName(imagePath)) ? L("图片预览") : Path.GetFileName(imagePath),
                Width = 980,
                Height = 720,
                MinWidth = 520,
                MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                Content = scroll,
                Owner = this
            };

            void SetScale(double value)
            {
                var next = Math.Max(0.1, Math.Min(8.0, value));
                scale.ScaleX = next;
                scale.ScaleY = next;
            }

            void FitToWindow()
            {
                if (!(imageSource is BitmapSource bitmap))
                {
                    SetScale(1);
                    return;
                }

                var viewportWidth = Math.Max(1, scroll.ViewportWidth - 24);
                var viewportHeight = Math.Max(1, scroll.ViewportHeight - 24);
                var fit = Math.Min(viewportWidth / bitmap.PixelWidth, viewportHeight / bitmap.PixelHeight);
                SetScale(double.IsInfinity(fit) || double.IsNaN(fit) ? 1 : Math.Min(1, fit));
            }

            scroll.PreviewMouseWheel += (_, args) =>
            {
                var oldScale = scale.ScaleX;
                var factor = args.Delta > 0 ? 1.12 : 0.88;
                var mouseOnImage = args.GetPosition(image);
                var mouseOnViewport = args.GetPosition(scroll);

                SetScale(oldScale * factor);
                var newScale = scale.ScaleX;
                scroll.UpdateLayout();
                scroll.ScrollToHorizontalOffset(mouseOnImage.X * newScale - mouseOnViewport.X);
                scroll.ScrollToVerticalOffset(mouseOnImage.Y * newScale - mouseOnViewport.Y);
                args.Handled = true;
            };

            var isDragging = false;
            var lastDragPoint = new Point();

            scroll.PreviewMouseLeftButtonDown += (_, args) =>
            {
                isDragging = true;
                lastDragPoint = args.GetPosition(scroll);
                scroll.CaptureMouse();
                image.Cursor = Cursors.SizeAll;
                args.Handled = true;
            };

            scroll.PreviewMouseMove += (_, args) =>
            {
                if (!isDragging || args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                var current = args.GetPosition(scroll);
                var delta = current - lastDragPoint;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - delta.X);
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - delta.Y);
                lastDragPoint = current;
                args.Handled = true;
            };

            scroll.PreviewMouseLeftButtonUp += (_, args) =>
            {
                if (!isDragging)
                {
                    return;
                }

                isDragging = false;
                scroll.ReleaseMouseCapture();
                image.Cursor = Cursors.Hand;
                args.Handled = true;
            };

            scroll.LostMouseCapture += (_, __) =>
            {
                isDragging = false;
                image.Cursor = Cursors.Hand;
            };

            window.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    window.Close();
                    args.Handled = true;
                }
            };
            window.Loaded += (_, __) => window.Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Background);
            window.Show();
        }

        private static ImageSource LoadOriginalImage(string filePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanSave)
            {
                MessageBox.Show(this, L("请先填写现象和人工对策。"), L("异常诊断"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private async void BtnAddTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanManageTemplateContent)
            {
                MessageBox.Show(this, L("请先确认当前记录带有报警内容，并填写现象和人工对策，再加入备选列表。"), L("异常诊断"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ViewModel.SetTemplateBusy(true);
            try
            {
                var saved = await Task.Run(() => ExceptionDiagnosisTemplateService.UpsertTemplate(
                    null,
                    ViewModel.AlarmContentText,
                    ViewModel.PhenomenonText,
                    ViewModel.ManualCountermeasureText));
                if (saved == null)
                {
                    MessageBox.Show(this, L("加入备选失败，请稍后重试。"), L("异常诊断"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await ViewModel.ReloadTemplatesAsync(saved.Id, manageBusyState: false);
            }
            finally
            {
                ViewModel.SetTemplateBusy(false);
            }
        }

        private async void BtnUpdateTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanUpdateTemplate)
            {
                return;
            }

            ViewModel.SetTemplateBusy(true);
            try
            {
                var saved = await Task.Run(() => ExceptionDiagnosisTemplateService.UpsertTemplate(
                    ViewModel.SelectedTemplate?.Id,
                    ViewModel.SelectedTemplate?.AlarmContent,
                    ViewModel.PhenomenonText,
                    ViewModel.ManualCountermeasureText));
                if (saved == null)
                {
                    MessageBox.Show(this, L("更新备选项失败，请稍后重试。"), L("异常诊断"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await ViewModel.ReloadTemplatesAsync(saved.Id, manageBusyState: false);
            }
            finally
            {
                ViewModel.SetTemplateBusy(false);
            }
        }

        private async void BtnDeleteTemplate_Click(object sender, RoutedEventArgs e)
        {
            var selected = ViewModel.SelectedTemplate;
            if (selected == null)
            {
                return;
            }

            var result = MessageBox.Show(
                this,
                L("确定删除当前备选项吗？删除后不会影响已保存到 CSV 的历史记录。"),
                L("异常诊断"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            ViewModel.SetTemplateBusy(true);
            try
            {
                var deleted = await Task.Run(() => ExceptionDiagnosisTemplateService.DeleteTemplate(selected.Id));
                if (!deleted)
                {
                    MessageBox.Show(this, L("删除备选项失败，请稍后重试。"), L("异常诊断"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await ViewModel.ReloadTemplatesAsync(manageBusyState: false);
            }
            finally
            {
                ViewModel.SetTemplateBusy(false);
            }
        }

        private ScrollViewer GetAiReplyScrollViewer()
        {
            if (_aiReplyScrollViewer == null)
            {
                _aiReplyScrollViewer = FindChildScrollViewer(AiReplyMarkdownViewer);
            }

            return _aiReplyScrollViewer;
        }

        private static ScrollViewer FindChildScrollViewer(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
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

        public sealed class RecordDialogViewModel : INotifyPropertyChanged
        {
            private string _csvPathText = string.Empty;
            private string _triggerTimeText = string.Empty;
            private string _alarmContentText = string.Empty;
            private string _aiReplyText = string.Empty;
            private string _mcpCallText = string.Empty;
            private string _visionImagePathText = string.Empty;
            private string _phenomenonText = string.Empty;
            private string _manualCountermeasureText = string.Empty;
            private DiagnosisTemplateListItem _selectedTemplate;
            private bool _isTemplateBusy;

            public event PropertyChangedEventHandler PropertyChanged;

            public bool CanSave => HasValue(PhenomenonText) && HasValue(ManualCountermeasureText);
            public bool CanManageTemplateContent => !IsTemplateBusy && CanSave && HasValue(AlarmContentText);
            public bool CanUpdateTemplate => !IsTemplateBusy && CanManageTemplateContent && SelectedTemplate != null;
            public bool CanDeleteTemplate => !IsTemplateBusy && SelectedTemplate != null;
            public bool HasTemplateItems => TemplateItems.Count > 0;
            public bool HasNoTemplateItems => !IsTemplateBusy && !HasTemplateItems;
            public bool CanSelectTemplate => !IsTemplateBusy && HasTemplateItems;
            public bool HasVisionImage => HasExistingFile(VisionImagePathText);

            public ObservableCollection<DiagnosisTemplateListItem> TemplateItems { get; } =
                new ObservableCollection<DiagnosisTemplateListItem>();

            public string CsvPathText
            {
                get => _csvPathText;
                set => SetField(ref _csvPathText, value);
            }

            public string TriggerTimeText
            {
                get => _triggerTimeText;
                set => SetField(ref _triggerTimeText, value);
            }

            public string AlarmContentText
            {
                get => _alarmContentText;
                set => SetField(ref _alarmContentText, value, affectsCanSave: true);
            }

            public string AiReplyText
            {
                get => _aiReplyText;
                set => SetField(ref _aiReplyText, value);
            }

            public string McpCallText
            {
                get => _mcpCallText;
                set => SetField(ref _mcpCallText, value);
            }

            public string VisionImagePathText
            {
                get => _visionImagePathText;
                set
                {
                    if (SetField(ref _visionImagePathText, value))
                    {
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVisionImage)));
                    }
                }
            }

            public string PhenomenonText
            {
                get => _phenomenonText;
                set => SetField(ref _phenomenonText, value, affectsCanSave: true);
            }

            public string ManualCountermeasureText
            {
                get => _manualCountermeasureText;
                set => SetField(ref _manualCountermeasureText, value, affectsCanSave: true);
            }

            public bool IsTemplateBusy
            {
                get => _isTemplateBusy;
                private set
                {
                    if (_isTemplateBusy == value)
                    {
                        return;
                    }

                    _isTemplateBusy = value;
                    OnTemplateStateChanged();
                }
            }

            public DiagnosisTemplateListItem SelectedTemplate
            {
                get => _selectedTemplate;
                set
                {
                    if (!SetField(ref _selectedTemplate, value, affectsSelectionState: true))
                    {
                        return;
                    }

                    if (value == null)
                    {
                        return;
                    }

                    PhenomenonText = value.Phenomenon;
                    ManualCountermeasureText = value.ManualCountermeasure;
                }
            }

            public async Task ReloadTemplatesAsync(string preferredTemplateId = null, bool manageBusyState = true)
            {
                var selectionId = !string.IsNullOrWhiteSpace(preferredTemplateId)
                    ? preferredTemplateId
                    : SelectedTemplate?.Id;
                var alarmContent = AlarmContentText;

                if (manageBusyState)
                {
                    IsTemplateBusy = true;
                }

                try
                {
                    var templates = await Task.Run(() => BuildTemplateItems(alarmContent));
                    ReplaceTemplateItems(templates);

                    if (!string.IsNullOrWhiteSpace(selectionId))
                    {
                        var preferredItem = FindTemplateItem(selectionId);
                        if (preferredItem != null)
                        {
                            SelectedTemplate = preferredItem;
                            return;
                        }
                    }

                    SelectedTemplate = null;
                }
                finally
                {
                    if (manageBusyState)
                    {
                        IsTemplateBusy = false;
                    }
                }
            }

            public void SetTemplateBusy(bool value)
            {
                IsTemplateBusy = value;
            }

            private static List<DiagnosisTemplateListItem> BuildTemplateItems(string alarmContent)
            {
                return ExceptionDiagnosisTemplateService.LoadTemplatesForAlarm(alarmContent)
                    .Select(item => new DiagnosisTemplateListItem
                    {
                        Id = item.Id,
                        AlarmContent = item.AlarmContent,
                        Phenomenon = item.Phenomenon,
                        ManualCountermeasure = item.ManualCountermeasure,
                        PhenomenonPrefixText = L("现象: "),
                        CountermeasurePrefixText = L("对策: "),
                        PhenomenonPreview = BuildPreview(item.Phenomenon, 32),
                        CountermeasurePreview = BuildPreview(item.ManualCountermeasure, 46),
                        LastUsedText = BuildLastUsedText(item),
                        SortTime = ResolveSortTime(item)
                    })
                    .OrderByDescending(item => item.SortTime)
                    .ThenBy(item => item.Phenomenon ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private void ReplaceTemplateItems(IEnumerable<DiagnosisTemplateListItem> items)
            {
                TemplateItems.Clear();
                foreach (var item in items ?? Enumerable.Empty<DiagnosisTemplateListItem>())
                {
                    TemplateItems.Add(item);
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTemplateItems)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoTemplateItems)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelectTemplate)));
            }

            private DiagnosisTemplateListItem FindTemplateItem(string templateId)
            {
                return TemplateItems.FirstOrDefault(item =>
                    string.Equals(item.Id, templateId, StringComparison.OrdinalIgnoreCase));
            }

            private bool SetField<T>(
                ref T field,
                T value,
                bool affectsCanSave = false,
                bool affectsSelectionState = false,
                [CallerMemberName] string propertyName = null)
            {
                if (Equals(field, value))
                {
                    return false;
                }

                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                if (affectsCanSave)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSave)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanManageTemplateContent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdateTemplate)));
                }

                if (affectsSelectionState)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdateTemplate)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteTemplate)));
                }

                return true;
            }

            private void OnTemplateStateChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTemplateBusy)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanManageTemplateContent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdateTemplate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteTemplate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoTemplateItems)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelectTemplate)));
            }

            private static bool HasValue(string value)
            {
                return !string.IsNullOrWhiteSpace(value);
            }

            private static bool HasExistingFile(string path)
            {
                try
                {
                    return !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());
                }
                catch
                {
                    return false;
                }
            }

            private static string BuildPreview(string text, int maxLength)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Empty;
                }

                var normalized = text
                    .Replace("\r", " ")
                    .Replace("\n", " / ")
                    .Trim();
                if (normalized.Length <= maxLength)
                {
                    return normalized;
                }

                return normalized.Substring(0, Math.Max(1, maxLength - 3)) + "...";
            }

            private static string BuildLastUsedText(ExceptionDiagnosisTemplateService.DiagnosisTemplateRecord item)
            {
                var referenceTime = ResolveSortTime(item);
                return referenceTime == default
                    ? L("经验项")
                    : referenceTime.ToString("MM-dd HH:mm");
            }

            private static DateTime ResolveSortTime(ExceptionDiagnosisTemplateService.DiagnosisTemplateRecord item)
            {
                return item?.LastUsedAt != default
                    ? item.LastUsedAt
                    : (item?.UpdatedAt != default ? item.UpdatedAt : item?.CreatedAt ?? default);
            }
        }

        public sealed class DiagnosisTemplateListItem
        {
            public string Id { get; set; } = string.Empty;
            public string AlarmContent { get; set; } = string.Empty;
            public string Phenomenon { get; set; } = string.Empty;
            public string ManualCountermeasure { get; set; } = string.Empty;
            public string PhenomenonPrefixText { get; set; } = string.Empty;
            public string CountermeasurePrefixText { get; set; } = string.Empty;
            public string PhenomenonPreview { get; set; } = string.Empty;
            public string CountermeasurePreview { get; set; } = string.Empty;
            public string LastUsedText { get; set; } = string.Empty;
            public DateTime SortTime { get; set; }
        }
    }
}
