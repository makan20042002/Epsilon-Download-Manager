using System.Text;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>
/// Bencoding (BEP 3): integers, byte strings, lists and dictionaries. Values come back as long, byte[], List&lt;object&gt; and
/// Dictionary&lt;string, object&gt; (keys are ASCII/Latin-1 text). Damaged or hostile input throws FormatException, never anything else.
/// </summary>
public static class Bencode
{
    const int MaxDepth = 40;
    const int MaxStringLength = 64 * 1024 * 1024;

    /// <summary>Parses one value. <paramref name="infoSpan"/> is where the top-level "info" dictionary lies (its exact bytes give the info hash).</summary>
    public static object Parse(byte[] data, out (int Start, int Length)? infoSpan)
    {
        infoSpan = null;
        var pos = 0;
        (int, int)? span = null;
        var value = ReadValue(data, ref pos, 0, ref span);
        if (pos != data.Length) throw new FormatException("Extra data after the bencoded value.");
        infoSpan = span;
        return value;
    }

    public static object Parse(byte[] data) => Parse(data, out _);

    /// <summary>Parses one value at the start of the data and reports how many bytes it used (peer messages carry a payload after the dictionary).</summary>
    public static object ParsePrefix(byte[] data, int offset, out int used)
    {
        var pos = offset;
        (int, int)? span = null;
        var value = ReadValue(data, ref pos, 0, ref span);
        used = pos - offset;
        return value;
    }

    static object ReadValue(byte[] d, ref int p, int depth, ref (int, int)? infoSpan)
    {
        if (depth > MaxDepth) throw new FormatException("Nesting too deep.");
        if (p >= d.Length) throw new FormatException("Unexpected end of data.");
        var c = (char)d[p];
        if (c == 'i')
        {
            var end = Array.IndexOf(d, (byte)'e', p + 1);
            if (end < 0) throw new FormatException("Unterminated integer.");
            var text = Encoding.ASCII.GetString(d, p + 1, end - p - 1);
            if (text.Length == 0 || text == "-" || text == "-0" || (text.Length > 1 && text[0] == '0') || (text.Length > 2 && text[0] == '-' && text[1] == '0') || !long.TryParse(text, out var number))
                throw new FormatException("Bad integer.");
            p = end + 1;
            return number;
        }
        if (c == 'l')
        {
            p++;
            var list = new List<object>();
            while (true)
            {
                if (p >= d.Length) throw new FormatException("Unterminated list.");
                if (d[p] == (byte)'e') { p++; return list; }
                list.Add(ReadValue(d, ref p, depth + 1, ref infoSpan));
            }
        }
        if (c == 'd')
        {
            p++;
            var dict = new Dictionary<string, object>();
            while (true)
            {
                if (p >= d.Length) throw new FormatException("Unterminated dictionary.");
                if (d[p] == (byte)'e') { p++; return dict; }
                var key = Encoding.Latin1.GetString(ReadString(d, ref p));
                var start = p;
                var value = ReadValue(d, ref p, depth + 1, ref infoSpan);
                if (depth == 0 && key == "info" && value is Dictionary<string, object>) infoSpan = (start, p - start);
                dict[key] = value;
            }
        }
        if (c is >= '0' and <= '9') return ReadString(d, ref p);
        throw new FormatException($"Unexpected byte '{c}' at position {p}.");
    }

    static byte[] ReadString(byte[] d, ref int p)
    {
        var colon = Array.IndexOf(d, (byte)':', p);
        if (colon < 0 || colon == p || colon - p > 10) throw new FormatException("Bad string length.");
        if (!int.TryParse(Encoding.ASCII.GetString(d, p, colon - p), out var length) || length < 0 || length > MaxStringLength || colon + 1 + length > d.Length)
            throw new FormatException("Bad string length.");
        var result = new byte[length];
        Buffer.BlockCopy(d, colon + 1, result, 0, length);
        p = colon + 1 + length;
        return result;
    }

    // ---------------------------------------------------------------- encoding

    public static byte[] Encode(object value)
    {
        using var stream = new MemoryStream();
        Write(stream, value);
        return stream.ToArray();
    }

    static void Write(Stream s, object value)
    {
        switch (value)
        {
            case long l: WriteAscii(s, "i" + l + "e"); break;
            case int i: WriteAscii(s, "i" + i + "e"); break;
            case byte[] b: WriteAscii(s, b.Length + ":"); s.Write(b); break;
            case string str: Write(s, Encoding.UTF8.GetBytes(str)); break;
            case IDictionary<string, object> dict:
                s.WriteByte((byte)'d');
                foreach (var pair in dict.OrderBy(x => x.Key, StringComparer.Ordinal)) { Write(s, Encoding.Latin1.GetBytes(pair.Key)); Write(s, pair.Value); }
                s.WriteByte((byte)'e');
                break;
            case IEnumerable<object> list:
                s.WriteByte((byte)'l');
                foreach (var item in list) Write(s, item);
                s.WriteByte((byte)'e');
                break;
            default: throw new ArgumentException("Cannot bencode " + value?.GetType().Name);
        }
    }

    static void WriteAscii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    // ---------------------------------------------------------------- typed access (missing or wrong type -> null / default)

    public static Dictionary<string, object>? Dict(object? o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
    public static List<object>? List(object? o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as List<object> : null;
    public static byte[]? Bytes(object? o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as byte[] : null;
    public static long? Long(object? o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is long l ? l : null;
    public static string? Text(object? o, string key) => Bytes(o, key) is { } b ? Encoding.UTF8.GetString(b) : null;
}
