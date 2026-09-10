namespace backend.Models;

public static class KirmaBukinistkaReceiptDraftStatuses
{
    public const string Open = "Open";
    public const string Failed = "Failed";
    public const string Completed = "Completed";
}

public class KirmaBukinistkaReceiptDraft
{
    public int Id { get; set; }
    public string Status { get; set; } = KirmaBukinistkaReceiptDraftStatuses.Open;
    public string CreatedByLogin { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? LastError { get; set; }
    public int? OdooPickingId { get; set; }
    public string? OdooPickingName { get; set; }

    public List<KirmaBukinistkaReceiptDraftLine> Lines { get; set; } = new();
}

public class KirmaBukinistkaReceiptDraftLine
{
    public int Id { get; set; }
    public int ReceiptDraftId { get; set; }
    public KirmaBukinistkaReceiptDraft ReceiptDraft { get; set; } = null!;
    public int OfferId { get; set; }
    public int OdooProductId { get; set; }
    public string OdooProductName { get; set; } = string.Empty;
    public decimal? ListPrice { get; set; }
    public bool? ApplyKirmaCostPrice { get; set; }
}

public class KirmaBukinistkaReceiptDraftLineRequest
{
    public int OfferId { get; set; }
    public int OdooProductId { get; set; }
    public string? OdooProductName { get; set; }
    public decimal? ListPrice { get; set; }
    public bool? ApplyKirmaCostPrice { get; set; }
}

public class KirmaBukinistkaReceiptDraftUpsertRequest
{
    public List<KirmaBukinistkaReceiptDraftLineRequest> Lines { get; set; } = new();
}

public class KirmaBukinistkaReceiptDraftLineDto
{
    public int OfferId { get; set; }
    public int OdooProductId { get; set; }
    public string OdooProductName { get; set; } = string.Empty;
    public decimal? ListPrice { get; set; }
    public bool? ApplyKirmaCostPrice { get; set; }
}

public class KirmaBukinistkaReceiptDraftDto
{
    public int Id { get; set; }
    public string Status { get; set; } = KirmaBukinistkaReceiptDraftStatuses.Open;
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<KirmaBukinistkaReceiptDraftLineDto> Lines { get; set; } = new();
}
