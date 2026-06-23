using ClosedXML.Excel;
using EW_Assistant.Component.Checklist;
using EW_Assistant.Component.MindMap;
using EW_Assistant.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Linq;

namespace EW_Assistant.Views
{
    /// <summary>
    /// 文档知识导图视图：接入 Workflow，对任意上传文件进行结构化解析并绘制思维导图。
    /// </summary>
    public partial class DocumentAiView : UserControl, INotifyPropertyChanged
    {
        private readonly DocumentMindMapParser _mindMapParser = new DocumentMindMapParser();
        private readonly DocumentChecklistParser _checklistParser = new DocumentChecklistParser();
        private MindMapNode _root;
        private DocumentChecklist _currentChecklist;
        private MindMapNode _selectedNode;
        private bool _isBusy;
        private bool _isDragHover;
        private string _statusText = L("拖拽或选择文档，然后点击想执行的模式");
        private string _outlineSummary = L("尚未加载文档");
        private double _canvasWidth = 1200;
        private double _canvasHeight = 800;
        private bool _isPanning;
        private Point _panStart;
        private Point _panOrigin;
        private double _zoom = 1.0;
        private const double MinZoom = 0.3;
        private const double MaxZoom = 3.0;
        private readonly Dictionary<MindMapNode, MindMapVisualNode> _visualLookup = new Dictionary<MindMapNode, MindMapVisualNode>();
        private bool _layoutRefreshScheduled;
        private MindMapNode _layoutRefreshFocus;
        private bool _layoutRefreshReset;
        private bool _resetViewAfterMeasure;
        private string _currentFilePath;
        private string _currentFileName = L("未选择文件");
        private DocumentAiViewMode _currentMode = DocumentAiViewMode.None;
        private readonly IList<ChecklistStatusOption> _checklistStatusOptions = new List<ChecklistStatusOption>(ChecklistItemStatusHelper.GetOptions());
        private ScrollViewer _mindMapScroll;
        private Canvas _mindMapCanvas;
        private ScaleTransform _zoomTransform;
        private TranslateTransform _panTransform;

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        public DocumentAiView()
        {
            InitializeComponent();
            ResolveTemplateReferences();
            VisualNodes = new ObservableCollection<MindMapVisualNode>();
            VisualEdges = new ObservableCollection<MindMapEdge>();
            ChecklistGroups = new ObservableCollection<ChecklistGroup>();
            ChecklistGroups.CollectionChanged += ChecklistGroups_CollectionChanged;
            DataContext = this;
        }

        public ObservableCollection<MindMapVisualNode> VisualNodes { get; }
        public ObservableCollection<MindMapEdge> VisualEdges { get; }
        public ObservableCollection<ChecklistGroup> ChecklistGroups { get; }
        public IList<ChecklistStatusOption> ChecklistStatusOptions => _checklistStatusOptions;

        private void ChecklistGroups_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasChecklist));
            OnPropertyChanged(nameof(CanExport));
        }

        public MindMapNode SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (_selectedNode == value) return;
                if (_selectedNode != null) _selectedNode.IsSelected = false;
                _selectedNode = value;
                if (_selectedNode != null) _selectedNode.IsSelected = true;
                OnPropertyChanged();
            }
        }

        public bool HasMindmap => _root != null;
        public bool HasChecklist => ChecklistGroups.Count > 0;
        public bool HasFile => !string.IsNullOrWhiteSpace(_currentFilePath);

        public string CurrentFileName
        {
            get => _currentFileName;
            private set
            {
                if (string.Equals(_currentFileName, value, StringComparison.Ordinal)) return;
                _currentFileName = value;
                OnPropertyChanged();
            }
        }

        public DocumentAiViewMode CurrentMode => _currentMode;
        public bool IsMindmapMode => _currentMode == DocumentAiViewMode.Mindmap;
        public bool IsChecklistMode => _currentMode == DocumentAiViewMode.Checklist;
        public bool CanGenerateMindmap => HasFile && !IsBusy;
        public bool CanGenerateChecklist => HasFile && !IsBusy;
        public bool CanExport => !IsBusy && ((IsMindmapMode && HasMindmap) || (IsChecklistMode && HasChecklist));

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateMindmap));
                OnPropertyChanged(nameof(CanGenerateChecklist));
                OnPropertyChanged(nameof(CanExport));
            }
        }

        public bool IsDragHover
        {
            get => _isDragHover;
            set { if (_isDragHover == value) return; _isDragHover = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
        }

        public string OutlineSummary
        {
            get => _outlineSummary;
            set { if (_outlineSummary == value) return; _outlineSummary = value; OnPropertyChanged(); }
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            set { if (Math.Abs(_canvasWidth - value) < 0.1) return; _canvasWidth = value; OnPropertyChanged(); }
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set { if (Math.Abs(_canvasHeight - value) < 0.1) return; _canvasHeight = value; OnPropertyChanged(); }
        }

        private void DropHost_OnDrop(object sender, DragEventArgs e)
        {
            IsDragHover = false;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            var target = files.FirstOrDefault(IsSupportedFile);
            if (target == null)
            {
                StatusText = L("未找到可处理的文件，请重新选择");
                return;
            }
            SetCurrentFile(target);
        }

        private void DropHost_OnPreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var allow = files?.Any(IsSupportedFile) == true;
            e.Effects = allow ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void DropHost_OnPreviewDragEnter(object sender, DragEventArgs e)
        {
            IsDragHover = true;
            DropHost_OnPreviewDragOver(sender, e);
        }

        private void DropHost_OnPreviewDragLeave(object sender, DragEventArgs e)
        {
            IsDragHover = false;
        }

        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = L("所有文件") + "|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                SetCurrentFile(dialog.FileName);
            }
        }

        private void SetCurrentFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !IsSupportedFile(path))
            {
                StatusText = L("文件不可用，请重新选择");
                return;
            }

            _currentFilePath = path;
            CurrentFileName = Path.GetFileName(path);
            ResetMindmap();
            ResetChecklist();
            SetMode(DocumentAiViewMode.None);
            StatusText = string.Format(L("已选择 {0}，请点击上方按钮生成"), CurrentFileName);
            MainWindow.PostProgramInfo("[DocumentAI] " + string.Format(L("已选择 {0}，请点击上方按钮生成"), CurrentFileName), "info");
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(CanGenerateMindmap));
            OnPropertyChanged(nameof(CanGenerateChecklist));
        }

        private async void BtnGenerateMindmap_Click(object sender, RoutedEventArgs e)
        {
            await GenerateMindmapAsync();
        }

        private async void BtnGenerateChecklist_Click(object sender, RoutedEventArgs e)
        {
            await GenerateChecklistAsync();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (IsMindmapMode && HasMindmap)
            {
                ExportMindmapOpml();
                return;
            }
            if (IsChecklistMode && HasChecklist)
            {
                ExportChecklistExcel();
                return;
            }

            StatusText = L("当前模式暂无可导出的内容");
        }

        private async Task GenerateMindmapAsync(bool forceRefresh = false)
        {
            if (IsBusy)
            {
                StatusText = L("正在处理其他任务，请稍候");
                return;
            }
            if (!HasFile)
            {
                StatusText = L("请先选择文件");
                return;
            }

            if (!forceRefresh && _root != null)
            {
                SetMode(DocumentAiViewMode.Mindmap);
                StatusText = string.Format(L("已加载缓存的思维导图（{0}）"), CurrentFileName);
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = string.Format(L("正在解析：{0}"), CurrentFileName);
                MainWindow.PostProgramInfo("[DocumentAI] Mindmap Workflow " + string.Format(L("正在解析：{0}"), CurrentFileName), "info");
                var tree = await _mindMapParser.ParseAsync(_currentFilePath);
                _root = tree;
                OnPropertyChanged(nameof(HasMindmap));
                OnPropertyChanged(nameof(CanExport));
                _resetViewAfterMeasure = true;
                Walk(_root, n => n.IsExpanded = false);
                _root.IsExpanded = false;
                SelectedNode = tree;
                RebuildScene(_root, true);
                SetMode(DocumentAiViewMode.Mindmap);
                StatusText = string.Format(L("解析完成（{0}）"), CurrentFileName);
                MainWindow.PostProgramInfo("[DocumentAI] " + string.Format(L("解析完成（{0}）"), CurrentFileName), "ok");
            }
            catch (Exception ex)
            {
                StatusText = L("解析失败：") + ex.Message;
                MainWindow.PostProgramInfo("[DocumentAI] " + L("解析失败：") + ex.Message, "error");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task GenerateChecklistAsync(bool forceRefresh = false)
        {
            if (IsBusy)
            {
                StatusText = L("正在处理其他任务，请稍候");
                return;
            }
            if (!HasFile)
            {
                StatusText = L("请先选择文件");
                return;
            }

            if (!forceRefresh && _currentChecklist != null && ChecklistGroups.Count > 0)
            {
                SetMode(DocumentAiViewMode.Checklist);
                StatusText = string.Format(L("已加载缓存的 Checklist（{0}）"), CurrentFileName);
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = string.Format(L("正在生成 Checklist：{0}"), CurrentFileName);
                MainWindow.PostProgramInfo("[DocumentAI] Workflow " + string.Format(L("正在生成 Checklist：{0}"), CurrentFileName), "info");
                var checklist = await _checklistParser.ParseAsync(_currentFilePath);
                ApplyChecklist(checklist);
                SetMode(DocumentAiViewMode.Checklist);
                StatusText = string.Format(L("Checklist 生成完成（{0}）"), CurrentFileName);
                MainWindow.PostProgramInfo("[DocumentAI] " + string.Format(L("Checklist 生成完成（{0}）"), CurrentFileName), "ok");
            }
            catch (Exception ex)
            {
                StatusText = L("Checklist 生成失败：") + ex.Message;
                MainWindow.PostProgramInfo("[DocumentAI] " + L("Checklist 生成失败：") + ex.Message, "error");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ExportMindmapOpml()
        {
            if (!HasMindmap || _root == null)
            {
                StatusText = L("暂无可导出的思维导图");
                return;
            }
            if (!IsMindmapMode)
            {
                StatusText = L("请切换到思维导图模式后再导出");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = L("OPML 文件") + "|*.opml",
                FileName = BuildMindmapFileName()
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var now = DateTime.Now;
                var document = new XDocument(
                    new XDeclaration("1.0", "utf-8", "yes"),
                    new XElement("opml",
                        new XAttribute("version", "2.0"),
                        new XElement("head",
                            new XElement("title", ResolveMindmapTitle()),
                            new XElement("dateCreated", now.ToString("yyyy-MM-ddTHH:mm:sszzz")),
                            new XElement("dateModified", now.ToString("yyyy-MM-ddTHH:mm:sszzz"))
                        ),
                        new XElement("body", BuildOutlineElement(_root))
                    )
                );
                document.Save(dialog.FileName);

                StatusText = L("思维导图 OPML 导出成功");
                MainWindow.PostProgramInfo("[DocumentAI] " + L("思维导图 OPML 导出成功") + ": " + dialog.FileName, "ok");
            }
            catch (Exception ex)
            {
                StatusText = L("思维导图导出失败：") + ex.Message;
                MainWindow.PostProgramInfo("[DocumentAI] " + L("思维导图导出失败：") + ex.Message, "error");
            }
        }

        private void ExportChecklistExcel()
        {
            if (!HasChecklist)
            {
                StatusText = L("暂无 Checklist 可导出");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = L("Excel 文件") + "|*.xlsx",
                FileName = BuildChecklistFileName()
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Checklist");
                    var headers = new[]
                    {
                        L("分组序号"),
                        L("分组标题"),
                        L("步骤序号"),
                        L("步骤标题"),
                        L("步骤内容"),
                        L("状态编码"),
                        L("状态文本"),
                        L("备注")
                    };
                    for (var i = 0; i < headers.Length; i++)
                    {
                        var cell = worksheet.Cell(1, i + 1);
                        cell.Value = headers[i];
                    }
                    var headerRange = worksheet.Range(1, 1, 1, headers.Length);
                    headerRange.Style.Font.Bold = true;
                    headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
                    worksheet.Row(1).Height = 20;

                    var row = 2;
                    foreach (var group in ChecklistGroups.OrderBy(g => g.Order))
                    {
                        var items = group.Items ?? new ObservableCollection<ChecklistItem>();
                        foreach (var item in items.OrderBy(i => i.Order))
                        {
                            worksheet.Cell(row, 1).Value = group.Order;
                            worksheet.Cell(row, 2).Value = group.Title;
                            worksheet.Cell(row, 3).Value = item.Order;
                            worksheet.Cell(row, 4).Value = item.Title;
                            var detailCell = worksheet.Cell(row, 5);
                            detailCell.Value = item.Detail ?? string.Empty;
                            detailCell.Style.Alignment.WrapText = true;
                            worksheet.Cell(row, 6).Value = item.Status.ToString();
                            worksheet.Cell(row, 7).Value = ChecklistItemStatusHelper.GetDisplayName(item.Status);
                            var noteCell = worksheet.Cell(row, 8);
                            noteCell.Value = item.Note ?? string.Empty;
                            noteCell.Style.Alignment.WrapText = true;

                            var rowRange = worksheet.Range(row, 1, row, headers.Length);
                            ApplyStatusFill(rowRange, item.Status);
                            row++;
                        }
                    }

                    var lastRow = row - 1;
                    if (lastRow >= 1)
                    {
                        var usedRange = worksheet.Range(1, 1, lastRow, headers.Length);
                        usedRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        usedRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                        usedRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                    }

                    worksheet.Column(1).Width = 10;
                    worksheet.Column(2).Width = 20;
                    worksheet.Column(3).Width = 10;
                    worksheet.Column(4).Width = 30;
                    worksheet.Column(5).Width = 45;
                    worksheet.Column(6).Width = 12;
                    worksheet.Column(7).Width = 12;
                    worksheet.Column(8).Width = 45;

                    worksheet.Column(5).Style.Alignment.WrapText = true;
                    worksheet.Column(8).Style.Alignment.WrapText = true;

                    if (lastRow >= 2)
                    {
                        worksheet.Column(2).AdjustToContents(2, lastRow);
                        worksheet.Column(4).AdjustToContents(2, lastRow);
                    }

                    worksheet.SheetView.FreezeRows(1);
                    workbook.SaveAs(dialog.FileName);
                }

                StatusText = L("Checklist Excel 导出成功");
                MainWindow.PostProgramInfo("[DocumentAI] " + L("Checklist Excel 导出成功") + ": " + dialog.FileName, "ok");
            }
            catch (Exception ex)
            {
                StatusText = L("导出失败：") + ex.Message;
                MainWindow.PostProgramInfo("[DocumentAI] " + L("导出失败：") + ex.Message, "error");
            }
        }

        private static void ApplyStatusFill(IXLRange range, ChecklistItemStatus status)
        {
            XLColor color;
            switch (status)
            {
                case ChecklistItemStatus.Pending:
                    color = XLColor.LightGoldenrodYellow;
                    break;
                case ChecklistItemStatus.Done:
                    color = XLColor.LightGreen;
                    break;
                case ChecklistItemStatus.Abnormal:
                    color = XLColor.LightSalmon;
                    break;
                default:
                    return;
            }

            range.Style.Fill.BackgroundColor = color;
        }

        private void ApplyChecklist(DocumentChecklist checklist)
        {
            _currentChecklist = checklist;
            ChecklistGroups.Clear();
            if (checklist != null)
            {
                foreach (var group in checklist.Groups)
                    ChecklistGroups.Add(group);
            }
            OnPropertyChanged(nameof(HasChecklist));
            OnPropertyChanged(nameof(CanExport));
        }

        private void ResetMindmap()
        {
            _root = null;
            SelectedNode = null;
            VisualNodes.Clear();
            VisualEdges.Clear();
            _visualLookup.Clear();
            OutlineSummary = L("尚未生成思维导图");
            OnPropertyChanged(nameof(HasMindmap));
            OnPropertyChanged(nameof(CanExport));
        }

        private void ResetChecklist()
        {
            _currentChecklist = null;
            ChecklistGroups.Clear();
        }

        private void SetMode(DocumentAiViewMode mode)
        {
            if (_currentMode == mode) return;
            _currentMode = mode;
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(IsMindmapMode));
            OnPropertyChanged(nameof(IsChecklistMode));
            OnPropertyChanged(nameof(CanExport));
        }

        private string BuildMindmapFileName()
        {
            string candidate = null;
            if (_root != null && !string.IsNullOrWhiteSpace(_root.Title))
                candidate = _root.Title;
            else if (!string.IsNullOrWhiteSpace(CurrentFileName))
            {
                var name = Path.GetFileNameWithoutExtension(CurrentFileName);
                if (!string.IsNullOrWhiteSpace(name))
                    candidate = name + "_Mindmap";
            }

            var safeName = SanitizeFileName(candidate, "Mindmap");
            return safeName + ".opml";
        }

        private string ResolveMindmapTitle()
        {
            if (_root != null && !string.IsNullOrWhiteSpace(_root.Title))
                return _root.Title;
            if (!string.IsNullOrWhiteSpace(CurrentFileName))
                return CurrentFileName;
            return "Mindmap";
        }

        private static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sanitizedChars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var sanitized = new string(sanitizedChars).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
        }

        private XElement BuildOutlineElement(MindMapNode node)
        {
            if (node == null)
                return new XElement("outline");

            var element = new XElement("outline",
                new XAttribute("text", node.Title ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(node.Body))
                element.Add(new XAttribute("note", node.Body));

            element.Add(new XAttribute("isOpen", node.IsExpanded ? "true" : "false"));

            if (node.Children != null && node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    element.Add(BuildOutlineElement(child));
                }
            }

            return element;
        }

        private string BuildChecklistFileName()
        {
            if (!string.IsNullOrWhiteSpace(CurrentFileName))
            {
                var name = Path.GetFileNameWithoutExtension(CurrentFileName);
                if (!string.IsNullOrWhiteSpace(name))
                    return name + "_Checklist.xlsx";
            }
            return "Checklist.xlsx";
        }

        private void RebuildScene(MindMapNode focusNode = null, bool resetView = false)
        {
            if (_root == null)
            {
                VisualNodes.Clear();
                VisualEdges.Clear();
                _visualLookup.Clear();
                CanvasWidth = 900;
                CanvasHeight = 640;
                OutlineSummary = L("尚未生成思维导图");
                if (resetView)
                {
                    _zoom = 1.0;
                    AnimateZoomTo(_zoom);
                    AnimatePanTo(0, 0);
                }
                return;
            }

            Dictionary<MindMapNode, double> knownHeights = null;
            if (_visualLookup.Count > 0)
            {
                knownHeights = new Dictionary<MindMapNode, double>(_visualLookup.Count);
                foreach (var kv in _visualLookup)
                    knownHeights[kv.Key] = kv.Value.Height;
            }

            double ResolveHeight(MindMapNode node)
            {
                if (knownHeights != null && knownHeights.TryGetValue(node, out var recorded) && recorded > 0)
                    return recorded;
                return 80;
            }

            var layout = MindMapLayoutEngine.Layout(
                _root,
                verticalGap: 60,
                nodeHeightProvider: ResolveHeight,
                verticalGapProvider: node =>
                {
                    var height = ResolveHeight(node);
                    var dynamicGap = height * 0.22;
                    if (dynamicGap < 28) dynamicGap = 28;
                    if (dynamicGap > 70) dynamicGap = 70;
                    return dynamicGap;
                });
            var ordered = new List<MindMapVisualNode>();
            var lookup = new Dictionary<MindMapNode, MindMapVisualNode>();

            foreach (var layoutNode in layout.Nodes)
            {
                if (_visualLookup.TryGetValue(layoutNode.Node, out var existing))
                {
                    existing.X = layoutNode.X;
                    existing.Y = layoutNode.Y;
                    lookup[layoutNode.Node] = existing;
                    ordered.Add(existing);
                }
                else
                {
                    ordered.Add(layoutNode);
                    lookup[layoutNode.Node] = layoutNode;
                }
            }

            for (int i = VisualNodes.Count - 1; i >= 0; i--)
            {
                if (!ordered.Contains(VisualNodes[i]))
                    VisualNodes.RemoveAt(i);
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                var desired = ordered[i];
                var currentIndex = VisualNodes.IndexOf(desired);
                if (currentIndex == i) continue;
                if (currentIndex >= 0)
                    VisualNodes.Move(currentIndex, i);
                else
                    VisualNodes.Insert(i, desired);
            }

            VisualEdges.Clear();
            foreach (var edge in layout.Edges)
                VisualEdges.Add(edge);
            _visualLookup.Clear();
            foreach (var kv in lookup)
                _visualLookup[kv.Key] = kv.Value;

            CanvasWidth = layout.Nodes.Count == 0 ? 900 : layout.Nodes.Max(n => n.X + n.Width + 220);
            CanvasHeight = layout.Nodes.Count == 0 ? 640 : layout.Nodes.Max(n => n.Y + n.Height + 180);
            if (resetView)
            {
                _zoom = 1.0;
                ZoomTransformElement.ScaleX = 1;
                ZoomTransformElement.ScaleY = 1;
                PanTransformElement.X = 0;
                PanTransformElement.Y = 0;
            }

            var totalNodes = CountNodes(_root) - 1;
            var depth = CalcDepth(_root) - 1;
            OutlineSummary = string.Format(L("节点 {0} 个 · 最大层级 {1} · 展开中"), Math.Max(totalNodes, 0), Math.Max(depth, 1));

            MindMapVisualNode focusVisual = null;
            if (focusNode != null)
                _visualLookup.TryGetValue(focusNode, out focusVisual);
            if (focusVisual == null && _visualLookup.TryGetValue(_root, out var rootVisual))
                focusVisual = rootVisual;
            if (focusVisual != null)
                FocusOnVisual(focusVisual, resetView);
        }

        private void NodeBorder_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border) return;
            if (border.DataContext is not MindMapVisualNode visual) return;
            SelectedNode = visual.Node;
        }

        private void NodeBorder_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border border && border.DataContext is MindMapVisualNode visual)
            {
                visual.Width = e.NewSize.Width;
                visual.Height = e.NewSize.Height;
                var reset = _resetViewAfterMeasure;
                if (_resetViewAfterMeasure)
                    _resetViewAfterMeasure = false;
                RequestLayoutRefresh(visual.Node, reset);
            }
        }

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle && toggle.DataContext is MindMapVisualNode visual)
            {
                var focus = visual.Node;
                var expanded = toggle.IsChecked == true;
                if (expanded && visual.Node.Children.Count > 0)
                    focus = visual.Node.Children[0];
                RebuildScene(focus);
            }
            else
            {
                RebuildScene();
            }
        }

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) return;
            Walk(_root, n => n.IsExpanded = true);
            RebuildScene(_root);
        }

        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) return;
            foreach (var child in _root.Children)
                Walk(child, n => n.IsExpanded = false);
            _root.IsExpanded = false;
            RebuildScene(_root, true);
        }

        private void Walk(MindMapNode node, Action<MindMapNode> visitor)
        {
            visitor?.Invoke(node);
            foreach (var child in node.Children)
                Walk(child, visitor);
        }

        private void MindMapScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var pointer = e.GetPosition(MindMapCanvasElement);
            var scaleFactor = e.Delta > 0 ? 1.12 : 0.9;
            var targetZoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * scaleFactor));
            scaleFactor = targetZoom / _zoom;
            if (Math.Abs(targetZoom - _zoom) < 0.001) return;
            _zoom = targetZoom;

            var translate = PanTransformElement;
            var targetX = (translate.X - pointer.X) * scaleFactor + pointer.X;
            var targetY = (translate.Y - pointer.Y) * scaleFactor + pointer.Y;

            AnimateZoomTo(_zoom);
            AnimatePanTo(targetX, targetY);
            e.Handled = true;
        }

        private void MindMapCanvas_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetViewToRoot();
                e.Handled = true;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed && !IsNodeElement(e.OriginalSource))
            {
                BeginPan(e.GetPosition(MindMapScrollHost));
                e.Handled = true;
            }
        }

        private void MindMapCanvas_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var current = e.GetPosition(MindMapScrollHost);
            var delta = current - _panStart;
            PanTransformElement.X = _panOrigin.X + delta.X;
            PanTransformElement.Y = _panOrigin.Y + delta.Y;
            e.Handled = true;
        }

        private void MindMapCanvas_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            EndPan();
            e.Handled = true;
        }

        private void MindMapCanvas_OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            EndPan();
        }

        private void BeginPan(Point startPoint)
        {
            _isPanning = true;
            _panStart = startPoint;
            _panOrigin = new Point(PanTransformElement.X, PanTransformElement.Y);
            PanTransformElement.BeginAnimation(TranslateTransform.XProperty, null);
            PanTransformElement.BeginAnimation(TranslateTransform.YProperty, null);
            MindMapCanvasElement.CaptureMouse();
            MindMapCanvasElement.Cursor = Cursors.Hand;
        }

        private void EndPan()
        {
            _isPanning = false;
            MindMapCanvasElement.ReleaseMouseCapture();
            MindMapCanvasElement.Cursor = Cursors.Arrow;
        }

        private static bool IsNodeElement(object source)
        {
            var dep = source as DependencyObject;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && fe.DataContext is MindMapVisualNode)
                    return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        private void MindMapScroll_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetViewToRoot();
                e.Handled = true;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed && !IsNodeElement(e.OriginalSource))
            {
                BeginPan(e.GetPosition(MindMapScrollHost));
                e.Handled = true;
            }
        }

        private void ResetViewToRoot()
        {
            if (_root == null) return;
            AnimateZoomTo(1.0);
            if (_visualLookup.TryGetValue(_root, out var visual))
                FocusOnVisual(visual);
        }

        private void FocusOnVisual(MindMapVisualNode visual, bool resetView = false)
        {
            if (visual == null) return;
            var viewportWidth = MindMapScrollHost.ViewportWidth > 0 ? MindMapScrollHost.ViewportWidth : ActualWidth;
            var viewportHeight = MindMapScrollHost.ViewportHeight > 0 ? MindMapScrollHost.ViewportHeight : ActualHeight;
            var targetX = viewportWidth / 2.0 - visual.CenterX * _zoom;
            var targetY = viewportHeight / 2.0 - visual.CenterY * _zoom;
            if (resetView)
            {
                PanTransformElement.X = targetX;
                PanTransformElement.Y = targetY;
            }
            else
            {
                AnimatePanTo(targetX, targetY);
            }
        }

        private static bool IsSupportedFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return File.Exists(path);
        }

        private static int CountNodes(MindMapNode node)
        {
            if (node == null) return 0;
            int count = 1;
            foreach (var child in node.Children)
                count += CountNodes(child);
            return count;
        }

        private static int CalcDepth(MindMapNode node)
        {
            if (node == null) return 0;
            if (node.Children.Count == 0) return 1;
            int max = 0;
            foreach (var child in node.Children)
                max = Math.Max(max, CalcDepth(child));
            return 1 + max;
        }

        private void RequestLayoutRefresh(MindMapNode focusNode = null, bool resetView = false)
        {
            if (focusNode != null)
                _layoutRefreshFocus = focusNode;
            _layoutRefreshReset = _layoutRefreshReset || resetView;
            if (_layoutRefreshScheduled) return;
            _layoutRefreshScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _layoutRefreshScheduled = false;
                var focus = _layoutRefreshFocus;
                var reset = _layoutRefreshReset;
                _layoutRefreshFocus = null;
                _layoutRefreshReset = false;
                if (_root == null) return;
                RebuildScene(focus ?? _selectedNode ?? _root, reset);
            }), DispatcherPriority.Background);
        }

        private void AnimatePanTo(double targetX, double targetY)
        {
            var duration = TimeSpan.FromMilliseconds(420);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animX = new DoubleAnimation(targetX, duration) { EasingFunction = easing };
            var animY = new DoubleAnimation(targetY, duration) { EasingFunction = easing };
            PanTransformElement.BeginAnimation(TranslateTransform.XProperty, animX);
            PanTransformElement.BeginAnimation(TranslateTransform.YProperty, animY);
        }

        private void AnimateZoomTo(double targetZoom)
        {
            targetZoom = Math.Max(MinZoom, Math.Min(MaxZoom, targetZoom));
            _zoom = targetZoom;
            var duration = TimeSpan.FromMilliseconds(420);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animX = new DoubleAnimation(targetZoom, duration) { EasingFunction = easing };
            var animY = new DoubleAnimation(targetZoom, duration) { EasingFunction = easing };
            ZoomTransformElement.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            ZoomTransformElement.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private ScrollViewer MindMapScrollHost => _mindMapScroll ?? (_mindMapScroll = FindElement<ScrollViewer>("MindMapScroll"));
        private Canvas MindMapCanvasElement => _mindMapCanvas ?? (_mindMapCanvas = FindElement<Canvas>("MindMapCanvas"));
        private ScaleTransform ZoomTransformElement => _zoomTransform ?? (_zoomTransform = FindElement<ScaleTransform>("ZoomTransform"));
        private TranslateTransform PanTransformElement => _panTransform ?? (_panTransform = FindElement<TranslateTransform>("PanTransform"));

        private void ResolveTemplateReferences()
        {
            _mindMapScroll = FindElement<ScrollViewer>("MindMapScroll");
            _mindMapCanvas = FindElement<Canvas>("MindMapCanvas");
            _zoomTransform = FindElement<ScaleTransform>("ZoomTransform");
            _panTransform = FindElement<TranslateTransform>("PanTransform");
        }

        private T FindElement<T>(string name) where T : class
        {
            var element = FindName(name) as T;
            if (element == null)
                throw new InvalidOperationException(string.Format("DocumentAiView.xaml 中未找到名为 {0} 的元素。", name));
            return element;
        }

    }

    public enum DocumentAiViewMode
    {
        None = 0,
        Mindmap = 1,
        Checklist = 2
    }
}

