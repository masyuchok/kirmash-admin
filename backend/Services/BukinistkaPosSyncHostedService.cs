namespace backend.Services;

public sealed class BukinistkaPosSyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<BukinistkaPosSyncHostedService> _logger;

    public BukinistkaPosSyncHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<BukinistkaPosSyncHostedService> logger )
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync( CancellationToken stoppingToken )
    {
        // Stagger startup so migrations finish first.
        try
        {
            await Task.Delay( TimeSpan.FromSeconds( 20 ), stoppingToken );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int minutes = 10;
            string? raw = _config["Odoo:PosSyncIntervalMinutes"];
            if (int.TryParse( raw, out int parsed ) && parsed > 0)
            {
                minutes = Math.Clamp( parsed, 1, 24 * 60 );
            }

            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                BukinistkaPosShopifySyncService sync =
                    scope.ServiceProvider.GetRequiredService<BukinistkaPosShopifySyncService>();
                Models.KirmaBukinistkaPosSyncResultDto result =
                    await sync.SyncAsync( stoppingToken );

                if (result.Skipped)
                {
                    _logger.LogDebug(
                        "Bukinistka POS sync skipped: {Reason}",
                        result.SkipReason );
                }
                else
                {
                    _logger.LogInformation(
                        "Bukinistka POS sync done: orders={Orders}, lines={Lines}, units={Units}",
                        result.OrdersScanned,
                        result.LinesProcessed,
                        result.UnitsSynced );
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError( ex, "Bukinistka POS sync failed." );
            }

            try
            {
                await Task.Delay( TimeSpan.FromMinutes( minutes ), stoppingToken );
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
