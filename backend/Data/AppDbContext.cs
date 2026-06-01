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
        public DbSet<VatReportExpense> VatReportExpenses { get; set; } = default!;
        public DbSet<VatReportExpenseProduct> VatReportExpenseProducts { get; set; } = default!;
        public DbSet<VatReportCashSale> VatReportCashSales { get; set; } = default!;
        public DbSet<InvoiceSettings> InvoiceSettings { get; set; } = default!;
        public DbSet<ExpenseInvoiceType> ExpenseInvoiceTypes { get; set; } = default!;
        public DbSet<InventoryProductSale> InventoryProductSales { get; set; } = default!;
        public DbSet<InventorySalesSyncState> InventorySalesSyncStates { get; set; } = default!;
        public DbSet<FinancePerson> FinancePersons { get; set; } = default!;
        public DbSet<FinanceMovement> FinanceMovements { get; set; } = default!;
        public DbSet<FinanceRecurringExpense> FinanceRecurringExpenses { get; set; } = default!;
        public DbSet<FinanceRecurringApplication> FinanceRecurringApplications { get; set; } = default!;

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
                entity.Property( sp => sp.ShopifyVariantId )
                    .IsRequired()
                    .HasMaxLength( 64 )
                    .HasDefaultValue( string.Empty );
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

                entity.HasIndex( sp => new { sp.SupplyId, sp.ShopifyProductId, sp.ShopifyVariantId } )
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

            modelBuilder.Entity<VatReportExpense>( entity =>
            {
                entity.HasOne( x => x.VatReport )
                    .WithMany( r => r.Expenses )
                    .HasForeignKey( x => x.VatReportId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.HasOne( x => x.ExpenseInvoiceType )
                    .WithMany( t => t.Expenses )
                    .HasForeignKey( x => x.ExpenseInvoiceTypeId )
                    .OnDelete( DeleteBehavior.Restrict );

                entity.Property( x => x.GrossAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.VatAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.NetAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.ExpenseDateUtc )
                    .IsRequired();
                entity.Property( x => x.Comment )
                    .HasMaxLength( 1024 );
                entity.Property( x => x.InvoiceFileName )
                    .HasMaxLength( 512 );
                entity.Property( x => x.InvoiceContentType )
                    .HasMaxLength( 128 );
                entity.Property( x => x.InvoiceData )
                    .HasColumnType( "bytea" );
                entity.Property( x => x.CreatedAtUtc )
                    .IsRequired();

                entity.HasOne( x => x.Supplier )
                    .WithMany()
                    .HasForeignKey( x => x.SupplierId )
                    .OnDelete( DeleteBehavior.SetNull );
            } );

            modelBuilder.Entity<VatReportExpenseProduct>( entity =>
            {
                entity.HasOne( x => x.VatReportExpense )
                    .WithMany( e => e.Products )
                    .HasForeignKey( x => x.VatReportExpenseId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( x => x.ShopifyProductId )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( x => x.ProductTitle )
                    .IsRequired()
                    .HasMaxLength( 512 );
                entity.Property( x => x.Quantity )
                    .IsRequired();
            } );

            modelBuilder.Entity<VatReportCashSale>( entity =>
            {
                entity.HasOne( x => x.VatReport )
                    .WithMany( r => r.CashSales )
                    .HasForeignKey( x => x.VatReportId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( x => x.ShopifyProductId )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( x => x.ProductTitle )
                    .IsRequired()
                    .HasMaxLength( 512 );
                entity.Property( x => x.Quantity )
                    .IsRequired();
                entity.Property( x => x.UnitPrice )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.GrossAmount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.CreatedAtUtc )
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

                entity.Property( i => i.ShopifyProductId )
                    .IsRequired()
                    .HasMaxLength( 64 );
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

            modelBuilder.Entity<ExpenseInvoiceType>( entity =>
            {
                entity.Property( x => x.Name )
                    .IsRequired()
                    .HasMaxLength( 256 );
                entity.Property( x => x.IsSystem )
                    .IsRequired();
                entity.Property( x => x.CreatedAtUtc )
                    .IsRequired();
            } );

            modelBuilder.Entity<InventoryProductSale>( entity =>
            {
                entity.Property( x => x.ShopifyProductId )
                    .IsRequired()
                    .HasMaxLength( 64 );
                entity.Property( x => x.UpdatedAtUtc )
                    .IsRequired();
                entity.HasIndex( x => x.ShopifyProductId )
                    .IsUnique();
            } );

            modelBuilder.Entity<InventorySalesSyncState>( entity =>
            {
                entity.Property( x => x.FullSyncCompleted )
                    .IsRequired();
                entity.Property( x => x.UpdatedAtUtc )
                    .IsRequired();
            } );

            modelBuilder.Entity<FinancePerson>( entity =>
            {
                entity.Property( x => x.Name )
                    .IsRequired()
                    .HasMaxLength( 128 );
                entity.Property( x => x.SortOrder )
                    .IsRequired();
                entity.Property( x => x.CreatedAtUtc )
                    .IsRequired();
                entity.HasIndex( x => x.Name )
                    .IsUnique();
            } );

            modelBuilder.Entity<FinanceMovement>( entity =>
            {
                entity.HasOne( x => x.Person )
                    .WithMany( p => p.Movements )
                    .HasForeignKey( x => x.PersonId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.HasOne( x => x.RecurringExpense )
                    .WithMany( r => r.GeneratedMovements )
                    .HasForeignKey( x => x.RecurringExpenseId )
                    .OnDelete( DeleteBehavior.SetNull );

                entity.Property( x => x.Kind )
                    .IsRequired();
                entity.Property( x => x.Amount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.Description )
                    .IsRequired()
                    .HasMaxLength( 1024 );
                entity.Property( x => x.MovementDate )
                    .IsRequired();
                entity.Property( x => x.CreatedAtUtc )
                    .IsRequired();
                entity.Property( x => x.UpdatedAtUtc )
                    .IsRequired();
            } );

            modelBuilder.Entity<FinanceRecurringExpense>( entity =>
            {
                entity.HasOne( x => x.Person )
                    .WithMany( p => p.RecurringExpenses )
                    .HasForeignKey( x => x.PersonId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( x => x.Kind )
                    .IsRequired();
                entity.Property( x => x.Amount )
                    .HasColumnType( "numeric(12,2)" );
                entity.Property( x => x.Description )
                    .IsRequired()
                    .HasMaxLength( 1024 );
                entity.Property( x => x.DayOfMonth )
                    .IsRequired();
                entity.Property( x => x.IsActive )
                    .IsRequired();
                entity.Property( x => x.CreatedAtUtc )
                    .IsRequired();
            } );

            modelBuilder.Entity<FinanceRecurringApplication>( entity =>
            {
                entity.HasOne( x => x.RecurringExpense )
                    .WithMany( r => r.Applications )
                    .HasForeignKey( x => x.RecurringExpenseId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.HasOne( x => x.Movement )
                    .WithMany()
                    .HasForeignKey( x => x.MovementId )
                    .OnDelete( DeleteBehavior.Cascade );

                entity.Property( x => x.AppliedAtUtc )
                    .IsRequired();

                entity.HasIndex( x => new { x.RecurringExpenseId, x.Year, x.Month } )
                    .IsUnique();
            } );
        }
    }
}
