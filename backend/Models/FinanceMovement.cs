namespace backend.Models
{
    public class FinanceMovement
    {
        public int Id { get; set; }
        public int PersonId { get; set; }
        public FinancePerson Person { get; set; } = default!;
        public FinanceMovementKind Kind { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateOnly MovementDate { get; set; }
        public bool IsFromRecurring { get; set; }
        public int? RecurringExpenseId { get; set; }
        public FinanceRecurringExpense? RecurringExpense { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
