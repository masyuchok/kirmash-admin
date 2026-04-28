using Microsoft.EntityFrameworkCore;
using backend.Models;

namespace backend.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext( DbContextOptions<AppDbContext> options )
        : base( options ) { }

        public DbSet<Product> Products { get; set; } = default!;
        public DbSet<Supplier> Suppliers { get; set; } = default!;
        public DbSet<Supply> Supplies { get; set; } = default!;
        public DbSet<SupplyProduct> SupplyProducts { get; set; } = default!;

        protected override void OnModelCreating( ModelBuilder modelBuilder )
        {
            base.OnModelCreating( modelBuilder );

            modelBuilder.Entity<SupplyProduct>( entity =>
            {
                entity.HasOne( sp => sp.Supply )
                    .WithMany( s => s.SupplyProducts )
                    .HasForeignKey( sp => sp.SupplyId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( sp => sp.ShopifyProductId )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( sp => sp.Quantity )
                    .IsRequired();
                entity.Property( sp => sp.SupplierPrice )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( sp => sp.MarginPercent )
                    .HasColumnType( "numeric(7,2)" );
                entity.Property( sp => sp.SalePrice )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( sp => sp.SyncWithShopify )
                    .HasDefaultValue( true );

                entity.HasIndex( sp => new { sp.SupplyId, sp.ShopifyProductId } )
                    .IsUnique();
            } );
        }
    }
}
