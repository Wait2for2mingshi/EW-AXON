using System;
using System.IO;
using EW_Assistant.Domain.Inventory;
using EW_Assistant.ViewModels.Inventory;

namespace EW_Assistant.Infrastructure.Inventory
{
    /// <summary>
    /// 简单的库存模块定位器，统一创建仓储与视图模型。
    /// </summary>
    public static class InventoryModule
    {
        private static readonly object _syncRoot = new object();
        private static bool _initialized;
        private static IInventoryRepository _repository;
        private static InventoryViewModel _viewModel;

        /// <summary>
        /// 库存数据存放目录（默认 D:\DataAI）。
        /// </summary>
        public static string DataFolder { get; private set; }

        /// <summary>
        /// 确保仓储与 VM 已初始化。未来可以在此替换为 DbInventoryRepository，而无需修改 ViewModel/View。
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                DataFolder = @"D:\DataAI";
                Directory.CreateDirectory(DataFolder);

                // 使用工厂创建仓储，后续切换 Web API/数据库时只需修改 App.config 的 InventoryRepositoryMode。
                _repository = InventoryRepositoryFactory.Create();
                _viewModel = new InventoryViewModel(_repository);

                _initialized = true;
            }
        }

        public static IInventoryRepository Repository
        {
            get
            {
                EnsureInitialized();
                return _repository;
            }
        }

        public static InventoryViewModel ViewModel
        {
            get
            {
                EnsureInitialized();
                return _viewModel;
            }
        }
    }
}
