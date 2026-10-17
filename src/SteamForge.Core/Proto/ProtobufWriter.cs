namespace SteamForge.Core.Proto;

/// <summary>
/// Hand-rolled protobuf wire-format writer.
/// Steam's IAuthenticationService accepts either JSON (input_json) or a base64-encoded
/// protobuf body (input_protobuf_encoded). We emit the protobuf form because it is byte-identical
/// to what the real Steam client sends, which keeps the request fingerprint clean.
/// No external dependency, no generated-code toolchain: field numbers are written explicitly
/// and stay auditable against the SteamDatabase/Protobufs definitions.
/// </summary>
public sealed class ProtobufWriter
{
    private readonly MemoryStream _buffer = new();

    private const uint WireVarint = 0;
    private const uint WireFixed64 = 1;
    private const uint WireLengthDelimited = 2;
    private const uint WireFixed32 = 5;

    private static void WriteTag(Stream s, int field, uint wireType)
        => WriteRawVarint(s, ((uint)field << 3) | wireType);

    private static void WriteRawVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public ProtobufWriter WriteString(int field, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        return WriteBytes(field, bytes);
    }

    public ProtobufWriter WriteBytes(int field, byte[] value)
    {
        WriteTag(_buffer, field, WireLengthDelimited);
        WriteRawVarint(_buffer, (ulong)value.Length);
        _buffer.Write(value, 0, value.Length);
        return this;
    }

    public ProtobufWriter WriteVarint(int field, ulong value)
    {
        WriteTag(_buffer, field, WireVarint);
        WriteRawVarint(_buffer, value);
        return this;
    }

    /// <summary>Steam uses fixed64 (little-endian) for client_id / steamid / encryption_timestamp.</summary>
    public ProtobufWriter WriteFixed64(int field, ulong value)
    {
        WriteTag(_buffer, field, WireFixed64);
        Span<byte> le = stackalloc byte[8];
        BitConverter.TryWriteBytes(le, value);
        _buffer.Write(le);
        return this;
    }

    /// <summary>fixed32 wire type — Steam encodes Poll/Begin `interval` as an IEEE-754 float.</summary>
    public ProtobufWriter WriteFloat32(int field, float value)
    {
        WriteTag(_buffer, field, WireFixed32);
        Span<byte> le = stackalloc byte[4];
        BitConverter.TryWriteBytes(le, value);
        _buffer.Write(le);
        return this;
    }

    public ProtobufWriter WriteBool(int field, bool value)
        => WriteVarint(field, value ? 1UL : 0UL);

    public ProtobufWriter WriteEnum(int field, int value)
        => WriteVarint(field, (ulong)(uint)value);

    public ProtobufWriter WriteMessage(int field, Action<ProtobufWriter> build)
    {
        var inner = new ProtobufWriter();
        build(inner);
        var bytes = inner.ToArray();
        return WriteBytes(field, bytes);
    }

    public byte[] ToArray()
    {
        var copy = new byte[_buffer.Length];
        var pos = _buffer.Position;
        _buffer.Position = 0;
        _buffer.Read(copy, 0, copy.Length);
        _buffer.Position = pos;
        return copy;
    }

    public string ToBase64() => Convert.ToBase64String(ToArray());
}
