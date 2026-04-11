using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services
{
    public class SupplierService
    {
        private readonly AppDbContext _db;
        public SupplierService(AppDbContext db) {
            _db = db;
        }

        public async Task<int> AddSupplier(Supplier supplier)
        {
            bool exists = await _db.Suppliers
                .AnyAsync( s => s.Name.ToLower( ) == supplier.Name.ToLower( ) );
            if (exists)
            {
                throw new InvalidOperationException( $"Пастаўшчык з імем '{supplier.Name}' ужо існуе." );
            }

            _db.Suppliers.Add( supplier );
            await _db.SaveChangesAsync( );

            return supplier.Id!.Value;
        }

        public async Task<List<Supplier>> GetAllAsync()
        {
            return await _db.Suppliers
                .OrderBy( s => s.Name )
                .ToListAsync( );
        }

        public async Task<Supplier> GetSupplier(int id)
        {
            return await _db.Suppliers
                .FirstAsync(s => s.Id == id);
        }

        public async Task UpdateSupplier(int id, Supplier newSupplier)
        {
            Supplier dbSupplier = await GetSupplier(id);

            if (dbSupplier == null)
            {
                throw new InvalidOperationException("Пастаўшчык не знойдзены");
            }

            if (!string.IsNullOrEmpty(newSupplier.Name))
                dbSupplier.Name = newSupplier.Name;

            if (!string.IsNullOrEmpty(newSupplier.Phone))
                dbSupplier.Phone = newSupplier.Phone;

            if (!string.IsNullOrEmpty(newSupplier.ContactName))
                dbSupplier.ContactName = newSupplier.ContactName;

            if (!string.IsNullOrEmpty(newSupplier.Country))
                dbSupplier.Country = newSupplier.Country;

            if (!string.IsNullOrEmpty(newSupplier.TGContact))
                dbSupplier.TGContact = newSupplier.TGContact;

            if (!string.IsNullOrEmpty(newSupplier.Instagram))
                dbSupplier.Instagram = newSupplier.Instagram;

            if (!string.IsNullOrEmpty(newSupplier.Email))
                dbSupplier.Email = newSupplier.Email;

            if (!string.IsNullOrEmpty(newSupplier.Website))
                dbSupplier.Website = newSupplier.Website;

            if (!string.IsNullOrEmpty(newSupplier.Country))
                dbSupplier.Country = newSupplier.Country;

            if (!string.IsNullOrEmpty(newSupplier.City))
                dbSupplier.City = newSupplier.City;

            if (!string.IsNullOrEmpty(newSupplier.Currency))
                dbSupplier.Currency = newSupplier.Currency;

            if (newSupplier.WorkStart.HasValue)
                dbSupplier.WorkStart = newSupplier.WorkStart;

            if (newSupplier.isVATPayer != dbSupplier.isVATPayer)
                dbSupplier.isVATPayer = newSupplier.isVATPayer;

            await _db.SaveChangesAsync();
        }
    }
}
