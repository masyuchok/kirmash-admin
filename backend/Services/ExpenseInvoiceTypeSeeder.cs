using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public static class ExpenseInvoiceTypeSeeder
    {
        public const string SupplierPaymentDefaultName = "Аплата пастаўшчыку";
        public const string PackagingDefaultName = "Упакоўка";
        public const string LegacySupplierPaymentName = "Ад пастаўшчыка";

        private static readonly string[] SystemDefaultNames =
        [
            SupplierPaymentDefaultName,
            PackagingDefaultName,
        ];

        private static readonly Dictionary<string, string> LegacyNameMap = new( StringComparer.Ordinal )
        {
            [LegacySupplierPaymentName] = SupplierPaymentDefaultName,
        };

        public static async Task EnsureDefaultAsync( AppDbContext db )
        {
            foreach (KeyValuePair<string, string> legacy in LegacyNameMap)
            {
                List<ExpenseInvoiceType> legacyRows = await db.ExpenseInvoiceTypes
                    .Where( x => x.Name == legacy.Key )
                    .ToListAsync();
                foreach (ExpenseInvoiceType row in legacyRows)
                {
                    row.Name = legacy.Value;
                }
            }

            foreach (string defaultName in SystemDefaultNames)
            {
                await DeduplicateByNameAsync( db, defaultName );
                await EnsureSystemTypeExistsAsync( db, defaultName );
            }

            await db.SaveChangesAsync();
        }

        private static async Task DeduplicateByNameAsync( AppDbContext db, string name )
        {
            List<ExpenseInvoiceType> sameNameRows = await db.ExpenseInvoiceTypes
                .Where( x => x.Name.ToLower() == name.ToLower() )
                .OrderBy( x => x.IsSystem ? 0 : 1 )
                .ThenBy( x => x.Id )
                .ToListAsync();

            if (sameNameRows.Count <= 1) return;

            ExpenseInvoiceType keeper = sameNameRows[0];
            keeper.Name = name;
            keeper.IsSystem = true;

            foreach (ExpenseInvoiceType duplicate in sameNameRows.Skip( 1 ))
            {
                List<VatReportExpense> linked = await db.VatReportExpenses
                    .Where( x => x.ExpenseInvoiceTypeId == duplicate.Id )
                    .ToListAsync();
                foreach (VatReportExpense expense in linked)
                {
                    expense.ExpenseInvoiceTypeId = keeper.Id;
                }

                db.ExpenseInvoiceTypes.Remove( duplicate );
            }
        }

        private static async Task EnsureSystemTypeExistsAsync( AppDbContext db, string name )
        {
            bool exists = await db.ExpenseInvoiceTypes.AnyAsync( x => x.Name == name );
            if (exists) return;

            db.ExpenseInvoiceTypes.Add( new ExpenseInvoiceType
            {
                Name = name,
                IsSystem = true,
                CreatedAtUtc = DateTime.UtcNow
            } );
        }
    }
}
