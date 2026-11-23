using System.Buffers;
using System.Text;

namespace BlueMail.Mime;

/// <summary>
/// Content-Transfer-Encoding (RFC 2045 6) decoders and encoders. Decoders are lenient: base64 skips any byte
/// outside the alphabet ("garbage") and stops at padding; quoted-printable keeps an invalid "=XY" literally.
/// Streaming variants never hold more than one buffer of the body in memory.
/// </summary>
public static class TransferDecoding
{
    private static readonly sbyte[] B64 = BuildB64();

    private static sbyte[] BuildB64()
    {
        var t = new sbyte[256];
        Array.Fill(t, (sbyte)-1);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (var i = 0; i < alphabet.Length; i++)
        {
            t[alphabet[i]] = (sbyte)i;
        }

        return t;
    }

    public static byte[] DecodeBase64Lenient(ReadOnlySpan<char> s)
    {
        var bytes = new byte[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            bytes[n++] = c < 256 ? (byte)c : (byte)'!';
        }

        return DecodeBase64Lenient(bytes.AsSpan(0, n));
    }

    public static byte[] DecodeBase64Lenient(ReadOnlySpan<byte> input)
    {
        var state = new Base64State();
        var output = new byte[(input.Length / 4 * 3) + 3];
        var written = state.Decode(input, output);
        return output.AsSpan(0, written).ToArray();
    }

    public static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> input)
    {
        var state = new QpState();
        var output = new byte[input.Length];
        var written = state.Decode(input, output, final: true);
        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>Decoded view of a part body. Unknown or identity encodings pass through.</summary>
    public static Stream Decode(Stream raw, string? contentTransferEncoding) =>
        (contentTransferEncoding ?? "").Trim().ToLowerInvariant() switch
        {
            "base64" => new DecodingStream(raw, new Base64State()),
            "quoted-printable" => new DecodingStream(raw, new QpState()),
            _ => raw,
        };

    public static byte[] Decode(ReadOnlySpan<byte> raw, string? contentTransferEncoding) =>
        (contentTransferEncoding ?? "").Trim().ToLowerInvariant() switch
        {
            "base64" => DecodeBase64Lenient(raw),
            "quoted-printable" => DecodeQuotedPrintable(raw),
            _ => raw.ToArray(),
        };

    internal interface IDecoderState
    {
        int Decode(ReadOnlySpan<byte> input, Span<byte> output, bool final);
        int MaxOutput(int inputLength);
    }

    internal sealed class Base64State : IDecoderState
    {
        private int _acc;
        private int _bits;
        private bool _done;

        public int MaxOutput(int inputLength) => (inputLength * 3 / 4) + 3;

        public int Decode(ReadOnlySpan<byte> input, Span<byte> output) => Decode(input, output, false);

        public int Decode(ReadOnlySpan<byte> input, Span<byte> output, bool final)
        {
            var n = 0;
            foreach (var b in input)
            {
                if (_done)
                {
                    break;
                }

                if (b == '=')
                {
                    _done = true; // padding ends the data; anything after it is ignored
                    break;
                }

                var v = B64[b];
                if (v < 0)
                {
                    continue; // whitespace, line breaks, garbage
                }

                _acc = (_acc << 6) | (byte)v;
                _bits += 6;
                if (_bits >= 8)
                {
                    _bits -= 8;
                    output[n++] = (byte)(_acc >> _bits);
                    _acc &= (1 << _bits) - 1;
                }
            }

            return n;
        }
    }

    internal sealed class QpState : IDecoderState
    {
        private readonly byte[] _carry = new byte[3];
        private int _carryLen;

        public int MaxOutput(int inputLength) => inputLength + 3;

        public int Decode(ReadOnlySpan<byte> input, Span<byte> output, bool final)
        {
            Span<byte> buf = _carryLen + input.Length <= 4096 ? stackalloc byte[_carryLen + input.Length] : new byte[_carryLen + input.Length];
            _carry.AsSpan(0, _carryLen).CopyTo(buf);
            input.CopyTo(buf[_carryLen..]);
            _carryLen = 0;
            var n = 0;
            var i = 0;
            while (i < buf.Length)
            {
                var b = buf[i];
                if (b != '=')
                {
                    // Trailing whitespace before a line break is transport padding (RFC 2045 6.7 rule 3).
                    if (b is (byte)' ' or (byte)'\t')
                    {
                        var j = i;
                        while (j < buf.Length && buf[j] is (byte)' ' or (byte)'\t')
                        {
                            j++;
                        }

                        if (j == buf.Length && !final)
                        {
                            return n + Carry(buf[i..], ref _carryLen, output, n);
                        }

                        if (j < buf.Length && buf[j] is (byte)'\r' or (byte)'\n')
                        {
                            i = j;
                            continue;
                        }
                    }

                    output[n++] = b;
                    i++;
                    continue;
                }

                var remaining = buf.Length - i;
                if (remaining < 3 && !final)
                {
                    return n + Carry(buf[i..], ref _carryLen, output, n);
                }

                if (remaining >= 2 && buf[i + 1] == '\n')
                {
                    i += 2; // soft line break with bare LF
                    continue;
                }

                if (remaining >= 3 && buf[i + 1] == '\r' && buf[i + 2] == '\n')
                {
                    i += 3; // soft line break
                    continue;
                }

                if (remaining >= 3 && EncodedWords.Hex((char)buf[i + 1]) is var h and >= 0 && EncodedWords.Hex((char)buf[i + 2]) is var l and >= 0)
                {
                    output[n++] = (byte)((h << 4) | l);
                    i += 3;
                    continue;
                }

                if (remaining == 1)
                {
                    i++; // a final "=" with nothing after it: soft break at end of data
                    continue;
                }

                output[n++] = b; // invalid escape kept literally
                i++;
            }

            return n;
        }

        private int Carry(ReadOnlySpan<byte> rest, ref int carryLen, Span<byte> output, int n)
        {
            if (rest.Length <= _carry.Length)
            {
                rest.CopyTo(_carry);
                carryLen = rest.Length;
                return 0;
            }

            // Long run of spaces: emit all but the last few; they cannot all be padding in the data we keep.
            var emit = rest[..^_carry.Length];
            emit.CopyTo(output[n..]);
            rest[^_carry.Length..].CopyTo(_carry);
            carryLen = _carry.Length;
            return emit.Length;
        }
    }

    private sealed class DecodingStream(Stream inner, IDecoderState state) : Stream
    {
        private readonly byte[] _in = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private byte[] _out = [];
        private int _outPos;
        private int _outLen;
        private bool _eof;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_outPos == _outLen)
            {
                if (_eof)
                {
                    return 0;
                }

                var read = inner.Read(_in, 0, _in.Length);
                var final = read == 0;
                _eof = final;
                var max = state.MaxOutput(read);
                if (_out.Length < max)
                {
                    _out = new byte[max];
                }

                _outLen = state switch
                {
                    Base64State b => b.Decode(_in.AsSpan(0, read), _out),
                    _ => state.Decode(_in.AsSpan(0, read), _out, final),
                };
                _outPos = 0;
            }

            var n = Math.Min(buffer.Length, _outLen - _outPos);
            _out.AsSpan(_outPos, n).CopyTo(buffer);
            _outPos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ArrayPool<byte>.Shared.Return(_in);
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ---------------- encoders (for building messages) ----------------

    /// <summary>Base64 with CRLF every 76 characters (RFC 2045 6.8).</summary>
    public static string EncodeBase64Lines(ReadOnlySpan<byte> data)
    {
        var b64 = Convert.ToBase64String(data);
        var sb = new StringBuilder(b64.Length + (b64.Length / 76 * 2) + 2);
        for (var i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64.AsSpan(i, Math.Min(76, b64.Length - i))).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Quoted-printable with lines of at most 76 characters, CRLF line breaks preserved as hard breaks.</summary>
    public static string EncodeQuotedPrintable(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length + (data.Length / 10));
        var lineLen = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
            {
                sb.Append("\r\n");
                lineLen = 0;
                i++;
                continue;
            }

            if (b == '\n')
            {
                sb.Append("\r\n");
                lineLen = 0;
                continue;
            }

            var nextIsBreak = i + 1 >= data.Length || data[i + 1] is (byte)'\r' or (byte)'\n';
            var literal = (b is >= 33 and <= 126 && b != '=') || (b is (byte)' ' or (byte)'\t' && !nextIsBreak);
            // A leading "." or "From " is safe in MIME bodies we send (SMTP dot-stuffing happens in transport).
            var token = literal ? ((char)b).ToString() : "=" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            if (lineLen + token.Length > 75)
            {
                sb.Append("=\r\n");
                lineLen = 0;
            }

            sb.Append(token);
            lineLen += token.Length;
        }

        return sb.ToString();
    }
}
