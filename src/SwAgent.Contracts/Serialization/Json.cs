using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwAgent.Contracts.Serialization;

/// <summary>Thrown for text that is not JSON.</summary>
public sealed class JsonParseException : FormatException
{
    public JsonParseException(string message, int position) : base(message) { Position = position; }
    public int Position { get; }
}

/// <summary>
/// Parses and writes JSON as a tree of null, bool, long, double, string, <c>List&lt;object?&gt;</c> and
/// <see cref="JsonObject"/>. No dependencies, so it can run inside SOLIDWORKS' CLR.
/// </summary>
public static class Json
{
    public const int MaxDepth = 64;

    public static object? Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var parser = new Parser(text);
        parser.SkipWhitespace();
        object? value = parser.ReadValue(0);
        parser.SkipWhitespace();
        if (!parser.AtEnd) throw parser.Error("unexpected text after the JSON value");
        return value;
    }

    public static string Write(object? value, bool indented = false)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, indented, 0);
        return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object? value, bool indented, int level)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("NaN and infinity have no JSON form");
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonObject o: WriteObject(sb, o, indented, level); break;
            case IList list: WriteArray(sb, list, indented, level); break;
            default:
                throw new ArgumentException($"{value.GetType().FullName} is not a JSON tree value; convert it with JsonCodec.ToJson first");
        }
    }

    static void WriteObject(StringBuilder sb, JsonObject o, bool indented, int level)
    {
        if (o.Count == 0) { sb.Append("{}"); return; }
        sb.Append('{');
        bool first = true;
        foreach (var pair in o)
        {
            if (!first) sb.Append(',');
            first = false;
            if (indented) NewLine(sb, level + 1);
            WriteString(sb, pair.Key);
            sb.Append(indented ? ": " : ":");
            WriteValue(sb, pair.Value, indented, level + 1);
        }
        if (indented) NewLine(sb, level);
        sb.Append('}');
    }

    static void WriteArray(StringBuilder sb, IList list, bool indented, int level)
    {
        if (list.Count == 0) { sb.Append("[]"); return; }
        sb.Append('[');
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            if (indented) NewLine(sb, level + 1);
            WriteValue(sb, list[i], indented, level + 1);
        }
        if (indented) NewLine(sb, level);
        sb.Append(']');
    }

    static void NewLine(StringBuilder sb, int level)
    {
        sb.Append('\n');
        sb.Append(' ', level * 2);
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    sealed class Parser
    {
        readonly string _s;
        int _i;

        public Parser(string s) { _s = s; }

        public bool AtEnd => _i >= _s.Length;

        public JsonParseException Error(string message) => new JsonParseException($"{message} at position {_i}", _i);

        public void SkipWhitespace()
        {
            while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
        }

        char Peek() => AtEnd ? throw Error("unexpected end of JSON") : _s[_i];

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth) throw Error($"nesting deeper than {MaxDepth}");
            char c = Peek();
            switch (c)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"': return ReadString();
                case 't': ExpectWord("true"); return true;
                case 'f': ExpectWord("false"); return false;
                case 'n': ExpectWord("null"); return null;
                default:
                    if (c == '-' || IsDigit(c)) return ReadNumber();
                    throw Error($"unexpected character '{c}'");
            }
        }

        void ExpectWord(string word)
        {
            if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                throw Error($"expected '{word}'");
            _i += word.Length;
        }

        JsonObject ReadObject(int depth)
        {
            _i++;
            var o = new JsonObject();
            SkipWhitespace();
            if (Peek() == '}') { _i++; return o; }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("expected a property name");
                int at = _i;
                string key = ReadString();
                SkipWhitespace();
                if (Peek() != ':') throw Error("expected ':'");
                _i++;
                SkipWhitespace();
                object? value = ReadValue(depth + 1);
                if (o.ContainsKey(key)) { _i = at; throw Error($"duplicate property '{key}'"); }
                o.Add(key, value);
                SkipWhitespace();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == '}') { _i++; return o; }
                throw Error("expected ',' or '}'");
            }
        }

        List<object?> ReadArray(int depth)
        {
            _i++;
            var list = new List<object?>();
            SkipWhitespace();
            if (Peek() == ']') { _i++; return list; }
            while (true)
            {
                SkipWhitespace();
                list.Add(ReadValue(depth + 1));
                SkipWhitespace();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == ']') { _i++; return list; }
                throw Error("expected ',' or ']'");
            }
        }

        string ReadString()
        {
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("unterminated string");
                char c = _s[_i];
                if (c == '"') { _i++; return sb.ToString(); }
                if (c < 0x20) throw Error("control character in string");
                if (c != '\\') { sb.Append(c); _i++; continue; }
                _i++;
                if (AtEnd) throw Error("unterminated string");
                char e = _s[_i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 5 > _s.Length ||
                            !int.TryParse(_s.Substring(_i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            throw Error("invalid \\u escape");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw Error($"invalid escape '\\{e}'");
                }
                _i++;
            }
        }

        object ReadNumber()
        {
            int start = _i;
            bool integral = true;
            if (_s[_i] == '-') _i++;
            if (AtEnd || !IsDigit(_s[_i])) throw Error("expected a digit");
            if (_s[_i] == '0') _i++;
            else while (!AtEnd && IsDigit(_s[_i])) _i++;
            if (!AtEnd && _s[_i] == '.')
            {
                integral = false;
                _i++;
                if (AtEnd || !IsDigit(_s[_i])) throw Error("expected a digit after '.'");
                while (!AtEnd && IsDigit(_s[_i])) _i++;
            }
            if (!AtEnd && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                integral = false;
                _i++;
                if (!AtEnd && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (AtEnd || !IsDigit(_s[_i])) throw Error("expected a digit in the exponent");
                while (!AtEnd && IsDigit(_s[_i])) _i++;
            }
            string text = _s.Substring(start, _i - start);
            if (integral && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            double d;
            try { d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); }
            catch (OverflowException) { throw Error("number out of range"); }
            if (double.IsInfinity(d)) throw Error("number out of range");
            return d;
        }

        static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
