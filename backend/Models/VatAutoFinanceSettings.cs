namespace backend.Models;

public class VatAutoFinanceSettings
{
    public int Id { get; set; } = 1;
    public bool IsEnabled { get; set; }
    public int? FinancePersonId { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
