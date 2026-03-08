using System;
using System.IO;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Textract;
using Amazon.Textract.Model;

namespace SupplierStatementConsole.Services
{
    public class TextractService
    {
        private readonly AmazonTextractClient _client;

        public TextractService()
        {
            var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
            var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_KEY");
            var region = Environment.GetEnvironmentVariable("AWS_REGION");

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(region))
            {
                throw new InvalidOperationException("AWS credentials are missing. Set AWS_ACCESS_KEY, AWS_SECRET_KEY, and AWS_REGION.");
            }

            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            _client = new AmazonTextractClient(credentials, RegionEndpoint.GetBySystemName(region));
        }

        public async Task<AnalyzeDocumentResponse> AnalyzeDocumentAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Document file not found.", filePath);
            }

            try
            {
                var bytes = File.ReadAllBytes(filePath);
                using (var stream = new MemoryStream(bytes))
                {
                    var request = new AnalyzeDocumentRequest
                    {
                        Document = new Document { Bytes = stream },
                        FeatureTypes = new System.Collections.Generic.List<string> { "TABLES", "FORMS" }
                    };

                    return await _client.AnalyzeDocumentAsync(request).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"AWS Textract request failed: {ex.Message}", ex);
            }
        }
    }
}
