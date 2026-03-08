using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SupplierStatementConsole.Services;

namespace SupplierStatementConsole
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            Console.WriteLine("Enter supplier statement file path:");
            var inputPath = Console.ReadLine()?.Trim('"', ' ');

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.Error.WriteLine("No file path provided.");
                return 1;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"File does not exist: {inputPath}");
                return 1;
            }

            try
            {
                var textractService = new TextractService();
                var parser = new TextractParser();
                var converter = new ExcelToPdfConverter();
                var processor = new FileProcessor(converter, textractService, parser);

                var result = await processor.ProcessAsync(inputPath).ConfigureAwait(false);
                var parsedOutputJson = JsonConvert.SerializeObject(result.ParsedStatement, Formatting.Indented);
                var rawOutputJson = JsonConvert.SerializeObject(result.RawTextractResponse, Formatting.Indented);

                Console.WriteLine();
                Console.WriteLine("Extraction Result:");
                Console.WriteLine(parsedOutputJson);

                var outputDirectory = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
                var baseName = Path.GetFileNameWithoutExtension(inputPath);
                var parsedOutputPath = Path.Combine(outputDirectory, baseName + ".json");
                var rawOutputPath = Path.Combine(outputDirectory, baseName + ".textract-raw.json");

                File.WriteAllText(parsedOutputPath, parsedOutputJson);
                File.WriteAllText(rawOutputPath, rawOutputJson);

                Console.WriteLine($"Parsed JSON saved to: {parsedOutputPath}");
                Console.WriteLine($"Raw Textract JSON saved to: {rawOutputPath}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Processing failed.");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
