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
builder.Services.AddHttpClient( "Groq" );
builder.Services.AddHttpClient( "Tavily" );
builder.Services.AddHttpClient( "SerpApi", client =>
{
    client.Timeout = TimeSpan.FromSeconds( 60 );
} );
builder.Services.AddHttpClient( "BookLookupPage", client =>
{
    client.Timeout = TimeSpan.FromSeconds( 12 );
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "KirmaBookLookup/1.0 (+https://kirma.sh)" );
} );

builder.Services.AddMemoryCache( );
builder.Services.AddScoped<JwtService>( );
builder.Services.AddScoped<OdooJsonRpcClient>( );
builder.Services.AddScoped<OdooAuthService>( );
builder.Services.AddScoped<OdooBukinistkaSessionResolver>( );
builder.Services.AddScoped<OdooProductService>( );
builder.Services.AddScoped<OdooStockReceiptService>( );
builder.Services.AddScoped<OdooStockDeliveryService>( );
builder.Services.AddScoped<OdooPosSalesReader>( );
builder.Services.AddScoped<KirmaBukinistkaOfferService>( );
builder.Services.AddScoped<BukinistkaPosShopifySyncService>( );
builder.Services.AddScoped<BukinistkaPosInvoiceService>( );
builder.Services.AddScoped<BukinistkaShopifyOdooDeliverySyncService>( );
builder.Services.AddScoped<BukinistkaInventoryService>( );
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
builder.Services.AddScoped<InvoicePdfTextExtractor>();
builder.Services.AddScoped<InvoiceLineItemParser>();
builder.Services.AddScoped<GroqInvoiceExtractionService>();
builder.Services.AddScoped<InvoiceExpenseExtractionService>();
builder.Services.AddSingleton<BookLookupSessionStore>();
builder.Services.AddScoped<TavilySearchService>();
builder.Services.AddScoped<SerpApiGoogleLensService>();
builder.Services.AddScoped<BookLookupService>();
builder.Services.AddScoped<VatReportProfitService>();
builder.Services.AddScoped<VatReportQueryService>();
builder.Services.AddScoped<VatReportGenerationService>();
builder.Services.AddScoped<VatReportMutationService>();
builder.Services.AddScoped<VatReportLockService>();
builder.Services.AddScoped<VatReportFinanceSyncService>();
builder.Services.AddScoped<VatReportUnpaidLinkService>();
builder.Services.AddScoped<VatReportService>();
builder.Services.AddScoped<FinanceService>();
builder.Services.AddScoped<KirmashService>();
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>( options =>
{
    options.MultipartBodyLengthLimit = 32 * 1024 * 1024;
    options.ValueLengthLimit = 32 * 1024 * 1024;
} );
builder.WebHost.ConfigureKestrel( options =>
{
    options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
} );

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
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "SyncOnSale" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "Direction" character varying(32) NOT NULL DEFAULT 'KirmaToBukinistka';
            UPDATE "KirmaBukinistkaOffers"
                SET "Direction" = 'KirmaToBukinistka'
                WHERE "Direction" IS NULL OR TRIM("Direction") = '';
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "IsAssignment" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "PeerPriceChangePending" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaOffers"
                ADD COLUMN IF NOT EXISTS "AcceptedAtUtc" timestamp with time zone NULL;
            UPDATE "KirmaBukinistkaOffers"
                SET "AcceptedAtUtc" = "CreatedAtUtc"
                WHERE "Status" = 'Accepted' AND "AcceptedAtUtc" IS NULL;
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOffers_PeerPriceChangePending"
                ON "KirmaBukinistkaOffers" ("PeerPriceChangePending");
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_ShopifyProductId"
                ON "KirmaBukinistkaOffers" ("Direction", "Status", "ShopifyProductId");
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_OdooProductId"
                ON "KirmaBukinistkaOffers" ("Direction", "Status", "OdooProductId");

            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaPendingOfferSaleDeductions" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "OfferId" integer NOT NULL,
                "Source" character varying(32) NOT NULL,
                "SourceKey" character varying(128) NOT NULL,
                "Quantity" integer NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_KirmaBukinistkaPendingOfferSaleDeductions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_KirmaBukinistkaPendingOfferSaleDeductions_Offers"
                    FOREIGN KEY ("OfferId") REFERENCES "KirmaBukinistkaOffers" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPendingOfferSaleDeductions_Source_SourceKey_OfferId"
                ON "KirmaBukinistkaPendingOfferSaleDeductions" ("Source", "SourceKey", "OfferId");
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPendingOfferSaleDeductions_OfferId"
                ON "KirmaBukinistkaPendingOfferSaleDeductions" ("OfferId");

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
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "IsReversed" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "IsReturn" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "IsInvoiced" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "InvoicedAtUtc" timestamp with time zone NULL;
            ALTER TABLE "KirmaBukinistkaPosSales"
                ADD COLUMN IF NOT EXISTS "VatReportRowId" integer NULL;
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPosSales_IsInvoiced"
                ON "KirmaBukinistkaPosSales" ("IsInvoiced");
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

            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaShopifyDeliverySyncs" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "ShopifyOrderId" character varying(64) NOT NULL,
                "ShopifyOrderNumber" character varying(64) NOT NULL,
                "ShopifyProductId" character varying(64) NOT NULL,
                "ShopifyVariantId" character varying(64) NOT NULL DEFAULT '',
                "OfferId" integer NOT NULL,
                "OdooProductId" integer NOT NULL,
                "Quantity" integer NOT NULL,
                "OdooPickingId" integer NOT NULL,
                "OdooPickingName" character varying(128) NULL,
                "SoldAtUtc" timestamp with time zone NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_KirmaBukinistkaShopifyDeliverySyncs" PRIMARY KEY ("Id")
            );
            ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs"
                ADD COLUMN IF NOT EXISTS "IsCancelled" boolean NOT NULL DEFAULT false;
            ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs"
                ADD COLUMN IF NOT EXISTS "CancelledAtUtc" timestamp with time zone NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_KirmaBukinistkaShopifyDeliverySyncs_Order_Product_Offer"
                ON "KirmaBukinistkaShopifyDeliverySyncs" ("ShopifyOrderId", "ShopifyProductId", "ShopifyVariantId", "OfferId");
            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaShopifyDeliverySyncStates" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "LastSyncedAtUtc" timestamp with time zone NULL,
                CONSTRAINT "PK_KirmaBukinistkaShopifyDeliverySyncStates" PRIMARY KEY ("Id")
            );

            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaReceiptDrafts" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Status" character varying(32) NOT NULL,
                "CreatedByLogin" character varying(256) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                "LastError" character varying(2048) NULL,
                "OdooPickingId" integer NULL,
                "OdooPickingName" character varying(128) NULL,
                CONSTRAINT "PK_KirmaBukinistkaReceiptDrafts" PRIMARY KEY ("Id")
            );
            CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaReceiptDrafts_Status"
                ON "KirmaBukinistkaReceiptDrafts" ("Status");

            CREATE TABLE IF NOT EXISTS "KirmaBukinistkaReceiptDraftLines" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "ReceiptDraftId" integer NOT NULL,
                "OfferId" integer NOT NULL,
                "OdooProductId" integer NOT NULL,
                "OdooProductName" character varying(512) NOT NULL,
                "ListPrice" numeric(18,2) NULL,
                "ApplyKirmaCostPrice" boolean NULL,
                CONSTRAINT "PK_KirmaBukinistkaReceiptDraftLines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_KirmaBukinistkaReceiptDraftLines_Drafts"
                    FOREIGN KEY ("ReceiptDraftId")
                    REFERENCES "KirmaBukinistkaReceiptDrafts" ("Id")
                    ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_KirmaBukinistkaReceiptDraftLines_Draft_Offer"
                ON "KirmaBukinistkaReceiptDraftLines" ("ReceiptDraftId", "OfferId");

            ALTER TABLE "Suppliers"
                ADD COLUMN IF NOT EXISTS "PriceListUrl" text NULL;

            CREATE TABLE IF NOT EXISTS "Kirmashes" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Title" character varying(256) NOT NULL,
                "Description" text NOT NULL DEFAULT '',
                "EventDate" date NOT NULL,
                "Status" character varying(32) NOT NULL DEFAULT 'draft',
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_Kirmashes" PRIMARY KEY ("Id")
            );

            CREATE TABLE IF NOT EXISTS "KirmashLines" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "KirmashId" integer NOT NULL,
                "ShopifyProductId" character varying(64) NOT NULL,
                "ShopifyVariantId" character varying(64) NOT NULL DEFAULT '',
                "Title" character varying(512) NOT NULL,
                "UnitPrice" numeric(12,2) NOT NULL,
                "Quantity" integer NOT NULL,
                CONSTRAINT "PK_KirmashLines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_KirmashLines_Kirmashes"
                    FOREIGN KEY ("KirmashId") REFERENCES "Kirmashes" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_KirmashLines_KirmashId"
                ON "KirmashLines" ("KirmashId");

            CREATE TABLE IF NOT EXISTS "KirmashPriceTags" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "KirmashId" integer NOT NULL,
                "KirmashLineId" integer NOT NULL,
                "Sequence" integer NOT NULL,
                "Title" character varying(512) NOT NULL,
                "UnitPrice" numeric(12,2) NOT NULL,
                "CheckoutUrl" character varying(2048) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_KirmashPriceTags" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_KirmashPriceTags_Kirmashes"
                    FOREIGN KEY ("KirmashId") REFERENCES "Kirmashes" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_KirmashPriceTags_KirmashLines"
                    FOREIGN KEY ("KirmashLineId") REFERENCES "KirmashLines" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_KirmashPriceTags_KirmashId"
                ON "KirmashPriceTags" ("KirmashId");
            CREATE INDEX IF NOT EXISTS "IX_KirmashPriceTags_KirmashLineId"
                ON "KirmashPriceTags" ("KirmashLineId");
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
