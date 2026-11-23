using System.Buffers.Binary;
using BlueMail.Dns;

namespace BlueMail.UnitTests.Mime;

public class DnsWireTests
{
    private static byte[] Header(ushort an, ushort qd = 1) =>
    [
        0x12, 0x34, 0x81, 0x80, (byte)(qd >> 8), (byte)qd, (byte)(an >> 8), (byte)an, 0, 0, 0, 0,
    ];

    private static byte[] Question() => [3, (byte)'a', (byte)'b', (byte)'c', 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1];

    [Fact]
    public void Query_has_rd_edns0_and_one_question()
    {
        var q = DnsWire.EncodeQuery(0xBEEF, "mx.bluemail.test", DnsType.MX, 1232, false);
        BinaryPrimitives.ReadUInt16BigEndian(q).Should().Be(0xBEEF);
        (q[2] & 0x01).Should().Be(1, "recursion desired");
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)).Should().Be(1);
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(10)).Should().Be(1, "OPT record");
        BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 8)).Should().Be(1232, "advertised UDP size in OPT CLASS");
    }

    [Fact]
    public void Decodes_compressed_answer()
    {
        // abc.test A 10.0.0.1 with the answer name as a pointer to offset 12
        var msg = Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 10, 0, 0, 1 }).ToArray();
        var m = DnsWire.Decode(msg);
        m.Answers.Should().ContainSingle();
        m.Answers[0].Name.Should().Be("abc.test.");
        m.Answers[0].Data.ToString().Should().Be("10.0.0.1");
    }

    public static TheoryData<string, byte[]> Hostile => new()
    {
        { "pointer loop", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 26, 0, 1, 0, 1, 0, 0, 0, 1, 0, 4, 1, 2, 3, 4 }).ToArray() },
        { "forward pointer", Header(1, 0).Concat(new byte[] { 0xC0, 20, 0, 1, 0, 1, 0, 0, 0, 1, 0, 4, 1, 2, 3, 4, 0, 0, 0, 0, 0 }).ToArray() },
        { "truncated rdata", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 1, 0, 40, 1, 2 }).ToArray() },
        { "short header", new byte[] { 1, 2, 3 } },
        { "txt overrun", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 16, 0, 1, 0, 0, 0, 1, 0, 3, 9, (byte)'a', (byte)'b' }).ToArray() },
        { "label type 01", Header(1, 0).Concat(new byte[] { 0x41, 1, 0 }).ToArray() },
        // BUG-066 (fuzz dns #661): NSEC3 salt length pointing past its RDATA (inside the packet) read out of bounds.
        { "nsec3 salt past rdata", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 50, 0, 1, 0, 0, 0, 1, 0, 5, 1, 0, 0, 1, 3, 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray() },
        { "nsec3 hash past rdata", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 50, 0, 1, 0, 0, 0, 1, 0, 7, 1, 0, 0, 1, 1, 0xAA, 0x30, 1, 2, 3, 4 }).ToArray() },
        { "rrsig signer past rdata", Header(1).Concat(Question()).Concat(new byte[] { 0xC0, 12, 0, 46, 0, 1, 0, 0, 0, 1, 0, 19, 0, 1, 13, 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 4, 3, (byte)'a', (byte)'b', (byte)'c', 0 }).ToArray() },
        { "name over 255", Header(1, 0).Concat(Enumerable.Range(0, 5).SelectMany(_ => new byte[] { 63 }.Concat(Enumerable.Repeat((byte)'a', 63)))).Concat(new byte[] { 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0 }).ToArray() },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_packets_fail_with_format_exception_only(string name, byte[] packet)
    {
        var act = () => DnsWire.Decode(packet);
        act.Should().Throw<DnsFormatException>(name);
    }

    [Fact]
    public void Reverse_names()
    {
        DnsClient.ReverseName(System.Net.IPAddress.Parse("10.77.0.26")).Should().Be("26.0.77.10.in-addr.arpa.");
        DnsClient.ReverseName(System.Net.IPAddress.Parse("2001:db8::1")).Should().Be("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa.");
    }
}
