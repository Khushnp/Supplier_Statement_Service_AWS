using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;

namespace SupplierStatementConsole.Services
{
    public class ExcelToPdfConverter
    {
        public string ConvertToPdf(string inputPath)
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Spreadsheet/CSV input file not found.", inputPath);
            }

            var extension = Path.GetExtension(inputPath);
            var outputPath = Path.Combine(Path.GetTempPath(), $"textract_{Guid.NewGuid():N}.pdf");

            try
            {
                if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryConvertWithExcelInterop(inputPath, outputPath))
                    {
                        return outputPath;
                    }
                }

                ConvertWithOpenSourceLibraries(inputPath, outputPath, extension);
                return outputPath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to convert spreadsheet to PDF. {ex.Message}", ex);
            }
        }

        private static bool TryConvertWithExcelInterop(string inputPath, string outputPath)
        {
            Type excelType = null;
            object excelApp = null;
            object workbook = null;

            try
            {
                excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null)
                {
                    return false;
                }

                excelApp = Activator.CreateInstance(excelType);
                excelType.InvokeMember("Visible", System.Reflection.BindingFlags.SetProperty, null, excelApp, new object[] { false });

                var workbooks = excelType.InvokeMember("Workbooks", System.Reflection.BindingFlags.GetProperty, null, excelApp, null);
                workbook = workbooks.GetType().InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, workbooks, new object[] { inputPath });

                // 0 = xlTypePDF
                workbook.GetType().InvokeMember("ExportAsFixedFormat", System.Reflection.BindingFlags.InvokeMethod, null, workbook, new object[] { 0, outputPath });

                return File.Exists(outputPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    if (workbook != null)
                    {
                        workbook.GetType().InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, workbook, new object[] { false });
                    }

                    if (excelApp != null)
                    {
                        excelType?.InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, excelApp, null);
                    }
                }
                catch
                {
                    // No-op.
                }
            }
        }

        private static void ConvertWithOpenSourceLibraries(string inputPath, string outputPath, string extension)
        {
            var rows = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
                ? ReadCsvRows(inputPath)
                : ReadExcelRows(inputPath, extension);

            if (rows.Count == 0)
            {
                throw new InvalidOperationException("No content available for PDF conversion.");
            }

            var maxColumns = rows.Max(r => r.Count);
            using (var writer = new PdfWriter(outputPath))
            using (var pdf = new PdfDocument(writer))
            using (var doc = new Document(pdf))
            {
                doc.Add(new Paragraph($"Converted from {Path.GetFileName(inputPath)}")
                    .SetFontSize(10));

                var table = new Table(maxColumns).UseAllAvailableWidth();
                foreach (var row in rows)
                {
                    for (var i = 0; i < maxColumns; i++)
                    {
                        var value = i < row.Count ? row[i] : string.Empty;
                        table.AddCell(new Cell().Add(new Paragraph(value ?? string.Empty).SetFontSize(8)));
                    }
                }

                doc.Add(table);
            }
        }

        private static List<List<string>> ReadExcelRows(string inputPath, string extension)
        {
            if (string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(".xls conversion requires Microsoft Excel Interop on this machine.");
            }

            using (var workbook = new XLWorkbook(inputPath))
            {
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null || worksheet.LastRowUsed() == null)
                {
                    return new List<List<string>>();
                }

                var range = worksheet.RangeUsed();
                var rows = new List<List<string>>();
                foreach (var row in range.Rows())
                {
                    rows.Add(row.Cells().Select(c => c.GetFormattedString(CultureInfo.InvariantCulture)).ToList());
                }

                return rows;
            }
        }

        private static List<List<string>> ReadCsvRows(string inputPath)
        {
            var rows = new List<List<string>>();
            foreach (var line in File.ReadAllLines(inputPath))
            {
                rows.Add(ParseCsvLine(line));
            }

            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null)
            {
                return result;
            }

            var current = string.Empty;
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current += '"';
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    result.Add(current);
                    current = string.Empty;
                }
                else
                {
                    current += ch;
                }
            }

            result.Add(current);
            return result;
        }
    }
}
