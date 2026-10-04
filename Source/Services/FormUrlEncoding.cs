using System.Text;

namespace PayloadPanda.Services;

/// <summary>
/// application/x-www-form-urlencoded as browsers write it (the WHATWG URL standard's
/// serializer): UTF-8, ASCII letters and digits and <c>*-._</c> kept, space as <c>+</c>,
/// everything else as uppercase <c>%XX</c>. <see cref="Uri.EscapeDataString"/> and
/// FormUrlEncodedContent differ (both leave <c>~</c> alone), hence this small codec.
/// </summary>
public static class FormUrlEncoding
{
    public static string Serialize(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join('&', pairs.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));

    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    /// <summary>Lenient parse: <c>+</c> is a space, invalid <c>%</c> sequences are kept as typed.</summary>
    public static List<KeyValuePair<string, string>> Parse(string body)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var segment in body.Split('&'))
        {
            if (segment.Length == 0)
                continue;
            var equalsIndex = segment.IndexOf('=');
            var key = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
            var value = equalsIndex >= 0 ? segment[(equalsIndex + 1)..] : string.Empty;
            pairs.Add(new(Decode(key), Decode(value)));
        }
        return pairs;
    }

    /// <summary>
    /// Parses only when serializing the result gives back exactly <paramref name="body"/>,
    /// so converting a hand-typed body into rows can never change what is sent.
    /// </summary>
    public static bool TryParseLossless(string body, out List<KeyValuePair<string, string>> pairs)
    {
        pairs = Parse(body);
        if (Serialize(pairs) == body)
            return true;

        pairs = [];
        return false;
    }

    private static string Decode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                // Keep surrogate pairs (emoji) together so they encode as one character.
                var length = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(i, length)));
                i += length - 1;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
