using Amazon.S3;
using Amazon.S3.Model;
using FourSaas.AutoUpdater.Storage;
using FourSaas.AutoUpdater.Storage.S3;

namespace FourSaas.AutoUpdater.Storage.S3.Tests;

public sealed class MinioMultipartCopyTests
{
    private const long BytesPerKibibyte = 1024;
    private const long BytesPerMebibyte = BytesPerKibibyte * BytesPerKibibyte;
    private const long S3MinimumPartSize = 5 * BytesPerMebibyte;
    private const long ForceMultipartCopyLimit = 0;
    private const int MultipartPartCount = 2;
    private const int TrailingByteCount = 1;
    private const int FirstPartNumber = 1;
    private const byte OriginalGenerationByte = (byte)'A';
    private const byte MutatedGenerationByte = (byte)'B';
    private const string CompactGuidFormat = "N";
    private const string TestPrefixRoot = "integration/multipart-copy";
    private const string SourceObjectKey = "source";
    private const string DestinationObjectKey = "destination";
    private const string CopySourceBindingMetadata = "x-amz-meta-4sup-copy-source";
    private const string BindingFieldSeparator = "|";
    private const char ObjectKeySeparator = '/';

    private const long MultipartFixtureLength =
        (S3MinimumPartSize * MultipartPartCount) + TrailingByteCount;

    [Fact]
    public async Task SourceMutationDuringMultipartCopyAbortsWithoutDestination()
    {
        MinioIntegrationSettings.SkipUnlessEnabled();
        var original = CreateGeneration(OriginalGenerationByte);
        var mutated = CreateGeneration(MutatedGenerationByte);
        var prefix = UniquePrefix();
        using var client = new MutatingS3Client();
        await using var store = new ForcedMultipartS3ObjectStore(prefix, client);
        var source = new ObjectKey(SourceObjectKey);
        var destination = new ObjectKey(DestinationObjectKey);

        await store.PutAsync(source, new MemoryStream(original), original.LongLength);
        client.AfterFirstCopyPart = cancellationToken =>
            PutRawObjectAsync(client, FullKey(prefix, source), mutated, cancellationToken);

        try
        {
            await Assert.ThrowsAsync<AmazonS3Exception>(
                () => store.CopyAsync(source, destination, overwrite: true).AsTask());
            Assert.Null(await store.HeadAsync(destination));
            Assert.Null(await store.FindIncompleteMultipartUploadAsync(destination));
        }
        finally
        {
            await store.DeleteAsync(source);
            await store.DeleteAsync(destination);
        }
    }

    [Fact]
    public async Task ResumeRejectsUploadBoundToPreviousSourceGeneration()
    {
        MinioIntegrationSettings.SkipUnlessEnabled();
        var original = CreateGeneration(OriginalGenerationByte);
        var mutated = CreateGeneration(MutatedGenerationByte);
        var prefix = UniquePrefix();
        using var client = MinioIntegrationSettings.CreateClient();
        await using var store = new ForcedMultipartS3ObjectStore(prefix, client);
        var source = new ObjectKey(SourceObjectKey);
        var destination = new ObjectKey(DestinationObjectKey);
        string? staleUploadId = null;

        await store.PutAsync(source, new MemoryStream(original), original.LongLength);
        var sourceHead = await store.HeadAsync(source);
        Assert.NotNull(sourceHead);
        Assert.NotNull(sourceHead!.Validator);

        try
        {
            var initiateRequest = new InitiateMultipartUploadRequest
            {
                BucketName = MinioIntegrationSettings.Bucket,
                Key = FullKey(prefix, destination)
            };
            initiateRequest.Metadata.Add(
                CopySourceBindingMetadata,
                SourceBinding(prefix, source, sourceHead));
            var initiated = await client.InitiateMultipartUploadAsync(initiateRequest);
            staleUploadId = initiated.UploadId;
            await client.CopyPartAsync(new CopyPartRequest
            {
                SourceBucket = MinioIntegrationSettings.Bucket,
                SourceKey = FullKey(prefix, source),
                DestinationBucket = MinioIntegrationSettings.Bucket,
                DestinationKey = FullKey(prefix, destination),
                UploadId = staleUploadId,
                PartNumber = FirstPartNumber,
                FirstByte = 0,
                LastByte = S3MinimumPartSize - TrailingByteCount,
                ETagToMatch = [sourceHead.Validator.Value]
            });

            await store.PutAsync(source, new MemoryStream(mutated), mutated.LongLength);
            await store.CopyAsync(source, destination, overwrite: true);

            Assert.Equal(mutated, await ReadAllAsync(store, destination));
            Assert.Null(await store.FindIncompleteMultipartUploadAsync(destination));
            await Assert.ThrowsAsync<AmazonS3Exception>(() => client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = MinioIntegrationSettings.Bucket,
                Key = FullKey(prefix, destination),
                UploadId = staleUploadId
            }));
            staleUploadId = null;
        }
        finally
        {
            if (staleUploadId is not null)
                await TryAbortAsync(client, prefix, destination, staleUploadId);
            await store.DeleteAsync(source);
            await store.DeleteAsync(destination);
        }
    }

    private static byte[] CreateGeneration(byte value)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)MultipartFixtureLength));
        Array.Fill(bytes, value);
        return bytes;
    }

    private static string UniquePrefix()
        => $"{TestPrefixRoot}{ObjectKeySeparator}{Guid.NewGuid().ToString(CompactGuidFormat)}";

    private static string FullKey(string prefix, ObjectKey key)
        => $"{prefix}{ObjectKeySeparator}{key.Value}";

    private static string SourceBinding(string prefix, ObjectKey source, ObjectHead head)
        => $"{MinioIntegrationSettings.Bucket}{ObjectKeySeparator}{FullKey(prefix, source)}{BindingFieldSeparator}{head.Validator!.Value}{BindingFieldSeparator}{head.Length}";

    private static async Task PutRawObjectAsync(
        IAmazonS3 client,
        string key,
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = MinioIntegrationSettings.Bucket,
            Key = key,
            InputStream = stream
        }, cancellationToken);
    }

    private static async Task<byte[]> ReadAllAsync(S3ObjectStore store, ObjectKey key)
    {
        var read = await store.OpenAsync(key);
        Assert.NotNull(read);
        await using (read!)
        {
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output);
            return output.ToArray();
        }
    }

    private static async Task TryAbortAsync(
        IAmazonS3 client,
        string prefix,
        ObjectKey destination,
        string uploadId)
    {
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = MinioIntegrationSettings.Bucket,
                Key = FullKey(prefix, destination),
                UploadId = uploadId
            });
        }
        catch (AmazonS3Exception)
        {
        }
    }

    private sealed class ForcedMultipartS3ObjectStore(string prefix, IAmazonS3 client)
        : S3ObjectStore(
            MinioIntegrationSettings.Bucket,
            prefix,
            client,
            providerProfile: S3ProviderProfile.Minio)
    {
        protected override long SingleRequestCopyLimit => ForceMultipartCopyLimit;
        protected override long MultipartCopyPartSize => S3MinimumPartSize;
    }

    private sealed class MutatingS3Client()
        : AmazonS3Client(
            MinioIntegrationSettings.GetRequiredEnvironmentVariable(MinioIntegrationSettings.AccessKeyEnvironmentVariable),
            MinioIntegrationSettings.GetRequiredEnvironmentVariable(MinioIntegrationSettings.SecretKeyEnvironmentVariable),
            MinioIntegrationSettings.CreateClientConfiguration())
    {
        public Func<CancellationToken, Task>? AfterFirstCopyPart { get; set; }

        public override async Task<CopyPartResponse> CopyPartAsync(
            CopyPartRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = await base.CopyPartAsync(request, cancellationToken);
            if (request.PartNumber == FirstPartNumber && AfterFirstCopyPart is not null)
                await AfterFirstCopyPart(cancellationToken);
            return response;
        }
    }
}
