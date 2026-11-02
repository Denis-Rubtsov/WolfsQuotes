using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

// Тонкая обёртка над S3-совместимым хранилищем (Beget, Ceph) для одного приватного бакета.
class S3Storage
{
    private readonly AmazonS3Client _client;

    public string Bucket { get; }
    public string ServiceUrl { get; }

    public S3Storage(string serviceUrl, string bucket, string region, string accessKey, string secretKey)
    {
        ServiceUrl = serviceUrl.TrimEnd('/');
        Bucket = bucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = region,
            ForcePathStyle = true,
            // Новые контрольные суммы SDK v4 (CRC32 в трейлерах) S3-совместимые хранилища часто не понимают
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(30),
        });
    }

    // Адрес сервера из публичной ссылки на файл в бакете того же хранилища (BASIC_URL):
    // https://<бакет>.s3.ru1.storage.beget.cloud/... и https://s3.ru1.storage.beget.cloud/<бакет>/... → https://s3.ru1.storage.beget.cloud
    public static string? ServiceUrlFrom(string? publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        var s3 = host.IndexOf(".s3.", StringComparison.OrdinalIgnoreCase);
        if (!host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) && s3 > 0)
            host = host[(s3 + 1)..];

        return $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
    }

    // null, если объекта нет
    public async Task<string?> GetTextAsync(string key, CancellationToken ct = default)
    {
        var result = await GetIfChangedAsync(key, null, ct);
        return result.Found ? result.Text : null;
    }

    // Условный GET: при совпадении ETag тело не скачивается (NotModified = true)
    public async Task<(bool Found, bool NotModified, string? Text, string? ETag)> GetIfChangedAsync(string key, string? etag, CancellationToken ct = default)
    {
        try
        {
            var request = new GetObjectRequest { BucketName = Bucket, Key = key };
            if (etag != null) request.EtagToNotMatch = etag;

            using var response = await _client.GetObjectAsync(request, ct);
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            return (true, false, await reader.ReadToEndAsync(ct), response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotModified)
        {
            return (true, true, null, etag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (false, false, null, null);
        }
    }

    public Task PutTextAsync(string key, string text, string contentType, CancellationToken ct = default) =>
        _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentBody = text,
            ContentType = contentType,
            CannedACL = S3CannedACL.Private,
            UseChunkEncoding = false,
        }, ct);

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(Bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task CopyAsync(string from, string to, CancellationToken ct = default) =>
        _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = from,
            DestinationBucket = Bucket,
            DestinationKey = to,
            CannedACL = S3CannedACL.Private,
        }, ct);

    public async Task<List<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);

        return keys;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _client.DeleteObjectAsync(Bucket, key, ct);

    // Можно ли скачать объект без ключей (то есть бакет случайно публичный)
    public async Task<bool> IsPubliclyReadableAsync(string key)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head,
            $"{ServiceUrl}/{Uri.EscapeDataString(Bucket)}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}"));
        return response.IsSuccessStatusCode;
    }
}
