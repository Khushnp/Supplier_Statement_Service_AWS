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
                var outputJson = JsonConvert.SerializeObject(result, Formatting.Indented);

                Console.WriteLine();
                Console.WriteLine("Extraction Result:");
                Console.WriteLine(outputJson);

                var outputPath = Path.Combine(
                    Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
                    Path.GetFileNameWithoutExtension(inputPath) + ".json");

                File.WriteAllText(outputPath, outputJson);
                Console.WriteLine($"JSON saved to: {outputPath}");

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
