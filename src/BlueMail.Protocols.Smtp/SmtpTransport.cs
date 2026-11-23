using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BlueMail.Protocols.Smtp;

public sealed record SmtpTransportOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);   // RFC 5321 4.5.3.2.7
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PreGreetDelay { get; init; } = TimeSpan.Zero;
    public Func<X509Certificate2?>? Certificate { get; init; }
    public bool ImplicitTls { get; init; }
}

/// <summary>
/// Runs an <see cref="SmtpServerSession"/> over a byte stream: line framing with a hard limit, DATA with dot-unstuffing
/// streamed to a temporary file, BDAT chunks, STARTTLS (TLS 1.2+ via SslStream) with any pipelined plaintext
/// discarded (RFC 3207 4.2, "STARTTLS command injection"), timeouts, and the pre-greet trap for early talkers.
/// </summary>
public sealed class SmtpTransport(Stream stream, SmtpServerSession session, SmtpTransportOptions options, ILogger logger)
{
    private Stream _stream = stream;
    private PipeReader _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (options.ImplicitTls)
            {
                if (!await UpgradeTlsAsync(ct))
                {
                    return;
                }

                session.TlsEstablished(); // RFC 8314: the whole session is protected, AUTH may be offered
            }

            if (options.PreGreetDelay > TimeSpan.Zero && await ClientSpokeEarlyAsync(ct))
            {
                // A legitimate client waits for the greeting (RFC 5321 3.1); spam software often does not.
                await WriteAsync([new SmtpReply(554, "5.7.1", "Protocol violation: talking before the greeting")], ct);
                return;
            }

            var step = await session.ConnectAsync(ct);
            while (true)
            {
                await WriteAsync(step.Replies, ct);
                switch (step.Action)
                {
                    case SmtpAction.Close:
                        return;
                    case SmtpAction.StartTls:
                        if (!await UpgradeTlsAsync(ct))
                        {
                            return;
                        }

                        session.TlsEstablished();
                        step = new SmtpStep(SmtpAction.Continue, []);
                        break;
                    case SmtpAction.ReadData:
                        step = await ReadDataAsync(ct);
                        continue;
                    case SmtpAction.ReadChunk:
                        step = await ReadChunkAsync(step.ChunkSize, step.LastChunk, ct);
                        continue;
                }

                var (line, tooLong, eof) = await ReadLineAsync(options.CommandTimeout, ct);
                if (eof)
                {
                    return;
                }

                step = await session.HandleLineAsync(line, tooLong, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryWriteAsync(SmtpReply.ShuttingDown);
        }
        catch (TimeoutException)
        {
            await TryWriteAsync(new SmtpReply(421, "4.4.2", session.Options.Hostname + " timeout, closing connection"));
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "SMTP connection from {Ip} ended", session.Envelope.RemoteAddress);
        }
        finally
        {
            await _reader.CompleteAsync();
        }
    }

    private async Task<bool> ClientSpokeEarlyAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.PreGreetDelay);
        try
        {
            var r = await _reader.ReadAsync(cts.Token);
            var spoke = r.Buffer.Length > 0;
            _reader.AdvanceTo(r.Buffer.Start);
            return spoke;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> UpgradeTlsAsync(CancellationToken ct)
    {
        var cert = options.Certificate?.Invoke();
        if (cert is null)
        {
            return false;
        }

        // Drop everything the client sent after STARTTLS on the plaintext channel.
        if (_reader.TryRead(out var pending))
        {
            _reader.AdvanceTo(pending.Buffer.End);
        }

        await _reader.CompleteAsync();
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
        }, cts.Token);
        _stream = ssl;
        _reader = PipeReader.Create(ssl, new StreamPipeReaderOptions(leaveOpen: true));
        session.Envelope.Tls = true;
        return true;
    }

    private async Task<(string Line, bool TooLong, bool Eof)> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        var max = session.Options.MaxCommandLineBytes;
        var tooLong = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync(cts.Token);
                var buffer = result.Buffer;
                var lf = buffer.PositionOf((byte)'\n');
                if (lf is { } pos)
                {
                    var lineSeq = buffer.Slice(0, pos);
                    var text = tooLong ? "" : Decode(lineSeq);
                    _reader.AdvanceTo(buffer.GetPosition(1, pos));
                    return (text.TrimEnd('\r'), tooLong, false);
                }

                if (buffer.Length > max)
                {
                    tooLong = true; // discard until the end of this line, then report
                    _reader.AdvanceTo(buffer.End);
                    continue;
                }

                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.End);
                    return ("", false, true);
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static string Decode(ReadOnlySequence<byte> s)
    {
        var bytes = s.ToArray();
        return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static FileStream NewTempFile() =>
        new(Path.Combine(Path.GetTempPath(), "bm-smtp-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    private FileStream? _bdatFile;
    private bool _bdatTooBig;

    /// <summary>DATA: lines until "." alone; dot-unstuffing (RFC 5321 4.5.2); bare LF normalised to CRLF.</summary>
    private async Task<SmtpStep> ReadDataAsync(CancellationToken ct)
    {
        await using var file = NewTempFile();
        var max = session.Options.MaxMessageBytes;
        var tooBig = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            cts.CancelAfter(options.DataTimeout);
            ReadResult result;
            try
            {
                result = await _reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            var buffer = result.Buffer;
            while (buffer.PositionOf((byte)'\n') is { } pos)
            {
                var line = buffer.Slice(0, pos);
                buffer = buffer.Slice(buffer.GetPosition(1, pos));
                var bytes = line.ToArray();
                var len = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
                if (len == 1 && bytes[0] == '.')
                {
                    _reader.AdvanceTo(buffer.Start);
                    return await session.CompleteMessageAsync(file, tooBig, ct);
                }

                var start = len > 0 && bytes[0] == '.' ? 1 : 0;
                if (!tooBig)
                {
                    if (file.Length + (len - start) + 2 > max)
                    {
                        tooBig = true; // keep reading to the terminator, store nothing more
                    }
                    else
                    {
                        await file.WriteAsync(bytes.AsMemory(start, len - start), ct);
                        await file.WriteAsync("\r\n"u8.ToArray(), ct);
                    }
                }
            }

            if (result.IsCompleted)
            {
                throw new IOException("Connection closed during DATA");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>BDAT: exactly <paramref name="size"/> octets, no dot-stuffing (RFC 3030).</summary>
    private async Task<SmtpStep> ReadChunkAsync(long size, bool last, CancellationToken ct)
    {
        _bdatFile ??= NewTempFile();
        var remaining = size;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (remaining > 0)
        {
            cts.CancelAfter(options.DataTimeout);
            ReadResult result;
            try
            {
                result = await _reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            var take = result.Buffer.Slice(0, Math.Min(remaining, result.Buffer.Length));
            if (!_bdatTooBig && _bdatFile.Length + take.Length > session.Options.MaxMessageBytes)
            {
                _bdatTooBig = true;
            }

            if (!_bdatTooBig)
            {
                foreach (var segment in take)
                {
                    await _bdatFile.WriteAsync(segment, ct);
                }
            }

            remaining -= take.Length;
            _reader.AdvanceTo(take.End);
            if (remaining > 0 && result.IsCompleted)
            {
                throw new IOException("Connection closed during BDAT");
            }
        }

        var step = session.ChunkReceived(size, last);
        if (!last)
        {
            return step;
        }

        try
        {
            if (step.Replies.Count > 0 || !session.BdatReadyToDeliver)
            {
                return step;
            }

            return await session.CompleteMessageAsync(_bdatFile, _bdatTooBig, ct);
        }
        finally
        {
            await _bdatFile.DisposeAsync();
            _bdatFile = null;
            _bdatTooBig = false;
        }
    }

    private async Task WriteAsync(IReadOnlyList<SmtpReply> replies, CancellationToken ct)
    {
        if (replies.Count == 0)
        {
            return;
        }

        foreach (var r in replies)
        {
            await _stream.WriteAsync(r.ToBytes(enhancedCodes: session.Envelope.Esmtp), ct);
        }

        await _stream.FlushAsync(ct);
    }

    private async Task TryWriteAsync(SmtpReply reply)
    {
        try
        {
            await _stream.WriteAsync(reply.ToBytes(session.Envelope.Esmtp));
            await _stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // connection already gone
        }
    }
}

/// <summary>Bidirectional stream over a Kestrel connection's duplex pipe (so SslStream can wrap it).</summary>
public sealed class DuplexPipeStream(IDuplexPipe pipe) : Stream
{
    private readonly Stream _in = pipe.Input.AsStream(leaveOpen: true);
    private readonly Stream _out = pipe.Output.AsStream(leaveOpen: true);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _out.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _out.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _in.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _out.WriteAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _in.Dispose();
            _out.Dispose();
        }

        base.Dispose(disposing);
    }
}
