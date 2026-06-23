using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace EW_Assistant.Component.MindMap
{
    /// <summary>
    /// 思维导图的基础节点模型，包含标题、备注、展开状态等。
    /// </summary>
    public sealed class MindMapNode : INotifyPropertyChanged
    {
        private string _title;
        private string _body = string.Empty;
        private bool _isExpanded = true;
        private bool _isSelected;

        public MindMapNode(string title)
        {
            _title = title ?? string.Empty;
            Children = new ObservableCollection<MindMapNode>();
            Children.CollectionChanged += HandleChildrenChanged;
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        /// <summary>正文/备注文本</summary>
        public string Body
        {
            get => _body;
            private set
            {
                if (_body == value) return;
                _body = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<MindMapNode> Children { get; }

        public bool HasChildren => Children.Count > 0;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public void AppendBodyText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var trimmed = text.Trim();
            Body = string.IsNullOrEmpty(_body) ? trimmed : $"{_body}{Environment.NewLine}{trimmed}";
        }

        private void HandleChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasChildren));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 思维导图上可视化后的节点（包含坐标、尺寸等），供画布布局和连线使用。
    /// </summary>
    public sealed class MindMapVisualNode : INotifyPropertyChanged
    {
        private double _x;
        private double _y;
        private double _width = 220;
        private double _height = 72;

        public MindMapVisualNode(MindMapNode node, int depth)
        {
            Node = node;
            Depth = depth;
        }

        public MindMapNode Node { get; }

        public int Depth { get; }

        public double X
        {
            get => _x;
            set
            {
                if (Math.Abs(_x - value) < 0.1) return;
                _x = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenterX));
                OnPropertyChanged(nameof(RightX));
                OnPropertyChanged(nameof(LeftX));
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                if (Math.Abs(_y - value) < 0.1) return;
                _y = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenterY));
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                if (Math.Abs(_width - value) < 0.1) return;
                _width = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenterX));
                OnPropertyChanged(nameof(RightX));
                OnPropertyChanged(nameof(LeftX));
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (Math.Abs(_height - value) < 0.1) return;
                _height = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CenterY));
            }
        }

        public double CenterX => _x + _width / 2;
        public double CenterY => _y + _height / 2;
        public double RightX => _x + _width;
        public double LeftX => _x;
        public Rect Bounds => new Rect(_x, _y, _width, _height);

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 用于绑定连线的视图对象，会监听节点坐标/尺寸变化自动刷新。
    /// </summary>
    public sealed class MindMapEdge : INotifyPropertyChanged
    {
        public MindMapEdge(MindMapVisualNode from, MindMapVisualNode to)
        {
            From = from;
            To = to;

            if (From != null)
                From.PropertyChanged += WatchNode;
            if (To != null)
                To.PropertyChanged += WatchNode;
        }

        public MindMapVisualNode From { get; }
        public MindMapVisualNode To { get; }

        public Point StartPoint => new Point(From?.RightX ?? (To.LeftX - 60), (From?.CenterY ?? To.CenterY));
        public Point EndPoint => new Point(To.LeftX, To.CenterY);
        public Point ControlPoint1
        {
            get
            {
                var start = StartPoint;
                var end = EndPoint;
                var dx = (end.X - start.X) * 0.35;
                return new Point(start.X + dx, start.Y);
            }
        }
        public Point ControlPoint2
        {
            get
            {
                var start = StartPoint;
                var end = EndPoint;
                var dx = (end.X - start.X) * 0.65;
                return new Point(start.X + dx, end.Y);
            }
        }

        private void WatchNode(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MindMapVisualNode.X) ||
                e.PropertyName == nameof(MindMapVisualNode.Y) ||
                e.PropertyName == nameof(MindMapVisualNode.Width) ||
                e.PropertyName == nameof(MindMapVisualNode.Height))
            {
                OnPropertyChanged(nameof(StartPoint));
                OnPropertyChanged(nameof(EndPoint));
                OnPropertyChanged(nameof(ControlPoint1));
                OnPropertyChanged(nameof(ControlPoint2));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
