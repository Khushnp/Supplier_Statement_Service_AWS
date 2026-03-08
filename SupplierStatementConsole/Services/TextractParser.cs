using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Amazon.Textract.Model;
using SupplierStatementConsole.Models;

namespace SupplierStatementConsole.Services
{
    public class TextractParser
    {
        private static readonly string[] InvoiceNumberKeys = { "invoice", "invoice no", "invoice number", "inv #", "inv no" };
        private static readonly string[] InvoiceDateKeys = { "invoice date", "date" };
        private static readonly string[] DueDateKeys = { "due date", "payment due" };
        private static readonly string[] CreditKeys = { "credit", "cr amount", "credit amount" };
        private static readonly string[] DebitKeys = { "debit", "dr amount", "debit amount" };

        public SupplierStatement Parse(AnalyzeDocumentResponse response)
        {
            if (response?.Blocks == null || response.Blocks.Count == 0)
            {
                throw new InvalidOperationException("No Textract blocks were returned to parse.");
            }

            var statement = new SupplierStatement();
            var blocksById = response.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Id)).ToDictionary(b => b.Id, b => b);

            var keyValueMap = ParseKeyValuePairs(response.Blocks, blocksById);
            PopulateSupplierCustomerFields(statement, keyValueMap);

            var invoices = ExtractInvoicesFromTables(response.Blocks, blocksById);
            if (invoices.Count == 0)
            {
                invoices = ExtractInvoiceFromLines(response.Blocks);
            }

            statement.Invoices = invoices;
            return statement;
        }

        private static Dictionary<string, string> ParseKeyValuePairs(List<Block> blocks, Dictionary<string, Block> blocksById)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keyBlocks = blocks.Where(b => b.BlockType == BlockType.KEY_VALUE_SET && b.EntityTypes.Contains("KEY"));

            foreach (var keyBlock in keyBlocks)
            {
                var key = GetTextFromBlock(keyBlock, blocksById);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var value = string.Empty;
                var valueRel = keyBlock.Relationships?.FirstOrDefault(r => r.Type == RelationshipType.VALUE);
                if (valueRel != null)
                {
                    foreach (var valueId in valueRel.Ids)
                    {
                        if (blocksById.TryGetValue(valueId, out var valueBlock))
                        {
                            value = GetTextFromBlock(valueBlock, blocksById);
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                break;
                            }
                        }
                    }
                }

                if (!result.ContainsKey(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private static void PopulateSupplierCustomerFields(SupplierStatement statement, Dictionary<string, string> keyValueMap)
        {
            statement.SupplierName = FindByAliases(keyValueMap, "supplier name", "vendor name", "from");
            statement.SupplierTaxRegistrationNumber = FindByAliases(keyValueMap, "supplier tax registration number", "vat", "tax registration", "tax no");
            statement.SupplierEmail = FindByAliases(keyValueMap, "supplier email", "email", "e-mail");
            statement.CustomerNumber = FindByAliases(keyValueMap, "customer number", "customer no", "account number", "account no");
            statement.CustomerName = FindByAliases(keyValueMap, "customer name", "bill to", "customer");
        }

        private static string FindByAliases(Dictionary<string, string> keyValueMap, params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var key = keyValueMap.Keys.FirstOrDefault(k => Normalize(k).Contains(Normalize(alias)));
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(keyValueMap[key]))
                {
                    return keyValueMap[key];
                }
            }

            return string.Empty;
        }

        private static List<Invoice> ExtractInvoicesFromTables(List<Block> blocks, Dictionary<string, Block> blocksById)
        {
            var invoices = new List<Invoice>();
            var tables = blocks.Where(b => b.BlockType == BlockType.TABLE).ToList();

            foreach (var table in tables)
            {
                var tableCells = new List<Block>();
                var childRel = table.Relationships?.FirstOrDefault(r => r.Type == RelationshipType.CHILD);
                if (childRel == null)
                {
                    continue;
                }

                foreach (var cellId in childRel.Ids)
                {
                    if (blocksById.TryGetValue(cellId, out var cell) && cell.BlockType == BlockType.CELL)
                    {
                        tableCells.Add(cell);
                    }
                }

                if (tableCells.Count == 0)
                {
                    continue;
                }

                var headers = tableCells
                    .Where(c => c.RowIndex == 1)
                    .OrderBy(c => c.ColumnIndex)
                    .ToDictionary(c => c.ColumnIndex, c => Normalize(GetTextFromBlock(c, blocksById)));

                var maxRow = tableCells.Max(c => c.RowIndex);
                for (var row = 2; row <= maxRow; row++)
                {
                    var invoice = new Invoice();
                    var rowCells = tableCells.Where(c => c.RowIndex == row).ToList();

                    foreach (var cell in rowCells)
                    {
                        if (!headers.TryGetValue(cell.ColumnIndex, out var header))
                        {
                            continue;
                        }

                        var value = GetTextFromBlock(cell, blocksById);
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        if (ContainsAny(header, InvoiceNumberKeys)) invoice.InvoiceNumber = value;
                        else if (ContainsAny(header, InvoiceDateKeys)) invoice.InvoiceDate = NormalizeDate(value);
                        else if (ContainsAny(header, DueDateKeys)) invoice.InvoiceDueDate = NormalizeDate(value);
                        else if (ContainsAny(header, CreditKeys)) invoice.CreditAmount = value;
                        else if (ContainsAny(header, DebitKeys)) invoice.DebitAmount = value;
                    }

                    if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber) ||
                        !string.IsNullOrWhiteSpace(invoice.InvoiceDate) ||
                        !string.IsNullOrWhiteSpace(invoice.CreditAmount) ||
                        !string.IsNullOrWhiteSpace(invoice.DebitAmount))
                    {
                        invoices.Add(invoice);
                    }
                }
            }

            return invoices;
        }

        private static List<Invoice> ExtractInvoiceFromLines(List<Block> blocks)
        {
            var lines = blocks.Where(b => b.BlockType == BlockType.LINE).Select(b => b.Text ?? string.Empty).ToList();
            var invoice = new Invoice
            {
                InvoiceNumber = ExtractAfterLabel(lines, InvoiceNumberKeys),
                InvoiceDate = NormalizeDate(ExtractAfterLabel(lines, InvoiceDateKeys)),
                InvoiceDueDate = NormalizeDate(ExtractAfterLabel(lines, DueDateKeys)),
                CreditAmount = ExtractAmount(lines, CreditKeys),
                DebitAmount = ExtractAmount(lines, DebitKeys)
            };

            return string.IsNullOrWhiteSpace(invoice.InvoiceNumber) &&
                   string.IsNullOrWhiteSpace(invoice.InvoiceDate) &&
                   string.IsNullOrWhiteSpace(invoice.InvoiceDueDate) &&
                   string.IsNullOrWhiteSpace(invoice.CreditAmount) &&
                   string.IsNullOrWhiteSpace(invoice.DebitAmount)
                ? new List<Invoice>()
                : new List<Invoice> { invoice };
        }

        private static string ExtractAfterLabel(IEnumerable<string> lines, IEnumerable<string> labels)
        {
            foreach (var line in lines)
            {
                foreach (var label in labels)
                {
                    var pattern = $@"{Regex.Escape(label)}\s*[:#-]?\s*(.+)$";
                    var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return match.Groups[1].Value.Trim();
                    }
                }
            }

            return string.Empty;
        }

        private static string ExtractAmount(IEnumerable<string> lines, IEnumerable<string> labels)
        {
            var extracted = ExtractAfterLabel(lines, labels);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }

            foreach (var line in lines)
            {
                if (labels.Any(l => Normalize(line).Contains(Normalize(l))))
                {
                    var amountMatch = Regex.Match(line, "[-+]?\\d{1,3}(,\\d{3})*(\\.\\d{2})?|[-+]?\\d+(\\.\\d{2})?");
                    if (amountMatch.Success)
                    {
                        return amountMatch.Value;
                    }
                }
            }

            return string.Empty;
        }

        private static string GetTextFromBlock(Block block, Dictionary<string, Block> blocksById)
        {
            if (!string.IsNullOrWhiteSpace(block.Text))
            {
                return block.Text.Trim();
            }

            var words = new List<string>();
            var childRel = block.Relationships?.FirstOrDefault(r => r.Type == RelationshipType.CHILD);
            if (childRel == null)
            {
                return string.Empty;
            }

            foreach (var childId in childRel.Ids)
            {
                if (!blocksById.TryGetValue(childId, out var child))
                {
                    continue;
                }

                if (child.BlockType == BlockType.WORD)
                {
                    words.Add(child.Text);
                }
                else if (child.BlockType == BlockType.SELECTION_ELEMENT && child.SelectionStatus == SelectionStatus.SELECTED)
                {
                    words.Add("X");
                }
            }

            return string.Join(" ", words).Trim();
        }

        private static string Normalize(string input)
        {
            return (input ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool ContainsAny(string value, IEnumerable<string> candidates)
        {
            return candidates.Any(c => Normalize(value).Contains(Normalize(c)));
        }

        private static string NormalizeDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var formats = new[] { "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "MM-dd-yyyy" };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(raw, out parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return raw;
        }
    }
}
