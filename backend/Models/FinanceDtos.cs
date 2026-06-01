namespace backend.Models
{
    public class FinancePersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
    }

    public class FinancePersonCreateRequest
    {
        public string? Name { get; set; }
    }

    public class FinancePersonUpdateRequest
    {
        public string? Name { get; set; }
    }

    public class FinanceMovementDto
    {
        public int Id { get; set; }
        public int PersonId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string MovementDate { get; set; } = string.Empty;
        public bool IsFromRecurring { get; set; }
        public int? RecurringExpenseId { get; set; }
    }

    public class FinanceMovementCreateRequest
    {
        public int PersonId { get; set; }
        public string? Kind { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public string? MovementDate { get; set; }
    }

    public class FinanceMovementUpdateRequest
    {
        public string? Kind { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public string? MovementDate { get; set; }
    }

    public class FinanceRecurringExpenseDto
    {
        public int Id { get; set; }
        public int PersonId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public int DayOfMonth { get; set; }
        public bool IsActive { get; set; }
    }

    public class FinanceRecurringExpenseCreateRequest
    {
        public int PersonId { get; set; }
        public string? Kind { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public int DayOfMonth { get; set; } = 1;
    }

    public class FinanceRecurringExpenseUpdateRequest
    {
        public string? Kind { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public int DayOfMonth { get; set; } = 1;
        public bool IsActive { get; set; } = true;
    }

    public class FinanceSummaryDto
    {
        public decimal TotalOutgoingTransfer { get; set; }
        public decimal TotalIncomingTransfer { get; set; }
        public decimal TotalPayment { get; set; }
        public decimal TotalKirmaPayout { get; set; }
        /// <summary>Calculated: person owes Kirma (not manual).</summary>
        public decimal PersonOwesKirma { get; set; }
        /// <summary>Calculated: Kirma owes the person (not manual).</summary>
        public decimal KirmaOwesPerson { get; set; }
    }

    public class FinancePersonOverviewDto
    {
        public FinancePersonDto Person { get; set; } = new();
        public FinanceSummaryDto Summary { get; set; } = new();
        public List<FinanceMovementDto> Movements { get; set; } = new();
        public List<FinanceRecurringExpenseDto> RecurringExpenses { get; set; } = new();
    }
}
