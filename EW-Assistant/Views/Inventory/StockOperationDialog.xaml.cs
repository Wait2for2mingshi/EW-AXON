using System.Collections.Generic;
using System;
using System.Windows;
using System.Windows.Input;
using EW_Assistant.Services;

namespace EW_Assistant.Views.Inventory
{
    public class StockOperationResult
    {
        public int Quantity { get; set; }
        public int NewQuantity { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// 库存操作窗口（入库/出库/调整），一次性填写所有字段。
    /// </summary>
    public partial class StockOperationDialog : Window
    {
        private readonly string _mode;

        public StockOperationResult Result { get; private set; }

        public StockOperationDialog(string mode, string title, string subtitle, int defaultQty = 0, int defaultNewQty = 0)
        {
            InitializeComponent();
            _mode = mode ?? "Stock";

            TitleText.Text = string.IsNullOrWhiteSpace(title) ? L("库存操作") : L(title);
            SubtitleText.Text = subtitle ?? string.Empty;

            SetupReasonOptions(_mode);
            QtyBox.Text = defaultQty > 0 ? defaultQty.ToString() : string.Empty;
            NewQtyBox.Text = defaultNewQty > 0 ? defaultNewQty.ToString() : string.Empty;
            ReasonBox.Text = GetDefaultReason(_mode);

            // 调整模式显示“调整后库存”，入/出库隐藏
            var isAdjust = string.Equals(_mode, "Adjust", StringComparison.OrdinalIgnoreCase);
            NewQtyLabel.Visibility = isAdjust ? Visibility.Visible : Visibility.Collapsed;
            NewQtyBox.Visibility = isAdjust ? Visibility.Visible : Visibility.Collapsed;
            QtyLabel.Text = isAdjust ? L("变更数量") : L("数量");
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            try { DragMove(); } catch { }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseInt(QtyBox.Text, out var qty) && _mode != "Adjust")
            {
                MessageBox.Show(L("请输入有效数量。"), L("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var isAdjust = string.Equals(_mode, "Adjust", StringComparison.OrdinalIgnoreCase);
            int newQty = 0;
            if (isAdjust && !TryParseInt(NewQtyBox.Text, out newQty))
            {
                MessageBox.Show(L("请输入有效的调整后库存。"), L("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new StockOperationResult
            {
                Quantity = qty,
                NewQuantity = newQty,
                Reason = ReasonBox.Text ?? string.Empty
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private bool TryParseInt(string text, out int value)
        {
            return int.TryParse(text, out value);
        }

        private string GetDefaultReason(string mode)
        {
            if (string.Equals(mode, "In", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "StockIn", StringComparison.OrdinalIgnoreCase))
            {
                return L("采购入库");
            }

            if (string.Equals(mode, "Out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "StockOut", StringComparison.OrdinalIgnoreCase))
            {
                return L("维修领料");
            }

            if (string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase))
            {
                return L("库存调整");
            }

            return string.Empty;
        }

        private void SetupReasonOptions(string mode)
        {
            ReasonBox.Items.Clear();
            var options = new List<string>();

            if (string.Equals(mode, "In", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "StockIn", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(L("采购入库"));
                options.Add(L("退料入库"));
            }
            else if (string.Equals(mode, "Out", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "StockOut", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(L("维修领料"));
                options.Add(L("生产领料"));
                options.Add(L("报废出库"));
            }
            else if (string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(L("库存调整"));
            }

            foreach (var opt in options)
            {
                ReasonBox.Items.Add(opt);
            }
        }

        public static StockOperationResult Show(Window owner, string mode, string title, string subtitle, int defaultQty = 0, int defaultNewQty = 0)
        {
            var dlg = new StockOperationDialog(mode, title, subtitle, defaultQty, defaultNewQty)
            {
                Owner = owner
            };
            var ok = dlg.ShowDialog();
            return ok == true ? dlg.Result : null;
        }
    }
}
