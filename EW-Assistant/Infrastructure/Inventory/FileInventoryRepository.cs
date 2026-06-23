using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Domain.Inventory;
using Newtonsoft.Json;

namespace EW_Assistant.Infrastructure.Inventory
{
    /// <summary>
    /// 基于本地 JSON 文件的库存仓储实现，使用同步 IO + 异步包装，确保线程安全。
    /// </summary>
    public class FileInventoryRepository : IInventoryRepository
    {
        private readonly string _dataFolder;
        private readonly string _partsFilePath;
        private readonly string _transactionsFilePath;
        private readonly SemaphoreSlim _syncRoot = new SemaphoreSlim(1, 1);
        private bool _initialized;

        public FileInventoryRepository(string dataFolder)
        {
            if (string.IsNullOrWhiteSpace(dataFolder))
            {
                throw new ArgumentException("dataFolder 不能为空", "dataFolder");
            }

            _dataFolder = dataFolder;
            _partsFilePath = Path.Combine(_dataFolder, "inventory_spareparts.json");
            _transactionsFilePath = Path.Combine(_dataFolder, "inventory_transactions.json");

            Directory.CreateDirectory(_dataFolder);
        }

        public async Task<List<SparePart>> GetAllPartsAsync()
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                return await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    return parts.OrderBy(p => p.Name).ToList();
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task<SparePart> GetPartByIdAsync(int id)
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                return await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    return parts.FirstOrDefault(p => p.Id == id);
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task<SparePart> AddPartAsync(SparePart part)
        {
            if (part == null)
            {
                throw new ArgumentNullException("part");
            }

            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                return await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    var nextId = parts.Count == 0 ? 1 : parts.Max(p => p.Id) + 1;
                    var now = DateTime.Now;

                    part.Id = nextId;
                    part.CreatedAt = now;
                    part.UpdatedAt = now;

                    parts.Add(part);
                    SavePartsLocked(parts);

                    return part;
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task UpdatePartAsync(SparePart part)
        {
            if (part == null)
            {
                throw new ArgumentNullException("part");
            }

            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    var existing = parts.FirstOrDefault(p => p.Id == part.Id);
                    if (existing == null)
                    {
                        throw new InvalidOperationException("指定的备件不存在，无法更新。");
                    }

                    existing.Name = part.Name;
                    existing.Spec = part.Spec;
                    existing.Unit = part.Unit;
                    existing.Location = part.Location;
                    existing.SafeStock = part.SafeStock;
                    existing.CurrentStock = part.CurrentStock;
                    existing.UpdatedAt = DateTime.Now;

                    SavePartsLocked(parts);
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task DeletePartAsync(int id)
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    var removed = parts.RemoveAll(p => p.Id == id);
                    if (removed > 0)
                    {
                        SavePartsLocked(parts);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        public async Task StockInAsync(int partId, int qty, string reason, string refNo, string operatorName)
        {
            await ChangeStockAsync(partId, qty, "StockIn", reason, refNo, operatorName, string.Empty).ConfigureAwait(false);
        }

        public async Task StockOutAsync(int partId, int qty, string reason, string refNo, string operatorName)
        {
            await ChangeStockAsync(partId, -qty, "StockOut", reason, refNo, operatorName, string.Empty).ConfigureAwait(false);
        }

        public async Task AdjustStockAsync(int partId, int newQty, string reason, string operatorName)
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    var transactions = LoadTransactionsLocked();
                    var part = parts.FirstOrDefault(p => p.Id == partId);
                    if (part == null)
                    {
                        throw new InvalidOperationException("指定的备件不存在，无法调整库存。");
                    }

                    var delta = newQty - part.CurrentStock;
                    part.CurrentStock = newQty;
                    part.UpdatedAt = DateTime.Now;

                    var transaction = new StockTransaction
                    {
                        Id = Guid.NewGuid(),
                        PartId = partId,
                        Type = "Adjust",
                        QtyChange = delta,
                        AfterQty = part.CurrentStock,
                        Reason = reason,
                        RefNo = string.Empty,
                        Operator = operatorName,
                        RelatedDevice = string.Empty,
                        CreatedAt = DateTime.Now
                    };

                    transactions.Add(transaction);

                    SavePartsLocked(parts);
                    SaveTransactionsLocked(transactions);
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        private async Task ChangeStockAsync(int partId, int qtyChange, string type, string reason, string refNo, string operatorName, string relatedDevice)
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();

                await Task.Run(() =>
                {
                    var parts = LoadPartsLocked();
                    var transactions = LoadTransactionsLocked();
                    var part = parts.FirstOrDefault(p => p.Id == partId);
                    if (part == null)
                    {
                        throw new InvalidOperationException("指定的备件不存在，无法变更库存。");
                    }

                    part.CurrentStock += qtyChange;
                    part.UpdatedAt = DateTime.Now;

                    var transaction = new StockTransaction
                    {
                        Id = Guid.NewGuid(),
                        PartId = partId,
                        Type = type,
                        QtyChange = qtyChange,
                        AfterQty = part.CurrentStock,
                        Reason = reason,
                        RefNo = refNo,
                        Operator = operatorName,
                        RelatedDevice = relatedDevice ?? string.Empty,
                        CreatedAt = DateTime.Now
                    };

                    transactions.Add(transaction);

                    SavePartsLocked(parts);
                    SaveTransactionsLocked(transactions);
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }

        private void EnsureFilesExistLocked()
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_dataFolder);

            if (!File.Exists(_partsFilePath))
            {
                SavePartsLocked(new List<SparePart>());
            }

            if (!File.Exists(_transactionsFilePath))
            {
                SaveTransactionsLocked(new List<StockTransaction>());
            }

            _initialized = true;
        }

        private List<SparePart> LoadPartsLocked()
        {
            var content = File.ReadAllText(_partsFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new List<SparePart>();
            }

            var data = JsonConvert.DeserializeObject<List<SparePart>>(content);
            return data ?? new List<SparePart>();
        }

        private List<StockTransaction> LoadTransactionsLocked()
        {
            var content = File.ReadAllText(_transactionsFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new List<StockTransaction>();
            }

            var data = JsonConvert.DeserializeObject<List<StockTransaction>>(content);
            return data ?? new List<StockTransaction>();
        }

        private void SavePartsLocked(List<SparePart> parts)
        {
            var json = JsonConvert.SerializeObject(parts, Formatting.Indented);
            File.WriteAllText(_partsFilePath, json, new UTF8Encoding(false));
        }

        private void SaveTransactionsLocked(List<StockTransaction> transactions)
        {
            var json = JsonConvert.SerializeObject(transactions, Formatting.Indented);
            File.WriteAllText(_transactionsFilePath, json, new UTF8Encoding(false));
        }

        public async Task<List<StockTransaction>> GetTransactionsAsync()
        {
            await _syncRoot.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureFilesExistLocked();
                return await Task.Run(() =>
                {
                    var list = LoadTransactionsLocked();
                    return list.OrderByDescending(t => t.CreatedAt).ToList();
                }).ConfigureAwait(false);
            }
            finally
            {
                _syncRoot.Release();
            }
        }
    }
}
