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
        public DbSet<VatReport> VatReports { get; set; } = default!;
        public DbSet<VatReportRow> VatReportRows { get; set; } = default!;
        public DbSet<VatReportRowItem> VatReportRowItems { get; set; } = default!;
        public DbSet<InvoiceSettings> InvoiceSettings { get; set; } = default!;

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
                entity.Property( sp => sp.VatRatePercent )
                    .HasColumnType( "numeric(7,2)" )
                    .HasDefaultValue( 23m );
                entity.Property( sp => sp.MarginPercent )
                    .HasColumnType( "numeric(7,2)" );
                entity.Property( sp => sp.SalePrice )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( sp => sp.SyncWithShopify )
                    .HasDefaultValue( true );

                entity.HasIndex( sp => new { sp.SupplyId, sp.ShopifyProductId } )
                    .IsUnique();
            } );

            modelBuilder.Entity<VatReport>( entity =>
            {
                entity.Property( r => r.PeriodYear )
                    .IsRequired();
                entity.Property( r => r.PeriodMonth )
                    .IsRequired();
                entity.Property( r => r.Type )
                    .IsRequired()
                    .HasMaxLength( 32 );
                entity.Property( r => r.Name )
                    .IsRequired()
                    .HasMaxLength( 256 );
                entity.Property( r => r.Document )
                    .HasMaxLength( 1024 );
                entity.Property( r => r.Vat )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.VatCredit )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.VatToPay )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.Documents )
                    .HasColumnType( "text[]" );
                entity.Property( r => r.ShopifyOrderIds )
                    .HasColumnType( "text[]" );
                entity.Property( r => r.CreatedAtUtc )
                    .IsRequired();
            } );

            modelBuilder.Entity<VatReportRow>( entity =>
            {
                entity.HasOne( r => r.VatReport )
                    .WithMany( r => r.Rows )
                    .HasForeignKey( r => r.VatReportId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( r => r.ShopifyOrderId )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( r => r.OrderNumber )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( r => r.OrderDateUtc )
                    .IsRequired();
                entity.Property( r => r.VatRatePercent )
                    .HasColumnType( "numeric(5,2)" );
                entity.Property( r => r.GrossAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.VatAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.NetAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.ShippingGrossAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.ShippingNetAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( r => r.InvoiceFileName )
                    .HasMaxLength( 512 );
                entity.Property( r => r.InvoiceContentType )
                    .HasMaxLength( 128 );
                entity.Property( r => r.InvoiceData )
                    .HasColumnType( "bytea" );
            } );

            modelBuilder.Entity<VatReportRowItem>( entity =>
            {
                entity.HasOne( i => i.VatReportRow )
                    .WithMany( r => r.Items )
                    .HasForeignKey( i => i.VatReportRowId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( i => i.ProductTitle )
                    .IsRequired()
                    .HasMaxLength( 512 );
                entity.Property( i => i.ProductType )
                    .HasMaxLength( 256 );
                entity.Property( i => i.Quantity )
                    .IsRequired();
                entity.Property( i => i.UnitPrice )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( i => i.GrossAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( i => i.AssignedVatRatePercent )
                    .HasColumnType( "numeric(5,2)" );
                entity.Property( i => i.AssignmentReason )
                    .IsRequired()
                    .HasMaxLength( 256 );
            } );

            modelBuilder.Entity<InvoiceSettings>( entity =>
            {
                entity.Property( x => x.CompanyName )
                    .IsRequired()
                    .HasMaxLength( 512 );
                entity.Property( x => x.Address )
                    .IsRequired()
                    .HasMaxLength( 1024 );
                entity.Property( x => x.Email )
                    .IsRequired()
                    .HasMaxLength( 320 );
                entity.Property( x => x.Website )
                    .IsRequired()
                    .HasMaxLength( 1024 );
                entity.Property( x => x.Nip )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( x => x.Currency )
                    .IsRequired()
                    .HasMaxLength( 16 );
                entity.Property( x => x.UpdatedAtUtc )
                    .IsRequired();
            } );
        }
    }
}
