using System;

namespace EW_Assistant.Domain.Inventory
{
    public class StockTransaction
    {
        public Guid Id { get; set; }

        public int PartId { get; set; }

        public string Type { get; set; }

        public int QtyChange { get; set; }

        public int AfterQty { get; set; }

        public string Reason { get; set; }

        public string RefNo { get; set; }

        public string Operator { get; set; }

        public string RelatedDevice { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
