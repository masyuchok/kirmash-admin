using backend.Data;
using backend.Services;
using backend.Services.Auth;
using backend.Services.Odoo;
using backend.Services.Shopify;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder( args );

// Add services to the container.

// JWT auth
builder.Services.AddAuthentication( JwtBearerDefaults.AuthenticationScheme )
    .AddJwtBearer( options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes( builder.Configuration["Jwt:Secret"]! )
            ),
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                string? cookie = context.Request.Cookies["jwt_token"];
                if (!string.IsNullOrEmpty( cookie ))
                {
                    context.Token = cookie;
                }

                return Task.CompletedTask;
            }
        };
    } );

builder.Services.AddAuthorization( );

const string FrontendCors = "Frontend";
string allowedOrigin = builder.Configuration
    .GetSection( "ClientUrl" )
    .Get<string>( ) ?? string.Empty;

builder.Services.AddCors( options =>
{
    options.AddPolicy( FrontendCors, policy =>
        policy
            .WithOrigins( allowedOrigin )
            .AllowAnyHeader( )
            .AllowAnyMethod( )
            .AllowCredentials( )
    );
} );

builder.Services.AddControllers( );
builder.Services.AddHttpClient( "Shopify" );
builder.Services.AddHttpClient( "Odoo" );

builder.Services.AddScoped<JwtService>( );
builder.Services.AddScoped<OdooJsonRpcClient>( );
builder.Services.AddScoped<OdooAuthService>( );
builder.Services.AddScoped<OdooProductService>( );
builder.Services.AddScoped<OdooStockReceiptService>( );
builder.Services.AddScoped<OdooPosSalesReader>( );
builder.Services.AddScoped<KirmaBukinistkaOfferService>( );
builder.Services.AddScoped<BukinistkaPosShopifySyncService>( );
builder.Services.AddHostedService<BukinistkaPosSyncHostedService>( );
builder.Services.AddScoped<SupplierService>( );
builder.Services.AddScoped<SupplierInventoryService>( );
builder.Services.AddScoped<ProductLedgerService>( );
builder.Services.AddScoped<InventorySalesCacheService>( );
builder.Services.AddScoped<SupplyService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<ShopifyGraphqlClient>();
builder.Services.AddScoped<ShopifyInventoryService>();
builder.Services.AddScoped<ShopifyProductCatalogService>();
builder.Services.AddScoped<ShopifyVariantLookupService>();
builder.Services.AddScoped<ShopifyOrderFetchService>();
builder.Services.AddScoped<VatReportProfitService>();
builder.Services.AddScoped<VatReportQueryService>();
builder.Services.AddScoped<VatReportGenerationService>();
builder.Services.AddScoped<VatReportMutationService>();
builder.Services.AddScoped<VatReportLockService>();
builder.Services.AddScoped<VatReportFinanceSyncService>();
builder.Services.AddScoped<VatReportUnpaidLinkService>();
builder.Services.AddScoped<VatReportService>();
builder.Services.AddScoped<FinanceService>();
builder.Services.AddHttpContextAccessor();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer( );
builder.Services.AddSwaggerGen( );

builder.Services.AddDbContext<AppDbContext>( options =>
    options.UseNpgsql( builder.Configuration.GetConnectionString( "DefaultConnection" ) )
        // SQL-only migrations update the DB; snapshot is kept in sync manually.
        .ConfigureWarnings( w =>
            w.Ignore( Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning ) ) );

var app = builder.Build( );

using (var scope = app.Services.CreateScope( ))
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>( );
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>( ).CreateLogger( "Startup" );
    try
    {
        logger.LogInformation( "Applying database migrations..." );
        db.Database.Migrate( );
        // Belt-and-suspenders: keep schema usable even if a raw SQL migration
        // was skipped or partially applied on an older deploy.
        db.Database.ExecuteSqlRaw(
            """
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "OdooProductId" integer NULL;
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "OdooQuantityBeforeAccept" integer NULL;
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "AcceptedListPrice" numeric(18,2) NULL;

            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaPosSales" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "OdooPosOrderId" integer NOT NULL,
                "OdooPosOrderLineId" integer NOT NULL,
                "OdooPosOrderName" character varying(128) NULL,
                "OfferId" integer NULL,
                "OdooProductId" integer NOT NULL,
                "ShopifyProductId" character varying(64) NOT NULL,
                "ShopifyVariantId" character varying(64) NOT NULL DEFAULT '',
                "Quantity" integer NOT NULL,
                "ProductName" character varying(512) NOT NULL,
                "IsOwnStock" boolean NOT NULL DEFAULT false,
                "SoldAtUtc" timestamp with time zone NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_KirmaBukinistkaPosSales" PRIMARY KEY ("Id")
            );
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "IsOwnStock" boolean NOT NULL DEFAULT false;
            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaPosSyncStates" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "LastSyncedAtUtc" timestamp with time zone NULL,
                "LastProcessedOrderId" integer NULL,
                CONSTRAINT "PK_KirmaBukinistkaPosSyncStates" PRIMARY KEY ("Id")
            );
            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaOdooOwnStockBuffers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "OdooProductId" integer NOT NULL,
                "OwnQtyRemaining" integer NOT NULL DEFAULT 0,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_KirmaBukinistkaOdooOwnStockBuffers" PRIMARY KEY ("Id")
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOdooOwnStockBuffers_OdooProductId"
                ON "KirmaBukinistkaOdooOwnStockBuffers" ("OdooProductId");
            """ );
        logger.LogInformation( "Database migrations applied." );
    }
    catch (Exception ex)
    {
        logger.LogCritical( ex, "Database migration failed. Backend will not start." );
        throw;
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment( ))
{
    app.UseSwagger( );
    app.UseSwaggerUI( );
}

app.UseHttpsRedirection( );
app.UseRouting( );

app.UseCors( FrontendCors );
app.UseAuthentication( );
app.UseAuthorization( );

app.MapControllers( );

app.Run( );
