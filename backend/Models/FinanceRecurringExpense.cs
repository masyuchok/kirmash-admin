namespace backend.Models
{
    public class FinanceRecurringExpense
    {
        public int Id { get; set; }
        public int PersonId { get; set; }
        public FinancePerson Person { get; set; } = default!;
        public FinanceMovementKind Kind { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public int DayOfMonth { get; set; } = 1;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAtUtc { get; set; }

        public List<FinanceRecurringApplication> Applications { get; set; } = new();
        public List<FinanceMovement> GeneratedMovements { get; set; } = new();
    }
}
