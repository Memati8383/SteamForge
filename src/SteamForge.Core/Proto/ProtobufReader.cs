namespace SteamForge.Core.Proto;

public enum ProtoField
{
    Varint,
    Fixed64,
    LengthDelimited,
    Fixed32,
    EndOfBuffer,
}

public readonly struct ProtoValue
{
    public readonly int FieldNumber;
    public readonly ProtoField Kind;
    public readonly ulong Varint;
    public readonly byte[] Bytes;

    public ProtoValue(int fieldNumber, ProtoField kind, ulong varint, byte[] bytes)
    {
        FieldNumber = fieldNumber;
        Kind = kind;
        Varint = varint;
        Bytes = bytes;
    }

    public string AsString() => Bytes is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(Bytes) : string.Empty;
    public ulong AsFixed64() => BitConverter.ToUInt64(Bytes, 0);
    public bool AsBool() => Varint != 0;
    public int AsInt() => (int)Varint;
    public long AsLong() => (long)Varint;
}

/// <summary>
/// Minimal protobuf wire-format reader used to decode Steam auth responses when the
/// request was sent in protobuf form. Unknown fields are skipped rather than throwing,
/// so a Valve field-number shift degrades to "field missing" instead of a parse crash.
/// </summary>
public sealed class ProtobufReader
{
    private readonly byte[] _data;
    private int _pos;

    public ProtobufReader(byte[] data) => _data = data ?? Array.Empty<byte>();
    public ProtobufReader(ReadOnlyMemory<byte> data) => _data = data.ToArray();

    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) break;
        }
        return value;
    }

    public ProtoValue? Next()
    {
        if (_pos >= _data.Length) return null;

        var span = _data.AsSpan();
        ulong tag = ReadVarint(span, ref _pos);
        int field = (int)(tag >> 3);
        uint wire = (uint)(tag & 0x7);

        switch (wire)
        {
            case 0:
                return new ProtoValue(field, ProtoField.Varint, ReadVarint(span, ref _pos), Array.Empty<byte>());

            case 1:
            {
                if (_pos + 8 > _data.Length) return null;
                var bytes = new byte[8];
                Array.Copy(_data, _pos, bytes, 0, 8);
                _pos += 8;
                return new ProtoValue(field, ProtoField.Fixed64, 0, bytes);
            }

            case 2:
            {
                ulong len = ReadVarint(span, ref _pos);
                if (_pos + (int)len > _data.Length) return null;
                var bytes = new byte[len];
                Array.Copy(_data, _pos, bytes, 0, (int)len);
                _pos += (int)len;
                return new ProtoValue(field, ProtoField.LengthDelimited, 0, bytes);
            }

            case 5:
            {
                if (_pos + 4 > _data.Length) return null;
                var bytes = new byte[4];
                Array.Copy(_data, _pos, bytes, 0, 4);
                _pos += 4;
                return new ProtoValue(field, ProtoField.Fixed32, 0, bytes);
            }

            default:
                // Unknown wire type — cannot safely skip, stop parsing.
                _pos = _data.Length;
                return null;
        }
    }

    /// <summary>Collect every field value for a given field number, in order.</summary>
    public List<ProtoValue> Collect(int fieldNumber)
    {
        var found = new List<ProtoValue>();
        ProtoValue? v;
        while ((v = Next()) is not null)
        {
            if (v.Value.FieldNumber == fieldNumber) found.Add(v.Value);
        }
        return found;
    }

    /// <summary>First value for a field number, or null.</summary>
    public ProtoValue? First(int fieldNumber)
    {
        ProtoValue? v;
        while ((v = Next()) is not null)
        {
            if (v.Value.FieldNumber == fieldNumber) return v.Value;
        }
        return null;
    }
}
