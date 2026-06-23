using System.Windows.Controls;
using EW_Assistant.Infrastructure.Inventory;

namespace EW_Assistant.Views.Inventory
{
    /// <summary>
    /// 简单的库存管理视图，绑定到 InventoryViewModel。
    /// </summary>
    public partial class InventoryView : UserControl
    {
        public InventoryView()
        {
            InitializeComponent();

            // 通过模块统一获取 VM，未来可在模块内替换为其他仓储实现而不改动视图。
            DataContext = InventoryModule.ViewModel;
        }
    }
}
