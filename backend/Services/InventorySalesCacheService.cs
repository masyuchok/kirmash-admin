using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class InventorySalesCacheService
    {
        private const int SyncStateId = 1;
        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours( 1 );
        private static readonly SemaphoreSlim SyncLock = new( 1, 1 );

        private readonly AppDbContext _db;
        private readonly VatReportService _vatReportService;

        public InventorySalesCacheService( AppDbContext db, VatReportService vatReportService )
        {
            _db = db;
            _vatReportService = vatReportService;
        }

        public async Task<DateTime?> EnsureFreshAsync( TimeSpan? maxAge = null, bool force = false )
        {
            TimeSpan ttl = maxAge ?? DefaultMaxAge;
            InventorySalesSyncState state = await GetOrCreateStateAsync();
            if (!force &&
                state.FullSyncCompleted &&
                state.UpdatedAtUtc >= DateTime.UtcNow.Subtract( ttl ))
            {
                return state.LastSyncedThroughUtc ?? state.UpdatedAtUtc;
            }

            await SyncLock.WaitAsync();
            try
            {
                state = await GetOrCreateStateAsync();
                if (!force &&
                    state.FullSyncCompleted &&
                    state.UpdatedAtUtc >= DateTime.UtcNow.Subtract( ttl ))
                {
                    return state.LastSyncedThroughUtc ?? state.UpdatedAtUtc;
                }

                if (!state.FullSyncCompleted || force)
                {
                    await RunFullSyncAsync( state );
                }
                else
                {
                    await RunIncrementalSyncAsync( state );
                }

                return state.LastSyncedThroughUtc ?? state.UpdatedAtUtc;
            }
            finally
            {
                SyncLock.Release();
            }
        }

        public async Task<Dictionary<string, int>> GetSoldByProductAsync()
        {
            List<InventoryProductSale> rows = await _db.InventoryProductSales.AsNoTracking().ToListAsync();
            return rows.ToDictionary(
                x => x.ShopifyProductId,
                x => x.SoldQuantity,
                StringComparer.OrdinalIgnoreCase
            );
        }

        private async Task RunFullSyncAsync( InventorySalesSyncState state )
        {
            Dictionary<string, int> soldByProduct = await _vatReportService.GetSoldQuantitiesByProductFromShopifyAsync();
            await ReplaceSoldCacheAsync( soldByProduct );

            state.FullSyncCompleted = true;
            state.LastSyncedThroughUtc = DateTime.UtcNow;
            state.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        private async Task RunIncrementalSyncAsync( InventorySalesSyncState state )
        {
            DateTime sinceUtc = state.LastSyncedThroughUtc ?? DateTime.UtcNow.AddDays( -1 );
            Dictionary<string, int> delta = await _vatReportService.GetSoldQuantitiesFromShopifySinceAsync( sinceUtc );
            if (delta.Count > 0)
            {
                await ApplyDeltaAsync( delta );
            }

            state.LastSyncedThroughUtc = DateTime.UtcNow;
            state.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        private async Task ReplaceSoldCacheAsync( Dictionary<string, int> soldByProduct )
        {
            _db.InventoryProductSales.RemoveRange( _db.InventoryProductSales );
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, int> entry in soldByProduct )
            {
                if (string.IsNullOrWhiteSpace( entry.Key ) || entry.Value <= 0) continue;
                _db.InventoryProductSales.Add( new InventoryProductSale
                {
                    ShopifyProductId = entry.Key,
                    SoldQuantity = entry.Value,
                    UpdatedAtUtc = now
                } );
            }
            await _db.SaveChangesAsync();
        }

        private async Task ApplyDeltaAsync( Dictionary<string, int> delta )
        {
            List<InventoryProductSale> existing = await _db.InventoryProductSales.ToListAsync();
            Dictionary<string, InventoryProductSale> byProductId = existing.ToDictionary(
                x => x.ShopifyProductId,
                StringComparer.OrdinalIgnoreCase
            );
            DateTime now = DateTime.UtcNow;

            foreach (KeyValuePair<string, int> entry in delta )
            {
                if (string.IsNullOrWhiteSpace( entry.Key ) || entry.Value <= 0) continue;
                if (byProductId.TryGetValue( entry.Key, out InventoryProductSale? row ))
                {
                    row.SoldQuantity += entry.Value;
                    row.UpdatedAtUtc = now;
                }
                else
                {
                    InventoryProductSale created = new()
                    {
                        ShopifyProductId = entry.Key,
                        SoldQuantity = entry.Value,
                        UpdatedAtUtc = now
                    };
                    _db.InventoryProductSales.Add( created );
                    byProductId[entry.Key] = created;
                }
            }

            await _db.SaveChangesAsync();
        }

        private async Task<InventorySalesSyncState> GetOrCreateStateAsync()
        {
            InventorySalesSyncState? state = await _db.InventorySalesSyncStates
                .FirstOrDefaultAsync( x => x.Id == SyncStateId );
            if (state is not null) return state;

            state = new InventorySalesSyncState
            {
                Id = SyncStateId,
                FullSyncCompleted = false,
                UpdatedAtUtc = DateTime.MinValue
            };
            _db.InventorySalesSyncStates.Add( state );
            await _db.SaveChangesAsync();
            return state;
        }
    }
}
