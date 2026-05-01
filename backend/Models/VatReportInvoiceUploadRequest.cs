using Microsoft.AspNetCore.Http;

namespace backend.Models
{
    public class VatReportInvoiceUploadRequest
    {
        public IFormFile? File { get; set; }
    }
}
