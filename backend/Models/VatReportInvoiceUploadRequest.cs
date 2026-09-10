using Microsoft.AspNetCore.Http;

namespace backend.Models
{
    public class VatReportInvoiceUploadRequest
    {
        public IFormFile? File { get; set; }
        /// <summary>Optional supplier hint for invoice product matching.</summary>
        public int? SupplierId { get; set; }
    }
}
