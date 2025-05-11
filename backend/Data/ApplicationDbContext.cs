using Microsoft.EntityFrameworkCore;
using backend.Models;

namespace backend.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext( DbContextOptions<ApplicationDbContext> options )
        : base( options ) { }

        public DbSet<Product> Products { get; set; } = default!;
        public DbSet<Supplier> Suppliers { get; set; } = default!;
    }
}
