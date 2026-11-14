using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SwAgent.AddIn.Core;

/// <summary>A page of a PDF as it is shown: its media box turned by its rotation and scaled by its user unit.</summary>
public sealed class PdfPage
{
    public PdfPage(double widthPt, double heightPt, int rotate)
    {
        WidthPt = widthPt;
        HeightPt = heightPt;
        Rotate = rotate;
    }

    /// <summary>Its width as shown, points (1/72 inch).</summary>
    public double WidthPt { get; }

    /// <summary>Its height as shown, points.</summary>
    public double HeightPt { get; }

    /// <summary>Its /Rotate, degrees clockwise (0, 90, 180 or 270).</summary>
    public int Rotate { get; }

    public double WidthMm => WidthPt * PdfFile.MmPerPoint;

    public double HeightMm => HeightPt * PdfFile.MmPerPoint;

    public override string ToString() => FormattableString.Invariant($"{WidthMm:0.##} × {HeightMm:0.##} mm");
}

/// <summary>
/// What a PDF holds, as export.drawing checks it (design 611, Export): its version and its pages in order, each with the
/// size it is shown at. The page tree is read from the document catalog the last trailer (or cross-reference stream)
/// names, through every object of the file whether written plainly or inside a FlateDecode object stream (PDF 1.5), the
/// latest definition of an object winning as incremental updates mean. A page's media box, rotation and user unit are
/// inherited from its parents where it has none. Nothing else is read: no content, fonts or text.
/// </summary>
public sealed class PdfFile
{
    public const double MmPerPoint = 25.4 / 72;

    /// <summary>How deep a page tree may nest before it is taken to point back at itself.</summary>
    const int MaxDepth = 64;

    static readonly Regex ObjectHeader = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b", RegexOptions.CultureInvariant);

    static readonly Regex Trailer = new Regex(@"\btrailer\b", RegexOptions.CultureInvariant);

    readonly Dictionary<int, object?> _objects = new Dictionary<int, object?>();

    PdfFile(string version)
    {
        Version = version;
    }

    /// <summary>The version its header gives, such as 1.4.</summary>
    public string Version { get; }

    /// <summary>Its pages, in the page tree's order.</summary>
    public IReadOnlyList<PdfPage> Pages { get; private set; } = new List<PdfPage>();

    /// <summary>The page tree root's /Count, or null when it gives none.</summary>
    public int? DeclaredCount { get; private set; }

    /// <summary>True when some objects were read from compressed object streams.</summary>
    public bool ObjectStreams { get; private set; }

    public static PdfFile Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads a PDF; FormatException saying what is wrong when it is not one that can be read.</summary>
    public static PdfFile Parse(byte[] bytes)
    {
        string text = Latin1(bytes, 0, bytes.Length);
        if (!text.StartsWith("%PDF-", StringComparison.Ordinal)) throw new FormatException("it does not start %PDF-");
        int eol = text.IndexOfAny(new[] { '\r', '\n' });
        var pdf = new PdfFile(text.Substring(5, (eol < 0 ? Math.Min(text.Length, 8) : eol) - 5).Trim());
        object? root = pdf.ReadObjects(bytes, text);
        pdf.ReadPages(root);
        return pdf;
    }

    /// <summary>Every object of the file into <see cref="_objects"/>; the catalog's reference the last trailer or XRef stream names.</summary>
    object? ReadObjects(byte[] bytes, string text)
    {
        var streams = new List<(Dictionary<string, object?> Dict, byte[] Data)>();
        var roots = new List<(int At, object? Root)>();
        int at = 0;
        while (true)
        {
            var m = ObjectHeader.Match(text, at);
            if (!m.Success) break;
            int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var lexer = new Lexer(text, m.Index + m.Length);
            object? value;
            try
            {
                value = lexer.Value();
            }
            catch (FormatException e)
            {
                throw new FormatException($"object {number} cannot be read: {e.Message}");
            }
            at = lexer.Position;
            if (value is Dictionary<string, object?> dict && lexer.Keyword("stream"))
            {
                int start = lexer.Position;
                if (start < text.Length && text[start] == '\r') start++;
                if (start < text.Length && text[start] == '\n') start++;
                int end = dict.TryGetValue("Length", out var l) && l is double length && start + (int)length <= bytes.Length
                        && text.IndexOf("endstream", start + (int)length, StringComparison.Ordinal) >= 0
                    ? start + (int)length
                    : EndOfStream(text, start);
                var data = new byte[end - start];
                Array.Copy(bytes, start, data, 0, data.Length);
                streams.Add((dict, data));
                int after = text.IndexOf("endstream", end, StringComparison.Ordinal);
                at = after < 0 ? text.Length : after + "endstream".Length;
                if (NameOf(dict, "Type") == "XRef" && dict.TryGetValue("Root", out var xrefRoot)) roots.Add((m.Index, xrefRoot));
            }
            _objects[number] = value;
        }

        foreach (Match t in Trailer.Matches(text))
        {
            try
            {
                if (new Lexer(text, t.Index + t.Length).Value() is Dictionary<string, object?> trailer && trailer.TryGetValue("Root", out var root)) roots.Add((t.Index, root));
            }
            catch (FormatException)
            {
                // A trailer that does not read is passed over; a later one, or the catalog itself, names the root.
            }
        }

        foreach (var (dict, data) in streams.Where(s => NameOf(s.Dict, "Type") == "ObjStm"))
        {
            ReadObjectStream(dict, data);
            ObjectStreams = true;
        }

        if (roots.Count > 0) return roots.OrderBy(r => r.At).Last().Root;
        var catalog = _objects.Where(o => o.Value is Dictionary<string, object?> d && NameOf(d, "Type") == "Catalog").Select(o => o.Key).ToList();
        if (catalog.Count == 0) throw new FormatException("it has no document catalog (no trailer /Root and no /Type /Catalog object)");
        return new Ref(catalog.Max());
    }

    static int EndOfStream(string text, int start)
    {
        int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("a stream has no endstream");
        if (end > start && text[end - 1] == '\n') end--;
        if (end > start && text[end - 1] == '\r') end--;
        return end;
    }

    /// <summary>The objects a /Type /ObjStm holds: N pairs of object number and offset from /First, then the objects.</summary>
    void ReadObjectStream(Dictionary<string, object?> dict, byte[] data)
    {
        byte[] decoded = Decode(dict, data);
        string text = Latin1(decoded, 0, decoded.Length);
        int n = (int)Number(dict, "N"), first = (int)Number(dict, "First");
        var header = new Lexer(text, 0);
        for (int i = 0; i < n; i++)
        {
            int number = (int)(header.Value() as double? ?? throw new FormatException("an object stream's header is not object numbers and offsets"));
            int offset = (int)(header.Value() as double? ?? throw new FormatException("an object stream's header is not object numbers and offsets"));
            if (_objects.ContainsKey(number)) continue;
            _objects[number] = new Lexer(text, first + offset).Value();
        }
    }

    static byte[] Decode(Dictionary<string, object?> dict, byte[] data)
    {
        dict.TryGetValue("Filter", out var filter);
        string? name = filter switch
        {
            Name single => single.Value,
            List<object?> list when list.Count == 1 && list[0] is Name only => only.Value,
            null => null,
            _ => "several filters",
        };
        if (name == null) return data;
        if (name != "FlateDecode") throw new FormatException($"an object stream is encoded with {name}, which is not read here");
        if (data.Length < 2) throw new FormatException("an object stream is empty");
        using var input = new MemoryStream(data, 2, data.Length - 2);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    void ReadPages(object? root)
    {
        var catalog = Resolve(root) as Dictionary<string, object?> ?? throw new FormatException("its document catalog is not a dictionary");
        if (!(Resolve(catalog.TryGetValue("Pages", out var p) ? p : null) is Dictionary<string, object?> tree))
            throw new FormatException("its document catalog names no page tree");
        DeclaredCount = tree.TryGetValue("Count", out var c) && Resolve(c) is double count ? (int)count : (int?)null;
        var pages = new List<PdfPage>();
        Walk(tree, null, 0, 1, pages, new HashSet<Dictionary<string, object?>>(), 0);
        Pages = pages;
    }

    void Walk(Dictionary<string, object?> node, List<double>? box, int rotate, double unit, List<PdfPage> pages, HashSet<Dictionary<string, object?>> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(node)) throw new FormatException("its page tree has a loop: a node is its own ancestor");
        if (node.TryGetValue("MediaBox", out var b)) box = Box(Resolve(b));
        if (node.TryGetValue("Rotate", out var r) && Resolve(r) is double degrees) rotate = (((int)degrees % 360) + 360) % 360;
        if (node.TryGetValue("UserUnit", out var u) && Resolve(u) is double userUnit) unit = userUnit;
        if (NameOf(node, "Type") == "Page" || !node.ContainsKey("Kids"))
        {
            if (box == null) throw new FormatException($"page {pages.Count + 1} has no MediaBox, its own or inherited");
            double w = Math.Abs(box[2] - box[0]) * unit, h = Math.Abs(box[3] - box[1]) * unit;
            pages.Add(rotate == 90 || rotate == 270 ? new PdfPage(h, w, rotate) : new PdfPage(w, h, rotate));
            return;
        }
        if (!(Resolve(node["Kids"]) is List<object?> kids)) throw new FormatException("a page tree node's /Kids is not an array");
        foreach (var kid in kids)
        {
            if (!(Resolve(kid) is Dictionary<string, object?> child)) throw new FormatException("a page tree node names a kid that is not a dictionary");
            Walk(child, box, rotate, unit, pages, seen, depth + 1);
        }
        seen.Remove(node);
    }

    List<double> Box(object? value)
    {
        if (!(value is List<object?> list) || list.Count != 4) throw new FormatException("a MediaBox is not an array of four numbers");
        return list.Select(v => Resolve(v) as double? ?? throw new FormatException("a MediaBox holds something that is not a number")).ToList();
    }

    object? Resolve(object? value)
    {
        for (int hops = 0; value is Ref r; hops++)
        {
            if (hops > MaxDepth || !_objects.TryGetValue(r.Number, out value)) throw new FormatException($"object {r.Number} is named but not in the file (the page tree cannot be followed)");
        }
        return value;
    }

    static string? NameOf(Dictionary<string, object?> dict, string key) => dict.TryGetValue(key, out var v) && v is Name n ? n.Value : null;

    static double Number(Dictionary<string, object?> dict, string key) =>
        dict.TryGetValue(key, out var v) && v is double d ? d : throw new FormatException($"an object stream has no /{key}");

    static string Latin1(byte[] bytes, int index, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++) chars[i] = (char)bytes[index + i];
        return new string(chars);
    }

    sealed class Name
    {
        public Name(string value)
        {
            Value = value;
        }

        public string Value { get; }
    }

    sealed class Ref
    {
        public Ref(int number)
        {
            Number = number;
        }

        public int Number { get; }
    }

    /// <summary>PDF's objects from text: numbers (double), names, strings, arrays (List), dictionaries, references, booleans, null.</summary>
    sealed class Lexer
    {
        readonly string _text;

        public Lexer(string text, int position)
        {
            _text = text;
            Position = position;
        }

        public int Position { get; private set; }

        /// <summary>True, past it, when the next token is <paramref name="word"/>.</summary>
        public bool Keyword(string word)
        {
            Skip();
            if (string.CompareOrdinal(_text, Position, word, 0, word.Length) != 0) return false;
            int end = Position + word.Length;
            if (end < _text.Length && !Delimiter(_text[end]) && !char.IsWhiteSpace(_text[end])) return false;
            Position = end;
            return true;
        }

        public object? Value()
        {
            Skip();
            if (Position >= _text.Length) throw new FormatException("the file ends inside an object");
            char c = _text[Position];
            if (c == '<' && Next(1) == '<') return Dictionary();
            if (c == '<') return HexString();
            if (c == '[') return Array();
            if (c == '(') return LiteralString();
            if (c == '/') return new Name(Word(1));
            if (c == '+' || c == '-' || c == '.' || char.IsDigit(c)) return NumberOrRef();
            string word = Word(0);
            return word switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                "" => throw new FormatException($"'{c}' where a value was expected"),
                _ => throw new FormatException($"'{word}' where a value was expected"),
            };
        }

        char Next(int ahead) => Position + ahead < _text.Length ? _text[Position + ahead] : '\0';

        Dictionary<string, object?> Dictionary()
        {
            Position += 2;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (Position >= _text.Length) throw new FormatException("a dictionary has no >>");
                if (_text[Position] == '>' && Next(1) == '>')
                {
                    Position += 2;
                    return dict;
                }
                if (!(Value() is Name key)) throw new FormatException("a dictionary's key is not a name");
                dict[key.Value] = Value();
            }
        }

        List<object?> Array()
        {
            Position++;
            var list = new List<object?>();
            while (true)
            {
                Skip();
                if (Position >= _text.Length) throw new FormatException("an array has no ]");
                if (_text[Position] == ']')
                {
                    Position++;
                    return list;
                }
                list.Add(Value());
            }
        }

        string LiteralString()
        {
            Position++;
            var s = new StringBuilder();
            int depth = 1;
            while (Position < _text.Length)
            {
                char c = _text[Position++];
                if (c == '\\' && Position < _text.Length)
                {
                    s.Append(_text[Position++]);
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) return s.ToString();
                s.Append(c);
            }
            throw new FormatException("a string has no closing )");
        }

        string HexString()
        {
            int end = _text.IndexOf('>', Position);
            if (end < 0) throw new FormatException("a hex string has no >");
            string hex = _text.Substring(Position + 1, end - Position - 1);
            Position = end + 1;
            return hex;
        }

        object NumberOrRef()
        {
            double first = ParseNumber(Word(0));
            int saved = Position;
            Skip();
            if (Position < _text.Length && char.IsDigit(_text[Position]) && first >= 0 && first == Math.Floor(first))
            {
                string second = Word(0);
                if (second.All(char.IsDigit) && Keyword("R")) return new Ref((int)first);
            }
            Position = saved;
            return first;
        }

        static double ParseNumber(string word) =>
            double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw new FormatException($"'{word}' is not a number");

        /// <summary>The run of regular characters from Position + <paramref name="skip"/>.</summary>
        string Word(int skip)
        {
            int start = Position + skip, end = start;
            while (end < _text.Length && !Delimiter(_text[end]) && !char.IsWhiteSpace(_text[end])) end++;
            Position = end;
            return _text.Substring(start, end - start);
        }

        static bool Delimiter(char c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';

        /// <summary>Past white space and comments.</summary>
        void Skip()
        {
            while (Position < _text.Length)
            {
                char c = _text[Position];
                if (c == '%')
                {
                    while (Position < _text.Length && _text[Position] != '\n' && _text[Position] != '\r') Position++;
                }
                else if (char.IsWhiteSpace(c) || c == '\0')
                {
                    Position++;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
