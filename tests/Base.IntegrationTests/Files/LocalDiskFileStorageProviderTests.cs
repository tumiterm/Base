using Base.Infrastructure.Files;

namespace Base.IntegrationTests.Files;

public sealed class LocalDiskFileStorageProviderTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "base-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("tenant/../../outside.txt")]
    [InlineData("/etc/passwd")]
    public async Task Keys_cannot_escape_the_storage_root(string objectKey)
    {
        var provider = new LocalDiskFileStorageProvider(_root);

        await Should.ThrowAsync<ArgumentException>(() => provider.ExistsAsync(objectKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Objects_round_trip_under_the_root()
    {
        var provider = new LocalDiskFileStorageProvider(_root);
        await using (var content = new MemoryStream("hello"u8.ToArray()))
        {
            await provider.UploadAsync("t/invoice/2026/10/a.txt", content, "text/plain", TestContext.Current.CancellationToken);
        }

        await using var read = await provider.OpenReadAsync("t/invoice/2026/10/a.txt", TestContext.Current.CancellationToken);
        using var reader = new StreamReader(read);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("hello");
        File.Exists(Path.Join(_root, "t", "invoice", "2026", "10", "a.txt")).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
