using System;
using System.Configuration;
using System.IO;
using EW_Assistant.Domain.Inventory;

namespace EW_Assistant.Infrastructure.Inventory
{
    /// <summary>
    /// 库存仓储创建工厂，可根据配置切换实现。
    /// </summary>
    public static class InventoryRepositoryFactory
    {
        private const string DefaultFolder = @"D:\DataAI";

        public static IInventoryRepository Create()
        {
            var mode = ConfigurationManager.AppSettings["InventoryRepositoryMode"];
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "File";
            }

            if (string.Equals(mode, "Api", StringComparison.OrdinalIgnoreCase))
            {
                // 占位：后续可实现 ApiInventoryRepository，当前抛出占位异常。
                throw new NotImplementedException("ApiInventoryRepository 尚未实现。");
            }

            // 默认文件模式
            Directory.CreateDirectory(DefaultFolder);
            return new FileInventoryRepository(DefaultFolder);
        }
    }
}
