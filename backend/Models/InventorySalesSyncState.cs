namespace backend.Models
{
    public class InventorySalesSyncState
    {
        public int Id { get; set; }
        public bool FullSyncCompleted { get; set; }
        public DateTime? LastSyncedThroughUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
