using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

// Интеграционные тесты S3 идут против эмулятора (moto) по адресу из VLK_TEST_S3_URL, например
//   pip install "moto[server]" && moto_server -p 5000
//   VLK_TEST_S3_URL=http://127.0.0.1:5000 dotnet test
// Без переменной тесты пропускаются; в CI выставлен VLK_TEST_S3_REQUIRED=1, и там пропуск — ошибка.
public sealed class S3Fixture : IDisposable
{
    public const string AccessKey = "test";
    public const string SecretKey = "test";
    public const string Region = "us-east-1";

    public string? Url { get; } = Environment.GetEnvironmentVariable("VLK_TEST_S3_URL") is { Length: > 0 } url ? url : null;

    private static bool Required => Environment.GetEnvironmentVariable("VLK_TEST_S3_REQUIRED") == "1";

    private readonly AmazonS3Client? _admin;

    public S3Fixture()
    {
        if (Url == null) return;

        _admin = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
        {
            ServiceURL = Url,
            AuthenticationRegion = Region,
            ForcePathStyle = true,
        });
    }

    public AmazonS3Client Admin => _admin!;

    public void Require()
    {
        if (Url != null) return;
        if (Required) Assert.Fail("VLK_TEST_S3_REQUIRED=1, но VLK_TEST_S3_URL не задан");
        Assert.Skip("VLK_TEST_S3_URL не задан — S3-тесты пропущены");
    }

    // Отдельный пустой бакет на каждый тест
    internal async Task<S3Storage> NewBucketAsync()
    {
        Require();
        var bucket = "vlk-test-" + Guid.NewGuid().ToString("N")[..12];
        await Admin.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        return new S3Storage(Url!, bucket, Region, AccessKey, SecretKey);
    }

    public void Dispose() => _admin?.Dispose();
}

[CollectionDefinition("S3")]
public class S3Collection : ICollectionFixture<S3Fixture>;
