using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EW_Assistant.Domain.Inventory;

namespace EW_Assistant.Infrastructure.Inventory
{
    /// <summary>
    /// 访问 .NET 8 Web API 的库存仓储实现占位，后续替换 HttpClient 调用即可，不影响 View/ViewModel。
    /// </summary>
    public class ApiInventoryRepository : IInventoryRepository
    {
        private readonly string _baseUrl;

        /// <summary>
        /// 构造函数，预留 Web API 根地址（例如 http://192.168.200.21:6001）。
        /// </summary>
        /// <param name="baseUrl">API 根地址</param>
        public ApiInventoryRepository(string baseUrl)
        {
            _baseUrl = baseUrl;
        }

        public Task<List<SparePart>> GetAllPartsAsync()
        {
            throw new NotImplementedException();
        }

        public Task<SparePart> GetPartByIdAsync(int id)
        {
            throw new NotImplementedException();
        }

        public Task<SparePart> AddPartAsync(SparePart part)
        {
            throw new NotImplementedException();
        }

        public Task UpdatePartAsync(SparePart part)
        {
            throw new NotImplementedException();
        }

        public Task DeletePartAsync(int id)
        {
            throw new NotImplementedException();
        }

        public Task StockInAsync(int partId, int qty, string reason, string refNo, string operatorName)
        {
            throw new NotImplementedException();
        }

        public Task StockOutAsync(int partId, int qty, string reason, string refNo, string operatorName)
        {
            throw new NotImplementedException();
        }

        public Task AdjustStockAsync(int partId, int newQty, string reason, string operatorName)
        {
            throw new NotImplementedException();
        }

        public Task<List<StockTransaction>> GetTransactionsAsync()
        {
            throw new NotImplementedException();
        }
    }
}
