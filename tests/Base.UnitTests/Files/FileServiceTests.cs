using System.Linq.Expressions;
using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Domain.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Base.UnitTests.Files;

public sealed class FileServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IRepository<StoredFile> _repository = Substitute.For<IRepository<StoredFile>>();
    private readonly IFileStorageProvider _provider = Substitute.For<IFileStorageProvider>();
    private readonly IApplicationSettingService _settings = Substitute.For<IApplicationSettingService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly FileService _service;

    public FileServiceTests()
    {
        _unitOfWork.Repository<StoredFile>().Returns(_repository);
        _provider.Name.Returns("LocalDisk");

        var resolver = Substitute.For<IFileStorageProviderResolver>();
        resolver.Names.Returns(["LocalDisk"]);
        resolver.TryGet("LocalDisk", out Arg.Any<IFileStorageProvider>()!).Returns(x =>
        {
            x[1] = _provider;
            return true;
        });
        resolver.Get("LocalDisk").Returns(_provider);

        _settings.GetListAsync(SettingCodes.Files.AllowedExtensions, Arg.Any<CancellationToken>()).Returns([".pdf", ".png"]);
        _settings.GetIntAsync(SettingCodes.Files.MaxUploadSizeMb, Arg.Any<CancellationToken>()).Returns(1);
        _settings.GetIntAsync(SettingCodes.Files.DownloadLinkMinutes, Arg.Any<CancellationToken>()).Returns(5);
        _settings.GetAsync(SettingCodes.Files.DefaultProvider, Arg.Any<CancellationToken>()).Returns("LocalDisk");

        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(TenantId);
        _user.UserId.Returns(UserId);
        _user.IsAuthenticated.Returns(true);

        _service = new FileService(
            _unitOfWork, resolver, new DefaultStoredFileAccessPolicy(_user), _settings, tenant, _user,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)), NullLogger<FileService>.Instance);
    }

    [Fact]
    public async Task Valid_pdf_is_hashed_stored_and_recorded()
    {
        var content = "%PDF-1.7 hello"u8.ToArray();

        var result = await _service.UploadAsync(new FileUploadRequest(new MemoryStream(content), "..\\..\\report.pdf", "text/html", "invoice"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Message);
        result.Data!.FileName.ShouldBe("report.pdf");
        result.Data.ContentType.ShouldBe("application/pdf"); // the client's "text/html" is ignored
        result.Data.SizeBytes.ShouldBe(content.Length);
        result.Data.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));

        await _provider.Received(1).UploadAsync(
            Arg.Is<string>(k => k.StartsWith($"{TenantId:N}/invoice/2026/10/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)),
            Arg.Any<Stream>(), "application/pdf", Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAsync(Arg.Is<StoredFile>(f => f.OwnerUserId == UserId && f.ProviderName == "LocalDisk"), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("virus.exe", "MZ", "not allowed")]
    [InlineData("fake.pdf", "MZ\u0090", "does not match")]
    [InlineData("empty.pdf", "", "empty")]
    public async Task Invalid_uploads_are_rejected_before_storage(string name, string body, string expected)
    {
        var result = await _service.UploadAsync(new FileUploadRequest(new MemoryStream(Encoding.Latin1.GetBytes(body)), name, null, "invoice"), TestContext.Current.CancellationToken);

        result.ErrorType.ShouldBe(ErrorType.Validation);
        result.Message!.ShouldContain(expected);
        await _provider.DidNotReceiveWithAnyArgs().UploadAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Oversized_upload_is_rejected_while_streaming()
    {
        var big = new byte[(1024 * 1024) + 1];
        "%PDF-"u8.CopyTo(big);

        var result = await _service.UploadAsync(new FileUploadRequest(new MemoryStream(big), "big.pdf", null, "invoice"), TestContext.Current.CancellationToken);

        result.Message!.ShouldContain("larger than");
    }

    [Fact]
    public async Task Metadata_failure_deletes_the_orphaned_object()
    {
        _unitOfWork.SaveAsync(Arg.Any<CancellationToken>()).Returns<int>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.UploadAsync(new FileUploadRequest(new MemoryStream("%PDF-1"u8.ToArray()), "a.pdf", null, "invoice"), TestContext.Current.CancellationToken));

        await _provider.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_elses_file_looks_missing_without_permission()
    {
        var file = new StoredFile("LocalDisk", "k", "a.pdf", "application/pdf", 1, new string('0', 64), "invoice", Guid.NewGuid(), null);
        _repository.GetAsync(Arg.Any<Expression<Func<StoredFile, bool>>>(), Arg.Any<string[]?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(file);

        var result = await _service.GetDownloadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.ErrorType.ShouldBe(ErrorType.NotFound);
        await _provider.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default);
    }
}
