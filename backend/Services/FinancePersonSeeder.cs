using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public static class FinancePersonSeeder
    {
        public const string ZoyaDefaultName = "Зоя";
        public const string MitsyaDefaultName = "Міця";

        private static readonly (string Name, int SortOrder)[] DefaultPersons =
        [
            (ZoyaDefaultName, 0),
            (MitsyaDefaultName, 1),
        ];

        public static async Task EnsureDefaultAsync( AppDbContext db )
        {
            foreach ((string name, int sortOrder) in DefaultPersons)
            {
                bool exists = await db.FinancePersons.AnyAsync( x => x.Name == name );
                if (exists) continue;

                db.FinancePersons.Add( new FinancePerson
                {
                    Name = name,
                    SortOrder = sortOrder,
                    CreatedAtUtc = DateTime.UtcNow
                } );
            }

            await db.SaveChangesAsync();
        }
    }
}
