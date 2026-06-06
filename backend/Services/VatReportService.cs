using backend.Models;

namespace backend.Services;

public class VatReportService
{
    private readonly VatReportQueryService _query;
    private readonly VatReportGenerationService _generation;
    private readonly VatReportMutationService _mutations;
    private readonly VatReportLockService _locks;

    public VatReportService(
        VatReportQueryService query,
        VatReportGenerationService generation,
        VatReportMutationService mutations,
        VatReportLockService locks )
    {
        _query = query;
        _generation = generation;
        _mutations = mutations;
        _locks = locks;
    }

    public Task<List<VatReportListItem>> GetAllAsync() => _query.GetAllAsync();

    public Task<List<VatReportPeriodListItem>> GetPeriodSummariesAsync() => _query.GetPeriodSummariesAsync();

    public Task<VatReportDetailsResponse> GetDetailsAsync( int id ) => _query.GetDetailsAsync( id );

    public Task<VatReportCombinedDetailsResponse> GetCombinedDetailsAsync( int id ) =>
        _query.GetCombinedDetailsAsync( id );

    public Task<VatReportListItem> GenerateAsync( int periodYear, int periodMonth, string reportType ) =>
        _generation.GenerateAsync( periodYear, periodMonth, reportType );

    public Task<VatReportListItem> RegenerateAsync( int id ) => _generation.RegenerateAsync( id );

    public Task<List<VatReportListItem>> SetLockedAsync( int reportId, bool locked ) =>
        _locks.SetLockedAsync( reportId, locked );

    public Task<List<VatReportSourceOrderOption>> GetSourceOrderOptionsAsync( int reportId ) =>
        _generation.GetSourceOrderOptionsAsync( reportId );

    public Task MoveRowToForeignAsync( int rowId, string deliveryName, string deliveryAddress ) =>
        _mutations.MoveRowToForeignAsync( rowId, deliveryName, deliveryAddress );

    public Task UpdateRowAsync(
        int rowId,
        decimal vatRatePercent,
        decimal grossAmount,
        decimal vatAmount,
        decimal netAmount,
        decimal? shippingGrossAmount = null ) =>
        _mutations.UpdateRowAsync( rowId, vatRatePercent, grossAmount, vatAmount, netAmount, shippingGrossAmount );

    public Task UpdateRowItemVatAsync( int itemId, decimal vatRatePercent ) =>
        _mutations.UpdateRowItemVatAsync( itemId, vatRatePercent );

    public Task AddRowAsync( int reportId, VatReportRowCreateRequest request ) =>
        _mutations.AddRowAsync( reportId, request );

    public Task DeleteRowAsync( int rowId ) => _mutations.DeleteRowAsync( rowId );

    public Task<int> AddExpenseAsync( int reportId, VatReportExpenseCreateRequest request ) =>
        _mutations.AddExpenseAsync( reportId, request );

    public Task UpdateExpenseAsync( int expenseId, VatReportExpenseCreateRequest request ) =>
        _mutations.UpdateExpenseAsync( expenseId, request );

    public Task UploadExpenseInvoiceAsync( int expenseId, string fileName, string contentType, byte[] data ) =>
        _mutations.UploadExpenseInvoiceAsync( expenseId, fileName, contentType, data );

    public Task<(string FileName, string ContentType, byte[] Data)> GetExpenseInvoiceAsync( int expenseId ) =>
        _mutations.GetExpenseInvoiceAsync( expenseId );

    public Task DeleteExpenseAsync( int expenseId ) => _mutations.DeleteExpenseAsync( expenseId );

    public Task<int> AddCashSaleAsync( int reportId, VatReportCashSaleCreateRequest request ) =>
        _mutations.AddCashSaleAsync( reportId, request );

    public Task DeleteCashSaleAsync( int cashSaleId ) => _mutations.DeleteCashSaleAsync( cashSaleId );

    public Task<string> AddForeignRowAsync( int reportId, VatReportForeignRowCreateRequest request ) =>
        _mutations.AddForeignRowAsync( reportId, request );

    public Task UploadRowInvoiceAsync( int rowId, string fileName, string contentType, byte[] data ) =>
        _mutations.UploadRowInvoiceAsync( rowId, fileName, contentType, data );

    public Task<(string FileName, string ContentType, byte[] Data)> GetRowInvoiceAsync( int rowId ) =>
        _mutations.GetRowInvoiceAsync( rowId );
}
