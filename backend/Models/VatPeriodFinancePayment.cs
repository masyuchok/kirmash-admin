namespace backend.Models;

public class VatPeriodFinancePayment
{
    public int Id { get; set; }
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public int FinanceMovementId { get; set; }
    public FinanceMovement FinanceMovement { get; set; } = default!;
    /// <summary>When true, auto VAT sync will not overwrite the movement amount.</summary>
    public bool IsAmountLocked { get; set; }
}
