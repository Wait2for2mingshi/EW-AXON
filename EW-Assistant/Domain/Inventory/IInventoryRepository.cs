using System.Collections.Generic;
using System.Threading.Tasks;

namespace EW_Assistant.Domain.Inventory
{
    public interface IInventoryRepository
    {
        Task<List<SparePart>> GetAllPartsAsync();

        Task<SparePart> GetPartByIdAsync(int id);

        Task<SparePart> AddPartAsync(SparePart part);

        Task UpdatePartAsync(SparePart part);

        Task DeletePartAsync(int id);

        Task StockInAsync(int partId, int qty, string reason, string refNo, string operatorName);

        Task StockOutAsync(int partId, int qty, string reason, string refNo, string operatorName);

        Task AdjustStockAsync(int partId, int newQty, string reason, string operatorName);

        Task<List<StockTransaction>> GetTransactionsAsync();
    }
}
