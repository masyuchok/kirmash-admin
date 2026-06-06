using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportFinanceSyncService
{
    private static readonly string[] MonthNamesBe =
    [
        "Студзень",
        "Люты",
        "Сакавік",
        "Красавік",
        "Май",
        "Чэрвень",
        "Ліпень",
        "Жнівень",
        "Верасень",
        "Кастрычнік",
        "Лістапад",
        "Снежань",
    ];

    private readonly AppDbContext _db;
    private readonly VatReportQueryService _query;

    public VatReportFinanceSyncService( AppDbContext db, VatReportQueryService query )
    {
        _db = db;
        _query = query;
    }

    public async Task<VatAutoFinanceSettings> GetOrCreateSettingsAsync()
    {
        VatAutoFinanceSettings? settings = await _db.VatAutoFinanceSettings.FirstOrDefaultAsync( x => x.Id == 1 );
        if (settings is not null)
        {
            return settings;
        }

        settings = new VatAutoFinanceSettings { Id = 1, IsEnabled = false, UpdatedAtUtc = DateTime.UtcNow };
        _db.VatAutoFinanceSettings.Add( settings );
        await _db.SaveChangesAsync();
        return settings;
    }

    public async Task SyncPeriodForReportIdAsync( int reportId )
    {
        VatReport? report = await _db.VatReports
            .AsNoTracking()
            .FirstOrDefaultAsync( r => r.Id == reportId );
        if (report is null)
        {
            return;
        }

        await SyncPeriodAsync( report.PeriodYear, report.PeriodMonth );
    }

    public async Task ResyncAllReportPeriodsAsync()
    {
        var periods = await _db.VatReports
            .AsNoTracking()
            .GroupBy( r => new { r.PeriodYear, r.PeriodMonth } )
            .Select( g => new { g.Key.PeriodYear, g.Key.PeriodMonth } )
            .ToListAsync();

        foreach (var period in periods)
        {
            await SyncPeriodAsync( period.PeriodYear, period.PeriodMonth );
        }

        List<VatPeriodFinancePayment> orphanLinks = await _db.VatPeriodFinancePayments
            .Include( l => l.FinanceMovement )
            .ToListAsync();
        foreach (VatPeriodFinancePayment link in orphanLinks)
        {
            bool hasReports = periods.Any( p =>
                p.PeriodYear == link.PeriodYear && p.PeriodMonth == link.PeriodMonth
            );
            if (!hasReports)
            {
                await RemoveLinkAsync( link );
            }
        }
    }

    public async Task SyncPeriodAsync( int periodYear, int periodMonth )
    {
        VatAutoFinanceSettings settings = await GetOrCreateSettingsAsync();
        bool hasReports = await _db.VatReports.AnyAsync( r =>
            r.PeriodYear == periodYear && r.PeriodMonth == periodMonth
        );

        VatPeriodFinancePayment? link = await _db.VatPeriodFinancePayments
            .Include( l => l.FinanceMovement )
            .FirstOrDefaultAsync( l => l.PeriodYear == periodYear && l.PeriodMonth == periodMonth );

        if (link is not null && link.FinanceMovement is null)
        {
            _db.VatPeriodFinancePayments.Remove( link );
            await _db.SaveChangesAsync();
            link = null;
        }

        if (!settings.IsEnabled || !settings.FinancePersonId.HasValue || !hasReports)
        {
            if (link is not null)
            {
                await RemoveLinkAsync( link );
            }

            return;
        }

        int personId = settings.FinancePersonId.Value;
        bool personExists = await _db.FinancePersons.AnyAsync( p => p.Id == personId );
        if (!personExists)
        {
            if (link is not null)
            {
                await RemoveLinkAsync( link );
            }

            return;
        }

        int baseReportId = await ResolveBaseReportIdAsync( periodYear, periodMonth );
        VatReportCombinedDetailsResponse combined = await _query.GetCombinedDetailsAsync( baseReportId );
        decimal totalVat = VatReportHelpers.Round2( combined.Details.Vat );
        string description = BuildVatPaymentDescription( periodMonth );
        DateOnly movementDate = new( periodYear, periodMonth, DateTime.DaysInMonth( periodYear, periodMonth ) );
        DateTime now = DateTime.UtcNow;

        if (link is not null)
        {
            FinanceMovement movement = link.FinanceMovement;
            movement.PersonId = personId;
            movement.Kind = FinanceMovementKind.Payment;
            if (!link.IsAmountLocked)
            {
                movement.Amount = totalVat;
            }
            movement.Description = description;
            movement.MovementDate = movementDate;
            movement.UpdatedAtUtc = now;
            await _db.SaveChangesAsync();
            return;
        }

        FinanceMovement created = new()
        {
            PersonId = personId,
            Kind = FinanceMovementKind.Payment,
            Amount = totalVat,
            Description = description,
            MovementDate = movementDate,
            IsFromRecurring = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.FinanceMovements.Add( created );
        await _db.SaveChangesAsync();

        _db.VatPeriodFinancePayments.Add(
            new VatPeriodFinancePayment
            {
                PeriodYear = periodYear,
                PeriodMonth = periodMonth,
                FinanceMovementId = created.Id
            }
        );
        await _db.SaveChangesAsync();
    }

    private async Task<int> ResolveBaseReportIdAsync( int periodYear, int periodMonth )
    {
        int? polandId = await _db.VatReports
            .AsNoTracking()
            .Where( r =>
                r.PeriodYear == periodYear &&
                r.PeriodMonth == periodMonth &&
                r.Type == VatReportType.Poland )
            .Select( r => (int?)r.Id )
            .FirstOrDefaultAsync();
        if (polandId.HasValue)
        {
            return polandId.Value;
        }

        return await _db.VatReports
            .AsNoTracking()
            .Where( r => r.PeriodYear == periodYear && r.PeriodMonth == periodMonth )
            .OrderByDescending( r => r.Id )
            .Select( r => r.Id )
            .FirstAsync();
    }

    private async Task RemoveLinkAsync( VatPeriodFinancePayment link )
    {
        _db.FinanceMovements.Remove( link.FinanceMovement );
        _db.VatPeriodFinancePayments.Remove( link );
        await _db.SaveChangesAsync();
    }

    public static string BuildVatPaymentDescription( int periodMonth )
    {
        string monthLabel = periodMonth >= 1 && periodMonth <= 12
            ? MonthNamesBe[periodMonth - 1]
            : $"месяц {periodMonth}";
        return $"VAT за {monthLabel}";
    }
}
