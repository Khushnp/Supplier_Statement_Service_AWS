using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SupplierStatementConsole.Models;

namespace SupplierStatementConsole.Services
{
    public class FileProcessor
    {
        private static readonly HashSet<string> PdfExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
        private static readonly HashSet<string> SpreadsheetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xls", ".csv" };

        private readonly ExcelToPdfConverter _converter;
        private readonly TextractService _textractService;
        private readonly TextractParser _parser;

        public FileProcessor(ExcelToPdfConverter converter, TextractService textractService, TextractParser parser)
        {
            _converter = converter;
            _textractService = textractService;
            _parser = parser;
        }

        public async Task<SupplierStatement> ProcessAsync(string inputPath)
        {
            var extension = Path.GetExtension(inputPath);
            var tempFiles = new List<string>();

            try
            {
                string processingPath;

                if (SpreadsheetExtensions.Contains(extension))
                {
                    processingPath = _converter.ConvertToPdf(inputPath);
                    tempFiles.Add(processingPath);
                }
                else if (PdfExtensions.Contains(extension) || ImageExtensions.Contains(extension))
                {
                    processingPath = inputPath;
                }
                else
                {
                    throw new NotSupportedException($"Unsupported file format: {extension}");
                }

                var response = await _textractService.AnalyzeDocumentAsync(processingPath).ConfigureAwait(false);
                return _parser.Parse(response);
            }
            finally
            {
                foreach (var tempFile in tempFiles)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch
                    {
                        // Intentionally swallow cleanup issues.
                    }
                }
            }
        }
    }
}
