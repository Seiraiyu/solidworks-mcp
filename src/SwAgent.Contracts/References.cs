using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>What a reference names. Its wire name is the second part of the reference string (r1.face.…).</summary>
public enum ReferenceKind
{
    Face,
    Edge,
    Vertex,
    Feature,
    Sketch,
    SketchEntity,
    SketchPoint,
    Plane,
    Axis,
    Point,
    Dimension,
    Component,
    Mate,
    Sheet,
    View,
    Annotation,
    Table,
}

/// <summary>What a reference string carries (design 371, References).</summary>
public sealed class ReferenceData
{
    public ReferenceData(ReferenceKind kind, string documentKey, byte[] persistId, string description, string? component = null, string? componentPath = null)
    {
        Kind = kind;
        DocumentKey = documentKey ?? throw new ArgumentNullException(nameof(documentKey));
        PersistId = persistId ?? throw new ArgumentNullException(nameof(persistId));
        Description = description ?? "";
        Component = string.IsNullOrEmpty(component) ? null : component;
        ComponentPath = Component == null || string.IsNullOrEmpty(componentPath) ? null : componentPath;
    }

    public ReferenceKind Kind { get; }

    /// <summary>The document's full path, or for a document never saved its title.</summary>
    public string DocumentKey { get; }

    /// <summary>What IModelDocExtension.GetPersistReference3 returned for the entity.</summary>
    public byte[] PersistId { get; }

    /// <summary>What the entity was when the reference was issued; reported by reference_unresolved.</summary>
    public string Description { get; }

    /// <summary>
    /// For an entity of a component, issued in an assembly (design 455, Assembly references): the component's name at
    /// issue (sub-1/pin-1), so a reference that no longer resolves can say why (the component is suppressed, lightweight
    /// or missing). For a component reference, its own name. Null for a part's own entities.
    /// </summary>
    public string? Component { get; }

    /// <summary>The full path of the component's file at issue, so a reference never resolves into a file SOLIDWORKS substituted for it; null when it has none or <see cref="Component"/> is null.</summary>
    public string? ComponentPath { get; }
}

/// <summary>
/// Encodes and decodes references: <c>r1.&lt;kind&gt;.&lt;payload&gt;</c>, the payload base64url of a version byte and three
/// length-prefixed blocks (document key, persistent id, description at issue). Payload version 1 holds the blocks as they
/// are; version 2 holds them deflated, after the deflated length, and is issued whenever it is shorter (SOLIDWORKS'
/// persistent ids are long and repetitive: 3.A measured a face reference at 1047 characters). Versions 3 and 4 are 1 and
/// 2 with two more blocks, the component's name and file at issue, for an entity of a component named in an assembly
/// (design 455). All four are read, so a reference issued by an earlier add-in keeps working. A string that does not start with <c>r1.</c> is a name, not a
/// reference. Opaque to agents; only the kind is meant to be read.
/// </summary>
public static class References
{
    public const string Prefix = "r1.";

    const byte Plain = 1;
    const byte Deflated = 2;
    const byte PlainWithComponent = 3;
    const byte DeflatedWithComponent = 4;
    const int MaxBlock = 1 << 20;

    public static bool IsReference(string? text) => text != null && text.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Encode(ReferenceData data)
    {
        if (data.PersistId.Length == 0) throw new ArgumentException("a reference needs a persistent id", nameof(data));
        byte[] blocks;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                WriteBlock(writer, Encoding.UTF8.GetBytes(data.DocumentKey));
                WriteBlock(writer, data.PersistId);
                WriteBlock(writer, Encoding.UTF8.GetBytes(data.Description));
                if (data.Component != null)
                {
                    WriteBlock(writer, Encoding.UTF8.GetBytes(data.Component));
                    WriteBlock(writer, Encoding.UTF8.GetBytes(data.ComponentPath ?? ""));
                }
            }
            blocks = buffer.ToArray();
        }
        bool component = data.Component != null;
        byte[] deflated = Deflate(blocks);
        byte[] payload = deflated.Length + 4 < blocks.Length
            ? Concat(new[] { component ? DeflatedWithComponent : Deflated }, BitConverter.GetBytes(deflated.Length), deflated)
            : Concat(new[] { component ? PlainWithComponent : Plain }, blocks);
        return Prefix + WireNames.ForEnum(data.Kind) + "." + ToBase64Url(payload);
    }

    /// <summary>Decodes <paramref name="text"/>; invalid_argument naming <paramref name="field"/> when it is not a well-formed reference.</summary>
    public static ReferenceData Decode(string text, string field)
    {
        if (!IsReference(text)) throw Malformed(text, field, $"does not start with {Prefix}");
        int dot = text.IndexOf('.', Prefix.Length);
        if (dot < 0) throw Malformed(text, field, "has no payload");
        string kindName = text.Substring(Prefix.Length, dot - Prefix.Length);
        if (!WireNames.TryParseEnum(typeof(ReferenceKind), kindName, out var kind)) throw Malformed(text, field, $"names an unknown kind '{kindName}'");
        byte[] payload;
        try
        {
            payload = FromBase64Url(text.Substring(dot + 1));
        }
        catch (FormatException)
        {
            throw Malformed(text, field, "has a payload that is not base64url");
        }
        try
        {
            byte[] blocks = payload[0] switch
            {
                Plain or PlainWithComponent => Sub(payload, 1, payload.Length - 1),
                Deflated or DeflatedWithComponent => Inflate(payload, text, field),
                _ => throw Malformed(text, field, "has an unknown payload version"),
            };
            using var reader = new BinaryReader(new MemoryStream(blocks), Encoding.UTF8);
            string documentKey = Encoding.UTF8.GetString(ReadBlock(reader));
            byte[] persistId = ReadBlock(reader);
            string description = Encoding.UTF8.GetString(ReadBlock(reader));
            string? component = null, componentPath = null;
            if (payload[0] == PlainWithComponent || payload[0] == DeflatedWithComponent)
            {
                component = Encoding.UTF8.GetString(ReadBlock(reader));
                componentPath = Encoding.UTF8.GetString(ReadBlock(reader));
                if (component.Length == 0) throw Malformed(text, field, "has an empty component");
            }
            if (reader.BaseStream.Position != blocks.Length) throw Malformed(text, field, "has bytes after its payload");
            if (persistId.Length == 0 || documentKey.Length == 0) throw Malformed(text, field, "has an empty document or persistent id");
            return new ReferenceData((ReferenceKind)kind!, documentKey, persistId, description, component, componentPath);
        }
        catch (EndOfStreamException)
        {
            throw Malformed(text, field, "is cut short");
        }
        catch (InvalidDataException)
        {
            throw Malformed(text, field, "has a block longer than its payload");
        }
    }

    /// <summary>The kind of a reference string, or null when it is not a reference or its kind is unknown. Does not decode the payload.</summary>
    public static ReferenceKind? KindOf(string? text)
    {
        if (!IsReference(text)) return null;
        int dot = text!.IndexOf('.', Prefix.Length);
        if (dot < 0) return null;
        return WireNames.TryParseEnum(typeof(ReferenceKind), text.Substring(Prefix.Length, dot - Prefix.Length), out var kind)
            ? (ReferenceKind)kind! : (ReferenceKind?)null;
    }

    static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    /// <summary>A version 2 payload's blocks. Its deflated length must be exactly what follows it, so nothing cut short or padded is read.</summary>
    static byte[] Inflate(byte[] payload, string text, string field)
    {
        if (payload.Length < 5) throw new EndOfStreamException();
        int length = BitConverter.ToInt32(payload, 1);
        if (length != payload.Length - 5) throw Malformed(text, field, length > payload.Length - 5 || length < 0 ? "is cut short" : "has bytes after its payload");
        try
        {
            using var input = new DeflateStream(new MemoryStream(payload, 5, length), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > 3L * MaxBlock) throw new InvalidDataException();
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw Malformed(text, field, "has a payload that does not inflate");
        }
    }

    static byte[] Sub(byte[] bytes, int start, int count)
    {
        var sub = new byte[count];
        Array.Copy(bytes, start, sub, 0, count);
        return sub;
    }

    static byte[] Concat(params byte[][] parts)
    {
        using var all = new MemoryStream();
        foreach (var part in parts) all.Write(part, 0, part.Length);
        return all.ToArray();
    }

    static void WriteBlock(BinaryWriter writer, byte[] block)
    {
        writer.Write(block.Length);
        writer.Write(block);
    }

    static byte[] ReadBlock(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxBlock || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
        return reader.ReadBytes(length);
    }

    static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string text)
    {
        if (text.Length == 0 || text.IndexOfAny(new[] { '+', '/', '=' }) >= 0) throw new FormatException();
        string padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException();
        }
        return Convert.FromBase64String(padded);
    }

    static SwAgentException Malformed(string text, string field, string why) =>
        new SwAgentException(ErrorCode.InvalidArgument,
            $"{field} is not a reference this add-in issued: it {why}. Use a reference from a result (r1.<kind>.…) or a name",
            new JsonObject { ["field"] = field, ["reference"] = text.Length > 200 ? text.Substring(0, 200) + "…" : text });
}
