using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Amazon.Textract;
using Amazon.Textract.Model;

namespace SupplierStatementConsole.Services
{
    public class TextractService
    {
        private const string DefaultRegionSystemName = "eu-west-2";

        private readonly AmazonTextractClient _client;
        private readonly AmazonS3Client _s3Client;
        private readonly string _configuredFallbackBucket;
        private readonly string _regionSystemName;
        private readonly string _accessKey;

        public TextractService()
        {
            var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
            var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_KEY");
            var region = Environment.GetEnvironmentVariable("AWS_REGION");

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                throw new InvalidOperationException("AWS credentials are missing. Set AWS_ACCESS_KEY and AWS_SECRET_KEY.");
            }

            var effectiveRegion = string.IsNullOrWhiteSpace(region) ? DefaultRegionSystemName : region;

            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            var regionEndpoint = RegionEndpoint.GetBySystemName(effectiveRegion);
            _client = new AmazonTextractClient(credentials, regionEndpoint);
            _s3Client = new AmazonS3Client(credentials, regionEndpoint);
            _configuredFallbackBucket = Environment.GetEnvironmentVariable("AWS_TEXTRACT_S3_BUCKET") ?? string.Empty;
            _regionSystemName = effectiveRegion;
            _accessKey = accessKey;
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
                return await AnalyzeWithBytesAsync(filePath).ConfigureAwait(false);
            }
            catch (AmazonTextractException textractEx) when (isPdfOrTiff && IsUnsupportedDocumentError(textractEx))
            {
                var fallbackBucket = await ResolveFallbackBucketAsync().ConfigureAwait(false);
                return await AnalyzeViaStartDocumentAnalysisAsync(filePath, fallbackBucket).ConfigureAwait(false);
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

        private async Task<string> ResolveFallbackBucketAsync()
        {
            if (!string.IsNullOrWhiteSpace(_configuredFallbackBucket))
            {
                return _configuredFallbackBucket;
            }

            var generatedName = GenerateDefaultBucketName();
            try
            {
                if (!await AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, generatedName).ConfigureAwait(false))
                {
                    var createRequest = new PutBucketRequest
                    {
                        BucketName = generatedName,
                        UseClientRegion = true
                    };

                    // us-east-1 expects no explicit location configuration.
                    if (!string.Equals(_regionSystemName, "us-east-1", StringComparison.OrdinalIgnoreCase))
                    {
                        createRequest.BucketRegionName = _regionSystemName;
                    }

                    await _s3Client.PutBucketAsync(createRequest).ConfigureAwait(false);
                }

                return generatedName;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Textract reported unsupported PDF/TIFF format for AnalyzeDocument(Bytes). " +
                    "Async fallback requires S3. Set AWS_TEXTRACT_S3_BUCKET to an existing bucket " +
                    "(same region as AWS_REGION) or grant permissions to create/use bucket '" + generatedName + "'.",
                    ex);
            }
        }

        private string GenerateDefaultBucketName()
        {
            var sanitizedKey = (_accessKey ?? string.Empty).ToLowerInvariant();
            if (sanitizedKey.Length > 12)
            {
                sanitizedKey = sanitizedKey.Substring(sanitizedKey.Length - 12);
            }

            var seed = string.IsNullOrWhiteSpace(sanitizedKey) ? Guid.NewGuid().ToString("N") : sanitizedKey;
            var sb = new StringBuilder();
            foreach (var ch in seed)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                }
            }

            var suffix = sb.ToString();
            if (suffix.Length < 8)
            {
                suffix = (suffix + Guid.NewGuid().ToString("N")).Substring(0, 8);
            }

            return $"supplier-statement-textract-{_regionSystemName}-{suffix}";
        }

        private async Task<AnalyzeDocumentResponse> AnalyzeViaStartDocumentAnalysisAsync(string filePath, string bucketName)
        {
            var objectKey = $"textract-input/{Guid.NewGuid():N}_{Path.GetFileName(filePath)}";
            try
            {
                await _s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucketName,
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
                            Bucket = bucketName,
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
                    await _s3Client.DeleteObjectAsync(bucketName, objectKey).ConfigureAwait(false);
                }
                catch
                {
                    // Do not fail extraction because cleanup failed.
                }
            }
        }
    }
}
