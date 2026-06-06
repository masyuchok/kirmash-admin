using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportLockService
{
    private readonly AppDbContext _db;

    public VatReportLockService( AppDbContext db )
    {
        _db = db;
    }

    public async Task<List<VatReportListItem>> SetLockedAsync( int reportId, bool locked )
    {
        VatReport? anchor = await _db.VatReports.FirstOrDefaultAsync( r => r.Id == reportId );
        if (anchor is null)
        {
            throw new InvalidOperationException( "Справаздача не знойдзена." );
        }

        List<VatReport> periodReports = await _db.VatReports
            .Where( r => r.PeriodYear == anchor.PeriodYear && r.PeriodMonth == anchor.PeriodMonth )
            .ToListAsync();

        foreach (VatReport report in periodReports)
        {
            report.IsLocked = locked;
        }

        await _db.SaveChangesAsync();

        return periodReports
            .OrderByDescending( r => r.Id )
            .Select( MapListItem )
            .ToList();
    }

    public async Task EnsurePeriodUnlockedByReportIdAsync( int reportId )
    {
        bool locked = await _db.VatReports
            .AsNoTracking()
            .AnyAsync( r => r.Id == reportId && r.IsLocked );
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    public async Task EnsurePeriodUnlockedByRowIdAsync( int rowId )
    {
        bool locked = await _db.VatReportRows
            .AsNoTracking()
            .Where( r => r.Id == rowId )
            .Select( r => r.VatReport.IsLocked )
            .FirstOrDefaultAsync();
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    public async Task EnsurePeriodUnlockedByRowItemIdAsync( int itemId )
    {
        bool locked = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( i => i.Id == itemId )
            .Select( i => i.VatReportRow.VatReport.IsLocked )
            .FirstOrDefaultAsync();
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    public async Task EnsurePeriodUnlockedByExpenseIdAsync( int expenseId )
    {
        bool locked = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( e => e.Id == expenseId )
            .Select( e => e.VatReport.IsLocked )
            .FirstOrDefaultAsync();
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    public async Task EnsurePeriodUnlockedByCashSaleIdAsync( int cashSaleId )
    {
        bool locked = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( c => c.Id == cashSaleId )
            .Select( c => c.VatReport.IsLocked )
            .FirstOrDefaultAsync();
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    public async Task EnsurePeriodUnlockedAsync( int periodYear, int periodMonth )
    {
        bool locked = await _db.VatReports
            .AsNoTracking()
            .AnyAsync( r => r.PeriodYear == periodYear && r.PeriodMonth == periodMonth && r.IsLocked );
        if (locked)
        {
            throw new InvalidOperationException( VatReportLockGuard.LockedMessage );
        }
    }

    private static VatReportListItem MapListItem( VatReport report ) =>
        new()
        {
            Id = report.Id,
            PeriodYear = report.PeriodYear,
            PeriodMonth = report.PeriodMonth,
            Type = report.Type,
            Name = report.Name,
            Document = report.Document,
            Vat = report.Vat,
            VatCredit = report.VatCredit,
            VatToPay = report.VatToPay,
            Documents = report.Documents.ToList(),
            ShopifyOrderIds = report.ShopifyOrderIds.ToList(),
            IsLocked = report.IsLocked,
        };
}
