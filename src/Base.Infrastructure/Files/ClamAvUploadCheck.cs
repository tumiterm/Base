using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Text;
using Base.Application.Common.Results;
using Base.Application.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Files;

/// <summary>ClamAV daemon (clamd) connection. Registered as an upload check only when <see cref="Host"/> is set.</summary>
public sealed class ClamAvOptions
{
    public const string SectionName = "FileScanning:ClamAv";

    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 3310;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Streams every upload to clamd (INSTREAM) before it is stored. Fails closed: if clamd can't be reached or
/// errors, the upload is refused rather than stored unscanned.
/// </summary>
internal sealed partial class ClamAvUploadCheck(IOptions<ClamAvOptions> options, ILogger<ClamAvUploadCheck> logger) : IFileUploadCheck
{
    private const int ChunkSize = 64 * 1024;

    public int Order => -100;

    public async Task<ValidationResponse> CheckAsync(FileUploadCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        string reply;
        try
        {
            reply = await ScanAsync(context.Content, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or IOException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnavailable(logger, exception, options.Value.Host);
            return Unscanned();
        }

        // Replies: "stream: OK", "stream: <signature> FOUND", "<reason> ERROR".
        if (reply.EndsWith(" OK", StringComparison.Ordinal))
        {
            return ValidationResponse.Success();
        }

        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            LogInfected(logger, reply, context.Sha256, context.Category);
            return ValidationResponse.Failure("The file was rejected because it appears to contain malware.", "file.malware");
        }

        LogScanError(logger, reply);
        return Unscanned();
    }

    internal async Task<string> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host!, settings.Port, timeout.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
        var chunk = new byte[4 + ChunkSize];
        int read;
        while ((read = await content.ReadAsync(chunk.AsMemory(4, ChunkSize), timeout.Token)) > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)read);
            await stream.WriteAsync(chunk.AsMemory(0, 4 + read), timeout.Token);
        }

        await stream.WriteAsync(new byte[4], timeout.Token); // zero-length chunk ends the stream

        using var reply = new MemoryStream();
        var buffer = new byte[256];
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            var end = Array.IndexOf(buffer, (byte)0, 0, read);
            await reply.WriteAsync(buffer.AsMemory(0, end < 0 ? read : end), timeout.Token);
            if (end >= 0 || reply.Length > 4096)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(reply.ToArray()).Trim();
    }

    private static ValidationResponse Unscanned() =>
        ValidationResponse.Failure("The file could not be checked for viruses right now. Please try again later.", "file.unscanned");

    [LoggerMessage(Level = LogLevel.Error, Message = "ClamAV at {Host} is unreachable; upload refused")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string? host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malware rejected: {Reply} (sha256 {Sha256}, category {Category})")]
    private static partial void LogInfected(ILogger logger, string reply, string sha256, string category);

    [LoggerMessage(Level = LogLevel.Error, Message = "ClamAV scan error: {Reply}; upload refused")]
    private static partial void LogScanError(ILogger logger, string reply);
}
