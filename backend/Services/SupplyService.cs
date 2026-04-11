using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services
{
    public class SupplyService
    {
        private readonly AppDbContext _db;
        public SupplyService(AppDbContext db) {
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
                    BooksNumber = s.Products.Count
                })
                .OrderBy(s => s.Date)
                .ToListAsync();
        }
    }
}
