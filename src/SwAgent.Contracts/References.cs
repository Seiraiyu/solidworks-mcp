using System;
using System.IO;
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
}

/// <summary>What a reference string carries (design 371, References).</summary>
public sealed class ReferenceData
{
    public ReferenceData(ReferenceKind kind, string documentKey, byte[] persistId, string description)
    {
        Kind = kind;
        DocumentKey = documentKey ?? throw new ArgumentNullException(nameof(documentKey));
        PersistId = persistId ?? throw new ArgumentNullException(nameof(persistId));
        Description = description ?? "";
    }

    public ReferenceKind Kind { get; }

    /// <summary>The document's full path, or for a document never saved its title.</summary>
    public string DocumentKey { get; }

    /// <summary>What IModelDocExtension.GetPersistReference3 returned for the entity.</summary>
    public byte[] PersistId { get; }

    /// <summary>What the entity was when the reference was issued; reported by reference_unresolved.</summary>
    public string Description { get; }
}

/// <summary>
/// Encodes and decodes references: <c>r1.&lt;kind&gt;.&lt;payload&gt;</c>, the payload base64url of a version byte and three
/// length-prefixed blocks (document key, persistent id, description at issue). A string that does not start with
/// <c>r1.</c> is a name, not a reference. Opaque to agents; only the kind is meant to be read.
/// </summary>
public static class References
{
    public const string Prefix = "r1.";

    const byte PayloadVersion = 1;
    const int MaxBlock = 1 << 20;

    public static bool IsReference(string? text) => text != null && text.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Encode(ReferenceData data)
    {
        if (data.PersistId.Length == 0) throw new ArgumentException("a reference needs a persistent id", nameof(data));
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PayloadVersion);
            WriteBlock(writer, Encoding.UTF8.GetBytes(data.DocumentKey));
            WriteBlock(writer, data.PersistId);
            WriteBlock(writer, Encoding.UTF8.GetBytes(data.Description));
        }
        return Prefix + WireNames.ForEnum(data.Kind) + "." + ToBase64Url(buffer.ToArray());
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
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            if (reader.ReadByte() != PayloadVersion) throw Malformed(text, field, "has an unknown payload version");
            string documentKey = Encoding.UTF8.GetString(ReadBlock(reader));
            byte[] persistId = ReadBlock(reader);
            string description = Encoding.UTF8.GetString(ReadBlock(reader));
            if (reader.BaseStream.Position != payload.Length) throw Malformed(text, field, "has bytes after its payload");
            if (persistId.Length == 0 || documentKey.Length == 0) throw Malformed(text, field, "has an empty document or persistent id");
            return new ReferenceData((ReferenceKind)kind!, documentKey, persistId, description);
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
