using System.Collections.Generic;

namespace SupplierStatementConsole.Models
{
    public class SupplierStatement
    {
        public string SupplierName { get; set; }
        public string SupplierTaxRegistrationNumber { get; set; }
        public string SupplierEmail { get; set; }
        public string CustomerNumber { get; set; }
        public string CustomerName { get; set; }
        public List<Invoice> Invoices { get; set; } = new List<Invoice>();
    }
}
