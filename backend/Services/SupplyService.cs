using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class SupplyService
    {
        private readonly AppDbContext _db;

        public SupplyService( AppDbContext db )
        {
            _db = db;
        }

        public async Task<List<Supply>> GetAllAsync()
        {
            return await _db.Supplies
                .OrderBy(s => s.Date)
                .ToListAsync();
        }

        public async Task<List<SupplyListItem>> GetSupplyList()
        {
            return await _db.Supplies
                .Select(s => new SupplyListItem
                {
                    Id = s.Id,
                    SupplierName = s.Supplier.Name,
                    Date = s.Date,
                    ProductNumber = s.SupplyProducts.Count
                })
                .OrderBy(s => s.Date)
                .ToListAsync();
        }
    }
}
