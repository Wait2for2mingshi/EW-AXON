using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EW_Assistant.Domain.Inventory;
using EW_Assistant.Services;
using EW_Assistant.ViewModels;
using EW_Assistant.Views.Inventory;

namespace EW_Assistant.ViewModels.Inventory
{
    /// <summary>
    /// 库存管理视图模型，提供基本的备件 CRUD 与库存操作。
    /// </summary>
    public class InventoryViewModel : ViewModelBase
    {
        private readonly IInventoryRepository _repository;
        private readonly RelayCommand _editCommand;
        private readonly RelayCommand _deleteCommand;
        private readonly RelayCommand _stockInCommand;
        private readonly RelayCommand _stockOutCommand;
        private readonly RelayCommand _adjustCommand;

        private ObservableCollection<SparePart> _parts = new ObservableCollection<SparePart>();
        private ObservableCollection<StockTransactionView> _transactions = new ObservableCollection<StockTransactionView>();
        private SparePart _selectedPart;
        private bool _isRefreshing;

        public InventoryViewModel(IInventoryRepository repository)
        {
            if (repository == null)
            {
                throw new ArgumentNullException("repository");
            }

            _repository = repository;

            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            AddPartCommand = new RelayCommand(async _ => await AddPartAsync());
            _editCommand = new RelayCommand(async _ => await EditPartAsync(), _ => SelectedPart != null);
            _deleteCommand = new RelayCommand(async _ => await DeletePartAsync(), _ => SelectedPart != null);
            _stockInCommand = new RelayCommand(async _ => await StockInAsync(), _ => SelectedPart != null);
            _stockOutCommand = new RelayCommand(async _ => await StockOutAsync(), _ => SelectedPart != null);
            _adjustCommand = new RelayCommand(async _ => await AdjustStockAsync(), _ => SelectedPart != null);

            EditPartCommand = _editCommand;
            DeletePartCommand = _deleteCommand;
            StockInCommand = _stockInCommand;
            StockOutCommand = _stockOutCommand;
            AdjustStockCommand = _adjustCommand;

            // 初次加载
            var _ = RefreshAsync();
        }

        public ObservableCollection<SparePart> Parts
        {
            get { return _parts; }
            private set { SetProperty(ref _parts, value); }
        }

        public SparePart SelectedPart
        {
            get { return _selectedPart; }
            set
            {
                if (SetProperty(ref _selectedPart, value))
                {
                    UpdateCommandStates();
                }
            }
        }

        public ICommand RefreshCommand { get; private set; }

        public ICommand AddPartCommand { get; private set; }

        public ICommand EditPartCommand { get; private set; }

        public ICommand DeletePartCommand { get; private set; }

        public ICommand StockInCommand { get; private set; }

        public ICommand StockOutCommand { get; private set; }

        public ICommand AdjustStockCommand { get; private set; }

        public ObservableCollection<StockTransactionView> Transactions
        {
            get { return _transactions; }
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        /// <summary>
        /// 刷新并应用搜索过滤。
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            try
            {
                var list = await _repository.GetAllPartsAsync();
                UpdateParts(list);
                await LoadTransactionsAsync(list);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("刷新库存失败：") + ex.Message, L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void UpdateParts(IEnumerable<SparePart> items)
        {
            Parts.Clear();
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                Parts.Add(item);
            }
        }

        private async Task LoadTransactionsAsync(IList<SparePart> parts)
        {
            Transactions.Clear();
            var partLookup = (parts ?? new List<SparePart>()).ToDictionary(p => p.Id, p => p);
            try
            {
                var records = await _repository.GetTransactionsAsync();
                if (records == null) return;

            foreach (var t in records)
            {
                SparePart p;
                partLookup.TryGetValue(t.PartId, out p);
                Transactions.Add(new StockTransactionView
                {
                    PartName = p != null ? p.Name : $"ID:{t.PartId}",
                    Type = ToDisplayType(t.Type),
                    QtyChange = t.QtyChange,
                    AfterQty = t.AfterQty,
                    Reason = t.Reason,
                    CreatedAt = t.CreatedAt
                });
            }
            }
            catch
            {
                // 忽略历史加载失败，不影响主流程
            }
        }

        private async Task AddPartAsync()
        {
            var part = AddPartDialog.ShowDialog(Application.Current != null ? Application.Current.MainWindow : null);
            if (part == null)
            {
                return;
            }

            try
            {
                if (part.SafeStock <= 0)
                {
                    part.SafeStock = await CalculateSuggestedSafeStockAsync(part.Name);
                }
                await _repository.AddPartAsync(part);
                await RefreshAsync();
                MessageBox.Show(L("新增备件成功。"), L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("新增失败：") + ex.Message, L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task EditPartAsync()
        {
            if (SelectedPart == null)
            {
                return;
            }
            var updated = EditPartDialog.ShowDialog(Application.Current != null ? Application.Current.MainWindow : null, SelectedPart);
            if (updated == null)
            {
                return;
            }
            updated.Id = SelectedPart.Id;
            updated.CreatedAt = SelectedPart.CreatedAt;
            updated.UpdatedAt = SelectedPart.UpdatedAt;

            try
            {
                await _repository.UpdatePartAsync(updated);
                await RefreshAsync();
                MessageBox.Show(L("更新成功。"), L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("更新失败：") + ex.Message, L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeletePartAsync()
        {
            if (SelectedPart == null)
            {
                return;
            }

            var result = MessageBox.Show(L("确定删除该备件吗？"), L("确认删除"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _repository.DeletePartAsync(SelectedPart.Id);
                await RefreshAsync();
                MessageBox.Show(L("删除成功。"), L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("删除失败：") + ex.Message, L("库存管理"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StockInAsync()
        {
            if (SelectedPart == null)
            {
                return;
            }

            var result = StockOperationDialog.Show(
                Application.Current != null ? Application.Current.MainWindow : null,
                "In",
                "入库",
                SelectedPart.Name,
                1);
            if (result == null)
            {
                return;
            }
            if (result.Quantity <= 0)
            {
                MessageBox.Show(L("数量必须大于 0。"), L("入库"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var reason = string.IsNullOrWhiteSpace(result.Reason) ? L("入库") : result.Reason;
            var refNo = string.Empty;
            var operatorName = string.Empty;

            try
            {
                await _repository.StockInAsync(SelectedPart.Id, result.Quantity, reason, refNo, operatorName);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("入库失败：") + ex.Message, L("入库"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StockOutAsync()
        {
            if (SelectedPart == null)
            {
                return;
            }

            var result = StockOperationDialog.Show(
                Application.Current != null ? Application.Current.MainWindow : null,
                "Out",
                "出库",
                SelectedPart.Name,
                1);
            if (result == null)
            {
                return;
            }
            if (result.Quantity <= 0)
            {
                MessageBox.Show(L("数量必须大于 0。"), L("出库"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var reason = string.IsNullOrWhiteSpace(result.Reason) ? L("出库") : result.Reason;
            var refNo = string.Empty;
            var operatorName = string.Empty;

            try
            {
                await _repository.StockOutAsync(SelectedPart.Id, result.Quantity, reason, refNo, operatorName);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("出库失败：") + ex.Message, L("出库"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task AdjustStockAsync()
        {
            if (SelectedPart == null)
            {
                return;
            }

            var result = StockOperationDialog.Show(
                Application.Current != null ? Application.Current.MainWindow : null,
                "Adjust",
                "调整库存",
                SelectedPart.Name,
                SelectedPart.CurrentStock,
                SelectedPart.CurrentStock);
            if (result == null)
            {
                return;
            }
            var newQty = result.NewQuantity;
            var reason = string.IsNullOrWhiteSpace(result.Reason) ? L("库存调整") : result.Reason;
            var operatorName = string.Empty;

            try
            {
                await _repository.AdjustStockAsync(SelectedPart.Id, newQty, reason, operatorName);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("调整库存失败：") + ex.Message, L("库存调整"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateCommandStates()
        {
            _editCommand.RaiseCanExecuteChanged();
            _deleteCommand.RaiseCanExecuteChanged();
            _stockInCommand.RaiseCanExecuteChanged();
            _stockOutCommand.RaiseCanExecuteChanged();
            _adjustCommand.RaiseCanExecuteChanged();
        }

        public class StockTransactionView
        {
            public string PartName { get; set; }
            public string Type { get; set; }
            public int QtyChange { get; set; }
            public int AfterQty { get; set; }
            public string Reason { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private string ToDisplayType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return L("其他");
            var key = type.Trim().ToLowerInvariant();
            switch (key)
            {
                case "stockin":
                case "in":
                    return L("入库");
                case "stockout":
                case "out":
                    return L("出库");
                case "adjust":
                    return L("调整");
                default:
                    return type;
            }
        }

        private async Task<int> CalculateSuggestedSafeStockAsync(string partName)
        {
            var now = DateTime.Now;
            var threshold = now.AddDays(-90);
            int partId = -1;
            if (!string.IsNullOrWhiteSpace(partName))
            {
                var match = Parts.FirstOrDefault(p => string.Equals(p.Name, partName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    partId = match.Id;
                }
            }

            try
            {
                var records = await _repository.GetTransactionsAsync();
                if (records == null || records.Count == 0)
                {
                    return 2;
                }

                var query = records.Where(r => r.CreatedAt >= threshold);
                if (partId > 0)
                {
                    query = query.Where(r => r.PartId == partId);
                }

                var totalOut = query.Where(r => r.QtyChange < 0).Sum(r => -r.QtyChange);
                if (totalOut <= 0)
                {
                    return 2;
                }

                decimal avgDaily = totalOut / 90m;
                decimal coverDays = 30m;
                var initial = (int)Math.Ceiling(avgDaily * coverDays);
                if (initial < 2) initial = 2;
                if (initial > 500) initial = 500;
                return initial;
            }
            catch
            {
                return 2;
            }
        }
    }
}
