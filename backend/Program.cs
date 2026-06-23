using backend.Data;
using backend.Services;
using backend.Services.Auth;
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

builder.Services.AddScoped<JwtService>( );
builder.Services.AddScoped<SupplierService>( );
builder.Services.AddScoped<SupplierInventoryService>( );
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
    options.UseNpgsql( builder.Configuration.GetConnectionString( "DefaultConnection" ) ) );

var app = builder.Build( );

using (var scope = app.Services.CreateScope( ))
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>( );
    db.Database.Migrate( );
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
