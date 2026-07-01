using backend.Data;

using backend.Models;

using backend.Services.Shopify;

using Microsoft.EntityFrameworkCore;



namespace backend.Services

{

    public class InventorySalesCacheService

    {

        private const int SyncStateId = 1;

        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours( 1 );

        private static readonly SemaphoreSlim SyncLock = new( 1, 1 );



        private readonly AppDbContext _db;

        private readonly ShopifyOrderFetchService _shopifyOrders;

        private readonly ProductLedgerService _ledger;



        public InventorySalesCacheService(

            AppDbContext db,

            ShopifyOrderFetchService shopifyOrders,

            ProductLedgerService ledger )

        {

            _db = db;

            _shopifyOrders = shopifyOrders;

            _ledger = ledger;

        }



        public async Task<DateTime?> GetLastSyncedAtUtcAsync()

        {

            InventorySalesSyncState state = await GetOrCreateStateAsync();

            if (!state.FullSyncCompleted)

            {

                return null;

            }



            return state.LastSyncedThroughUtc ?? state.UpdatedAtUtc;

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



                await SyncUnreportedMonthsAsync( state );



                return state.LastSyncedThroughUtc ?? state.UpdatedAtUtc;

            }

            finally

            {

                SyncLock.Release();

            }

        }



        private async Task SyncUnreportedMonthsAsync( InventorySalesSyncState state )

        {

            List<(int Year, int Month)> unreportedPeriods = await _ledger.GetUnreportedPeriodsAsync();

            _db.InventoryProductSales.RemoveRange( _db.InventoryProductSales );

            DateTime now = DateTime.UtcNow;



            foreach ((int year, int month) in unreportedPeriods)

            {

                Dictionary<(string ProductId, string VariantId), int> soldByLine =

                    await _shopifyOrders.GetSoldQuantitiesByProductVariantForMonthAsync( year, month );

                foreach (KeyValuePair<(string ProductId, string VariantId), int> entry in soldByLine)

                {

                    if (string.IsNullOrWhiteSpace( entry.Key.ProductId ) || entry.Value <= 0) continue;

                    _db.InventoryProductSales.Add( new InventoryProductSale

                    {

                        PeriodYear = year,

                        PeriodMonth = month,

                        ShopifyProductId = entry.Key.ProductId,

                        ShopifyVariantId = entry.Key.VariantId,

                        SoldQuantity = entry.Value,

                        UpdatedAtUtc = now

                    } );

                }

            }



            state.FullSyncCompleted = true;

            state.LastSyncedThroughUtc = now;

            state.UpdatedAtUtc = now;

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


