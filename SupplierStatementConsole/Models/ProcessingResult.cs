using Amazon.Textract.Model;

namespace SupplierStatementConsole.Models
{
    public class ProcessingResult
    {
        public SupplierStatement ParsedStatement { get; set; }
        public AnalyzeDocumentResponse RawTextractResponse { get; set; }
    }
}
