using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EW_Assistant.Component.Checklist
{
    /// <summary>AI Checklist 根对象，包含标题、描述和分组。</summary>
    public sealed class DocumentChecklist : INotifyPropertyChanged
    {
        private string _title;
        private string _description;
        public ObservableCollection<ChecklistGroup> Groups { get; }

        public DocumentChecklist()
        {
            Groups = new ObservableCollection<ChecklistGroup>();
        }

        public string Title
        {
            get => _title;
            set
            {
                if (string.Equals(_title, value, StringComparison.Ordinal)) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                if (string.Equals(_description, value, StringComparison.Ordinal)) return;
                _description = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Checklist 分组，表示某一段流程。</summary>
    public sealed class ChecklistGroup : INotifyPropertyChanged
    {
        private int _order;
        private string _title;
        private string _description;
        private ObservableCollection<ChecklistItem> _items = new ObservableCollection<ChecklistItem>();

        public int Order
        {
            get => _order;
            set
            {
                if (_order == value) return;
                _order = value;
                OnPropertyChanged();
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (string.Equals(_title, value, StringComparison.Ordinal)) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                if (string.Equals(_description, value, StringComparison.Ordinal)) return;
                _description = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<ChecklistItem> Items
        {
            get => _items;
            set
            {
                if (ReferenceEquals(_items, value)) return;
                _items = value ?? new ObservableCollection<ChecklistItem>();
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Checklist 具体步骤。</summary>
    public sealed class ChecklistItem : INotifyPropertyChanged
    {
        private int _order;
        private string _title;
        private string _detail;
        private ChecklistItemStatus _status;
        private string _note;

        public int Order
        {
            get => _order;
            set
            {
                if (_order == value) return;
                _order = value;
                OnPropertyChanged();
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (string.Equals(_title, value, StringComparison.Ordinal)) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public string Detail
        {
            get => _detail;
            set
            {
                if (string.Equals(_detail, value, StringComparison.Ordinal)) return;
                _detail = value;
                OnPropertyChanged();
            }
        }

        public ChecklistItemStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public string Note
        {
            get => _note;
            set
            {
                if (string.Equals(_note, value, StringComparison.Ordinal)) return;
                _note = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Checklist 步骤状态。</summary>
    public enum ChecklistItemStatus
    {
        Pending = 0,
        Done = 1,
        Abnormal = 2
    }

    /// <summary>状态下拉框选项。</summary>
    public sealed class ChecklistStatusOption
    {
        public ChecklistStatusOption(ChecklistItemStatus status, string displayName)
        {
            Status = status;
            DisplayName = displayName;
        }

        public ChecklistItemStatus Status { get; }
        public string DisplayName { get; }
    }

    /// <summary>Checklist 状态工具方法。</summary>
    public static class ChecklistItemStatusHelper
    {
        private static readonly IDictionary<ChecklistItemStatus, string> Names = new Dictionary<ChecklistItemStatus, string>
        {
            { ChecklistItemStatus.Pending, "待执行" },
            { ChecklistItemStatus.Done, "已完成" },
            { ChecklistItemStatus.Abnormal, "异常" }
        };

        public static string GetDisplayName(ChecklistItemStatus status)
        {
            string name;
            return Names.TryGetValue(status, out name) ? name : status.ToString();
        }

        public static IEnumerable<ChecklistStatusOption> GetOptions()
        {
            return new[]
            {
                new ChecklistStatusOption(ChecklistItemStatus.Pending, GetDisplayName(ChecklistItemStatus.Pending)),
                new ChecklistStatusOption(ChecklistItemStatus.Done, GetDisplayName(ChecklistItemStatus.Done)),
                new ChecklistStatusOption(ChecklistItemStatus.Abnormal, GetDisplayName(ChecklistItemStatus.Abnormal))
            };
        }

        public static ChecklistItemStatus Parse(string text, ChecklistItemStatus fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            var normalized = text.Trim().ToLowerInvariant();
            if (normalized == "pending" || normalized == "todo" || normalized == "待执行")
                return ChecklistItemStatus.Pending;
            if (normalized == "done" || normalized == "完成" || normalized == "已完成")
                return ChecklistItemStatus.Done;
            if (normalized == "abnormal" || normalized == "异常" || normalized == "issue" || normalized == "ng")
                return ChecklistItemStatus.Abnormal;

            ChecklistItemStatus parsed;
            return Enum.TryParse(normalized, true, out parsed) ? parsed : fallback;
        }
    }
}
