using System;
using System.Windows;
using System.Windows.Input;
using EW_Assistant.Domain.Inventory;
using EW_Assistant.Services;

namespace EW_Assistant.Views.Inventory
{
    /// <summary>
    /// 新增备件的多字段输入窗口。
    /// </summary>
    public partial class AddPartDialog : Window
    {
        public SparePart Result { get; private set; }

        public AddPartDialog()
        {
            InitializeComponent();
            CurrentStockBox.Text = "0";
            UnitBox.Text = "PCS";
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
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show(L("名称不能为空。"), L("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int safe = 0;
            int current = ParseIntOrZero(CurrentStockBox.Text);

            Result = new SparePart
            {
                Name = NameBox.Text.Trim(),
                Spec = SpecBox.Text ?? string.Empty,
                Unit = string.IsNullOrWhiteSpace(UnitBox.Text) ? "PCS" : UnitBox.Text.Trim(),
                Location = LocationBox.Text ?? string.Empty,
                SafeStock = safe,
                CurrentStock = current,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private int ParseIntOrZero(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

        public static SparePart ShowDialog(Window owner)
        {
            var dlg = new AddPartDialog
            {
                Owner = owner
            };
            var ok = dlg.ShowDialog();
            return ok == true ? dlg.Result : null;
        }
    }
}
