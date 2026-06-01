namespace backend.Models
{
    public class FinancePerson
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public List<FinanceMovement> Movements { get; set; } = new();
        public List<FinanceRecurringExpense> RecurringExpenses { get; set; } = new();
    }
}
