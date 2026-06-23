using System;

namespace EW_Assistant.Domain.Inventory
{
    public class SparePart
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string Spec { get; set; }

        public string Unit { get; set; }

        public string Location { get; set; }

        public int SafeStock { get; set; }

        public int CurrentStock { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
