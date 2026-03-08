namespace SupplierStatementConsole.Models
{
    public class Invoice
    {
        public string InvoiceNumber { get; set; }
        public string InvoiceDate { get; set; }
        public string InvoiceDueDate { get; set; }
        public string CreditAmount { get; set; }
        public string DebitAmount { get; set; }
    }
}
