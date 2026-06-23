using System;
using System.Windows;
using System.Windows.Input;
using EW_Assistant.Domain.Inventory;
using EW_Assistant.Services;

namespace EW_Assistant.Views.Inventory
{
    /// <summary>
    /// 编辑备件信息窗口。
    /// </summary>
    public partial class EditPartDialog : Window
    {
        public SparePart Result { get; private set; }

        public EditPartDialog(SparePart source)
        {
            InitializeComponent();

            if (source != null)
            {
                NameBox.Text = source.Name;
                SpecBox.Text = source.Spec;
                LocationBox.Text = source.Location;
                UnitBox.Text = string.IsNullOrWhiteSpace(source.Unit) ? "PCS" : source.Unit;
                CurrentStockBox.Text = source.CurrentStock.ToString();
                SafeStockBox.Text = source.SafeStock.ToString();
            }
            else
            {
                UnitBox.Text = "PCS";
            }
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

            Result = new SparePart
            {
                Name = NameBox.Text.Trim(),
                Spec = SpecBox.Text ?? string.Empty,
                Location = LocationBox.Text ?? string.Empty,
                Unit = string.IsNullOrWhiteSpace(UnitBox.Text) ? "PCS" : UnitBox.Text.Trim(),
                CurrentStock = ParseIntOrZero(CurrentStockBox.Text),
                SafeStock = ParseIntOrZero(SafeStockBox.Text)
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

        public static SparePart ShowDialog(Window owner, SparePart source)
        {
            var dlg = new EditPartDialog(source)
            {
                Owner = owner
            };
            var ok = dlg.ShowDialog();
            return ok == true ? dlg.Result : null;
        }
    }
}
