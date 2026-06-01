namespace backend.Models
{
    public class FinanceRecurringApplication
    {
        public int Id { get; set; }
        public int RecurringExpenseId { get; set; }
        public FinanceRecurringExpense RecurringExpense { get; set; } = default!;
        public int Year { get; set; }
        public int Month { get; set; }
        public int MovementId { get; set; }
        public FinanceMovement Movement { get; set; } = default!;
        public DateTime AppliedAtUtc { get; set; }
    }
}
