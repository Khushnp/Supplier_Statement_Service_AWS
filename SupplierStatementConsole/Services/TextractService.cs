using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Textract;
using Amazon.Textract.Model;

namespace SupplierStatementConsole.Services
{
    public class TextractService
    {
        private readonly AmazonTextractClient _client;
        private readonly AmazonS3Client _s3Client;
        private readonly string _fallbackBucket;

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
            var regionEndpoint = RegionEndpoint.GetBySystemName(region);
            _client = new AmazonTextractClient(credentials, regionEndpoint);
            _s3Client = new AmazonS3Client(credentials, regionEndpoint);
            _fallbackBucket = Environment.GetEnvironmentVariable("AWS_TEXTRACT_S3_BUCKET") ?? string.Empty;
        }

        public async Task<AnalyzeDocumentResponse> AnalyzeDocumentAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Document file not found.", filePath);
            }

            var extension = Path.GetExtension(filePath) ?? string.Empty;
            var isPdfOrTiff = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                              || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                              || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);

            try
            {
                // First attempt sync AnalyzeDocument(Bytes) for all formats.
                // This keeps PDF/TIFF support working when the document is compatible
                // and no S3 bucket is configured.
                return await AnalyzeWithBytesAsync(filePath).ConfigureAwait(false);
            }
            catch (AmazonTextractException textractEx) when (isPdfOrTiff && IsUnsupportedDocumentError(textractEx))
            {
                // Only fallback to async S3-based flow for PDF/TIFF if we hit unsupported format.
                if (string.IsNullOrWhiteSpace(_fallbackBucket))
                {
                    throw new InvalidOperationException(
                        "Textract reported unsupported PDF/TIFF format for AnalyzeDocument(Bytes). " +
                        "Set AWS_TEXTRACT_S3_BUCKET (same region as AWS_REGION) to enable async fallback via StartDocumentAnalysis.",
                        textractEx);
                }

                return await AnalyzeViaStartDocumentAnalysisAsync(filePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"AWS Textract request failed: {ex.Message}", ex);
            }
        }

        private async Task<AnalyzeDocumentResponse> AnalyzeWithBytesAsync(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            using (var stream = new MemoryStream(bytes))
            {
                var request = new AnalyzeDocumentRequest
                {
                    Document = new Document { Bytes = stream },
                    FeatureTypes = new List<string> { "TABLES", "FORMS" }
                };

                return await _client.AnalyzeDocumentAsync(request).ConfigureAwait(false);
            }
        }

        private static bool IsUnsupportedDocumentError(AmazonTextractException ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (string.Equals(ex.ErrorCode, "UnsupportedDocumentException", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (ex.Message ?? string.Empty).IndexOf("unsupported document format", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<AnalyzeDocumentResponse> AnalyzeViaStartDocumentAnalysisAsync(string filePath)
        {
            var objectKey = $"textract-input/{Guid.NewGuid():N}_{Path.GetFileName(filePath)}";
            try
            {
                await _s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _fallbackBucket,
                    Key = objectKey,
                    FilePath = filePath
                }).ConfigureAwait(false);

                var startResponse = await _client.StartDocumentAnalysisAsync(new StartDocumentAnalysisRequest
                {
                    FeatureTypes = new List<string> { "TABLES", "FORMS" },
                    DocumentLocation = new DocumentLocation
                    {
                        S3Object = new Amazon.Textract.Model.S3Object
                        {
                            Bucket = _fallbackBucket,
                            Name = objectKey
                        }
                    }
                }).ConfigureAwait(false);

                var allBlocks = new List<Block>();
                string nextToken = null;

                while (true)
                {
                    await Task.Delay(1500).ConfigureAwait(false);

                    var getResponse = await _client.GetDocumentAnalysisAsync(new GetDocumentAnalysisRequest
                    {
                        JobId = startResponse.JobId,
                        NextToken = nextToken
                    }).ConfigureAwait(false);

                    if (getResponse.JobStatus == JobStatus.FAILED)
                    {
                        throw new InvalidOperationException($"Textract async job failed: {getResponse.StatusMessage}");
                    }

                    if (getResponse.JobStatus == JobStatus.PARTIAL_SUCCESS)
                    {
                        allBlocks.AddRange(getResponse.Blocks);
                        if (string.IsNullOrWhiteSpace(getResponse.NextToken))
                        {
                            break;
                        }

                        nextToken = getResponse.NextToken;
                        continue;
                    }

                    if (getResponse.JobStatus != JobStatus.SUCCEEDED)
                    {
                        continue;
                    }

                    allBlocks.AddRange(getResponse.Blocks);
                    if (string.IsNullOrWhiteSpace(getResponse.NextToken))
                    {
                        break;
                    }

                    nextToken = getResponse.NextToken;
                }

                return new AnalyzeDocumentResponse
                {
                    Blocks = allBlocks
                };
            }
            finally
            {
                try
                {
                    await _s3Client.DeleteObjectAsync(_fallbackBucket, objectKey).ConfigureAwait(false);
                }
                catch
                {
                    // Do not fail extraction because cleanup failed.
                }
            }
        }
    }
}
