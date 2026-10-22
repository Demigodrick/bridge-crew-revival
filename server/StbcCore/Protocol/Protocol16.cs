using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StbcServer.Protocol;

/// <summary>
/// Photon "GpBinaryV16" serialization, as implemented by the game's Photon3Unity3D 4.1.0.5 client.
///
/// Values the server needs to inspect are decoded to plain .NET types. Everything else (custom
/// types such as PUN's Vector3/Quaternion, typed arrays, dictionaries) is kept as <see cref="RawValue"/>
/// so it is relayed byte-for-byte without the server having to understand it.
/// </summary>
public static class Protocol16
{
    public const byte Null = 42, Dictionary = 68, StringArray = 97, Byte = 98, Custom = 99, Double = 100,
        EventData = 101, Float = 102, Hashtable = 104, Integer = 105, Short = 107, Long = 108,
        IntegerArray = 110, Boolean = 111, OperationResponse = 112, OperationRequest = 113,
        String = 115, ByteArray = 120, Array = 121, ObjectArray = 122;

    // ---- Reading ---------------------------------------------------------------------------

    public static object? ReadTyped(Reader r)
    {
        int start = r.Position;
        byte type = r.ReadByte();
        return ReadValue(r, type, start);
    }

    /// <param name="start">Position of the type byte, or -1 when the type was implicit.</param>
    public static object? ReadValue(Reader r, byte type, int start = -1)
    {
        switch (type)
        {
            case 0:
            case Null: return null;
            case Byte: return r.ReadByte();
            case Boolean: return r.ReadByte() != 0;
            case Short: return r.ReadInt16();
            case Integer: return r.ReadInt32();
            case Long: return r.ReadInt64();
            case Float: return r.ReadSingle();
            case Double: return r.ReadDouble();
            case String: return ReadString(r);
            case ByteArray: return r.ReadBytes(r.ReadInt32());
            case IntegerArray:
            {
                var a = new int[r.ReadInt32()];
                for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32();
                return a;
            }
            case StringArray:
            {
                var a = new string[r.ReadInt16()];
                for (int i = 0; i < a.Length; i++) a[i] = ReadString(r);
                return a;
            }
            case ObjectArray:
            {
                var a = new object?[r.ReadInt16()];
                for (int i = 0; i < a.Length; i++) a[i] = ReadTyped(r);
                return a;
            }
            case Hashtable:
            {
                int count = r.ReadInt16();
                var h = new PHashtable(count);
                for (int i = 0; i < count; i++)
                {
                    var key = ReadTyped(r) ?? throw new InvalidDataException("null hashtable key");
                    h[key] = ReadTyped(r);
                }
                if (start >= 0) h.Raw = r.Slice(start);
                return h;
            }
            case Array:
            {
                // The client sends every array except byte[] as a typed array. int[], bool[] and string[]
                // are decoded (actor lists, target actors, lobby property lists, friend lists); other
                // typed arrays are relayed raw.
                int save = r.Position;
                short len = r.ReadInt16();
                byte elem = r.ReadByte();
                if (elem == Integer)
                {
                    var a = new int[len];
                    for (int i = 0; i < len; i++) a[i] = r.ReadInt32();
                    return a;
                }
                if (elem == Boolean)
                {
                    var a = new bool[len];
                    for (int i = 0; i < len; i++) a[i] = r.ReadByte() != 0;
                    return a;
                }
                if (elem == String)
                {
                    var a = new string[len];
                    for (int i = 0; i < len; i++) a[i] = ReadString(r);
                    return a;
                }
                r.Position = save;
                break;
            }
        }

        // Everything else: skip over it exactly as the client deserializer would and keep the bytes.
        if (start < 0)
            throw new InvalidDataException($"cannot keep untyped value of type {type} raw");
        Skip(r, type);
        return new RawValue(r.Slice(start));
    }

    public static string ReadString(Reader r)
    {
        short len = r.ReadInt16();
        if (len == 0) return "";
        var bytes = r.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
    }

    public static Dictionary<byte, object?> ReadParameterTable(Reader r)
    {
        int count = r.ReadInt16();
        var p = new Dictionary<byte, object?>(count);
        for (int i = 0; i < count; i++)
        {
            byte key = r.ReadByte();
            p[key] = ReadTyped(r);
        }
        return p;
    }

    public static OperationRequest ReadOperationRequest(Reader r) =>
        new(r.ReadByte(), ReadParameterTable(r));

    /// <summary>Advance past one value of the given type (mirrors Protocol16.Deserialize).</summary>
    private static void Skip(Reader r, byte type)
    {
        switch (type)
        {
            case 0: case Null: return;
            case Byte: case Boolean: r.Skip(1); return;
            case Short: r.Skip(2); return;
            case Integer: case Float: r.Skip(4); return;
            case Long: case Double: r.Skip(8); return;
            case String: r.Skip(r.ReadInt16()); return;
            case ByteArray: r.Skip(r.ReadInt32()); return;
            case IntegerArray: r.Skip(4 * r.ReadInt32()); return;
            case StringArray:
                for (int n = r.ReadInt16(), i = 0; i < n; i++) r.Skip(r.ReadInt16());
                return;
            case ObjectArray:
                for (int n = r.ReadInt16(), i = 0; i < n; i++) Skip(r, r.ReadByte());
                return;
            case Hashtable:
                for (int n = r.ReadInt16(), i = 0; i < n; i++) { Skip(r, r.ReadByte()); Skip(r, r.ReadByte()); }
                return;
            case Custom:
                r.Skip(1);
                r.Skip(r.ReadInt16());
                return;
            case Dictionary:
                SkipDictionary(r);
                return;
            case Array:
                SkipArray(r);
                return;
            case EventData:
                r.Skip(1);
                SkipParameterTable(r);
                return;
            case OperationRequest:
                r.Skip(1);
                SkipParameterTable(r);
                return;
            case OperationResponse:
                r.Skip(1 + 2);
                Skip(r, r.ReadByte());
                SkipParameterTable(r);
                return;
            default:
                throw new InvalidDataException($"unknown Protocol16 type code {type}");
        }
    }

    private static void SkipParameterTable(Reader r)
    {
        for (int n = r.ReadInt16(), i = 0; i < n; i++) { r.Skip(1); Skip(r, r.ReadByte()); }
    }

    private static void SkipDictionary(Reader r)
    {
        byte keyType = r.ReadByte(), valueType = r.ReadByte();
        bool dynKey = keyType is 0 or Null, dynValue = valueType is 0 or Null;
        for (int n = r.ReadInt16(), i = 0; i < n; i++)
        {
            Skip(r, dynKey ? r.ReadByte() : keyType);
            Skip(r, dynValue ? r.ReadByte() : valueType);
        }
    }

    private static void SkipArray(Reader r)
    {
        short len = r.ReadInt16();
        byte elem = r.ReadByte();
        switch (elem)
        {
            case Array:
                for (int i = 0; i < len; i++) SkipArray(r);
                return;
            case ByteArray:
                for (int i = 0; i < len; i++) r.Skip(r.ReadInt32());
                return;
            case Custom:
                r.Skip(1);
                for (int i = 0; i < len; i++) r.Skip(r.ReadInt16());
                return;
            case Dictionary:
            {
                // DeserializeDictionaryArray: one shared type header, then per element count + entries.
                // (Only 0 means "typed per entry" here, unlike a single dictionary.)
                byte keyType = r.ReadByte(), valueType = r.ReadByte();
                for (int i = 0; i < len; i++)
                    for (int n = r.ReadInt16(), j = 0; j < n; j++)
                    {
                        Skip(r, keyType == 0 ? r.ReadByte() : keyType);
                        Skip(r, valueType == 0 ? r.ReadByte() : valueType);
                    }
                return;
            }
            default:
                for (int i = 0; i < len; i++) Skip(r, elem);
                return;
        }
    }

    // ---- Writing ---------------------------------------------------------------------------

    public static void WriteTyped(Writer w, object? value)
    {
        switch (value)
        {
            case null: w.WriteByte(Null); return;
            case RawValue raw: w.Write(raw.Bytes); return;
            case PHashtable { Raw: not null } h: w.Write(h.Raw); return;
            case byte b: w.WriteByte(Byte); w.WriteByte(b); return;
            case bool b: w.WriteByte(Boolean); w.WriteByte(b ? (byte)1 : (byte)0); return;
            case short s: w.WriteByte(Short); w.WriteInt16(s); return;
            case int i: w.WriteByte(Integer); w.WriteInt32(i); return;
            case long l: w.WriteByte(Long); w.WriteInt64(l); return;
            case float f: w.WriteByte(Float); w.WriteSingle(f); return;
            case double d: w.WriteByte(Double); w.WriteDouble(d); return;
            case string s: w.WriteByte(String); WriteString(w, s); return;
            case byte[] bytes: w.WriteByte(ByteArray); w.WriteInt32(bytes.Length); w.Write(bytes); return;
            case int[] ints:
                w.WriteByte(Array); w.WriteInt16((short)ints.Length); w.WriteByte(Integer);
                foreach (var i in ints) w.WriteInt32(i);
                return;
            case bool[] bools:
                w.WriteByte(Array); w.WriteInt16((short)bools.Length); w.WriteByte(Boolean);
                foreach (var b in bools) w.WriteByte(b ? (byte)1 : (byte)0);
                return;
            case string[] strings:
                // Same encoding as the client (typed array of strings), so relays stay byte-exact.
                w.WriteByte(Array); w.WriteInt16((short)strings.Length); w.WriteByte(String);
                foreach (var s in strings) WriteString(w, s);
                return;
            case object?[] objects:
                w.WriteByte(ObjectArray); w.WriteInt16((short)objects.Length);
                foreach (var o in objects) WriteTyped(w, o);
                return;
            case PHashtable h:
                w.WriteByte(Hashtable); w.WriteInt16((short)h.Count);
                foreach (var kv in h) { WriteTyped(w, kv.Key); WriteTyped(w, kv.Value); }
                return;
            default:
                throw new NotSupportedException($"cannot serialize {value.GetType()}");
        }
    }

    public static void WriteString(Writer w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > short.MaxValue) throw new NotSupportedException("string too long");
        w.WriteInt16((short)bytes.Length);
        w.Write(bytes);
    }

    public static void WriteParameterTable(Writer w, IDictionary<byte, object?>? parameters)
    {
        w.WriteInt16((short)(parameters?.Count ?? 0));
        if (parameters == null) return;
        foreach (var kv in parameters)
        {
            w.WriteByte(kv.Key);
            WriteTyped(w, kv.Value);
        }
    }

    public static void WriteOperationResponse(Writer w, OperationResponse resp)
    {
        w.WriteByte(resp.OperationCode);
        w.WriteInt16(resp.ReturnCode);
        if (string.IsNullOrEmpty(resp.DebugMessage)) w.WriteByte(Null);
        else { w.WriteByte(String); WriteString(w, resp.DebugMessage!); }
        WriteParameterTable(w, resp.Parameters);
    }

    public static void WriteEvent(Writer w, EventData ev)
    {
        w.WriteByte(ev.Code);
        WriteParameterTable(w, ev.Parameters);
    }

    public static void WriteOperationRequest(Writer w, OperationRequest req)
    {
        w.WriteByte(req.OperationCode);
        WriteParameterTable(w, req.Parameters);
    }

    /// <summary>Serialized form of a value, used for equality checks (cache filters, CAS).</summary>
    public static byte[] Encode(object? value)
    {
        var w = new Writer();
        WriteTyped(w, value);
        return w.ToArray();
    }

    public static bool SameValue(object? a, object? b) => Compat.BytesEqual(Encode(a), Encode(b));
}

/// <summary>A value kept in its exact serialized form, including its type code.</summary>
public sealed class RawValue : IEquatable<RawValue>
{
    public RawValue(byte[] bytes) => Bytes = bytes;
    public byte[] Bytes { get; }
    public bool Equals(RawValue? other) => other != null && Compat.BytesEqual(Bytes, other.Bytes);
    public override bool Equals(object? obj) => Equals(obj as RawValue);
    public override int GetHashCode() => Compat.BytesHash(Bytes);
    public override string ToString() => $"raw[{Bytes.Length}]";
}

/// <summary>Photon Hashtable. Keeps its original bytes until modified, so relays are exact.</summary>
public sealed class PHashtable : Dictionary<object, object?>
{
    public PHashtable() { }
    public PHashtable(int capacity) : base(capacity) { }
    public PHashtable(IDictionary<object, object?> other) : base(other) { }
    internal byte[]? Raw { get; set; }
}

public sealed class OperationRequest
{
    public OperationRequest(byte operationCode, Dictionary<byte, object?> parameters)
    {
        OperationCode = operationCode;
        Parameters = parameters;
    }

    public byte OperationCode { get; }
    public Dictionary<byte, object?> Parameters { get; }
    public object? this[byte key] => Parameters.GetOrDefault(key);
}

public sealed class OperationResponse
{
    public OperationResponse(byte operationCode, short returnCode = 0, string? debugMessage = null)
    {
        OperationCode = operationCode;
        ReturnCode = returnCode;
        DebugMessage = debugMessage;
    }

    public byte OperationCode { get; }
    public short ReturnCode { get; }
    public string? DebugMessage { get; }
    public Dictionary<byte, object?> Parameters { get; } = new();
    public object? this[byte key] { set => Parameters[key] = value; }
}

public sealed class EventData
{
    public EventData(byte code) => Code = code;
    public byte Code { get; }
    public Dictionary<byte, object?> Parameters { get; } = new();
    public object? this[byte key] { set => Parameters[key] = value; }
}

/// <summary>Big-endian reader over a byte array.</summary>
public sealed class Reader
{
    private readonly byte[] data;
    public int Position;

    public Reader(byte[] data, int position = 0)
    {
        this.data = data;
        Position = position;
    }

    public int Remaining => data.Length - Position;
    public byte ReadByte() { Need(1); return data[Position++]; }
    public short ReadInt16() { Need(2); var v = (short)((data[Position] << 8) | data[Position + 1]); Position += 2; return v; }
    public int ReadInt32() { Need(4); var v = (data[Position] << 24) | (data[Position + 1] << 16) | (data[Position + 2] << 8) | data[Position + 3]; Position += 4; return v; }
    public long ReadInt64() { long hi = (uint)ReadInt32(); long lo = (uint)ReadInt32(); return (hi << 32) | lo; }
    public float ReadSingle() => BitConverter.ToSingle(BigEndian(4), 0);
    public double ReadDouble() => BitConverter.ToDouble(BigEndian(8), 0);
    public byte[] ReadBytes(int count) { Need(count); var b = new byte[count]; Buffer.BlockCopy(data, Position, b, 0, count); Position += count; return b; }
    public void Skip(int count) { Need(count); Position += count; }
    public byte[] Slice(int start) { var b = new byte[Position - start]; Buffer.BlockCopy(data, start, b, 0, b.Length); return b; }

    private byte[] BigEndian(int n)
    {
        var b = ReadBytes(n);
        if (BitConverter.IsLittleEndian) System.Array.Reverse(b);
        return b;
    }

    private void Need(int count)
    {
        if (count < 0 || Position + count > data.Length)
            throw new InvalidDataException($"read past end (need {count} at {Position}, have {data.Length})");
    }
}

/// <summary>Big-endian writer.</summary>
public sealed class Writer
{
    private readonly MemoryStream stream = new();

    public int Length => (int)stream.Length;
    public void WriteByte(byte b) => stream.WriteByte(b);
    public void Write(byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    public void WriteInt16(short v) { stream.WriteByte((byte)(v >> 8)); stream.WriteByte((byte)v); }
    public void WriteInt32(int v) { stream.WriteByte((byte)(v >> 24)); stream.WriteByte((byte)(v >> 16)); stream.WriteByte((byte)(v >> 8)); stream.WriteByte((byte)v); }
    public void WriteInt64(long v) { WriteInt32((int)(v >> 32)); WriteInt32((int)v); }
    public void WriteSingle(float v) => WriteBigEndian(BitConverter.GetBytes(v));
    public void WriteDouble(double v) => WriteBigEndian(BitConverter.GetBytes(v));
    public byte[] ToArray() => stream.ToArray();

    private void WriteBigEndian(byte[] b)
    {
        if (BitConverter.IsLittleEndian) System.Array.Reverse(b);
        Write(b);
    }
}
