using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Files;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Files;

/// <summary>ClamAV through the real upload pipeline, against a fake clamd that speaks the INSTREAM protocol.</summary>
public sealed class UploadCheckTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly FakeClamd _clamd = new();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(
            sql.NewDatabase(),
            services => services.AddSingleton<ICurrentUser>(new TestCurrentUser().WithAllPermissions()),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["FileScanning:ClamAv:Host"] = "127.0.0.1",
                ["FileScanning:ClamAv:Port"] = _clamd.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["FileScanning:ClamAv:TimeoutSeconds"] = "5",
            });

    public async ValueTask DisposeAsync()
    {
        _clamd.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Clean_files_are_stored_after_the_whole_content_is_scanned()
    {
        var content = Pdf(new string('x', 200_000)); // several INSTREAM chunks

        (await UploadAsync(content)).IsSuccess.ShouldBeTrue();
        _clamd.LastScanned.ShouldBe(content);
    }

    [Fact]
    public async Task Infected_files_are_rejected()
    {
        var result = await UploadAsync(Pdf("EICAR-STANDARD-ANTIVIRUS-TEST-FILE"));

        result.IsSuccess.ShouldBeFalse();
        result.Message.ShouldBe("The file was rejected because it appears to contain malware.");
    }

    [Fact]
    public async Task Uploads_are_refused_when_clamd_is_down()
    {
        _clamd.Dispose();

        (await UploadAsync(Pdf("clean"))).Message.ShouldBe("The file could not be checked for viruses right now. Please try again later.");
    }

    private static byte[] Pdf(string body) => Encoding.ASCII.GetBytes("%PDF-1.7 " + body);

    private Task<Application.Common.Results.ValidationResponse<FileUploadResult>> UploadAsync(byte[] content) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IFileService>().UploadAsync(new FileUploadRequest(new MemoryStream(content), "scan.pdf", null, "test")));

    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeClamd()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public byte[]? LastScanned { get; private set; }

        public void Dispose()
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
            _listener.Stop();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var command = new byte["zINSTREAM\0".Length];
                    await stream.ReadExactlyAsync(command, _stop.Token);

                    using var scanned = new MemoryStream();
                    var length = new byte[4];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(length, _stop.Token);
                        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(length);
                        if (size == 0)
                        {
                            break;
                        }

                        var chunk = new byte[size];
                        await stream.ReadExactlyAsync(chunk, _stop.Token);
                        scanned.Write(chunk);
                    }

                    LastScanned = scanned.ToArray();
                    var reply = Encoding.ASCII.GetString(LastScanned).Contains("EICAR", StringComparison.Ordinal) ? "stream: Eicar-Test-Signature FOUND\0" : "stream: OK\0";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }
}
