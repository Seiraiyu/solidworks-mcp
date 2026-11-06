using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>What the file an export wrote holds, as read back from disk.</summary>
public sealed class ExportFile
{
    public long Bytes { get; set; }

    /// <summary>What the file starts with, as the format's check read it (ISO-10303-21;, a Parasolid header, binary STL, a zip).</summary>
    public string Header { get; set; } = "";

    /// <summary>A binary STL's triangle count; null for the other formats.</summary>
    public int? Triangles { get; set; }

    /// <summary>A STEP file's FILE_SCHEMA; null for the other formats.</summary>
    public string? Schema { get; set; }

    /// <summary>Each check made and what it found.</summary>
    public List<string> Checks { get; } = new List<string>();
}

/// <summary>
/// Proves on disk that an export wrote what was asked (design 371, Export): the file exists, was written by this call
/// and is not empty, and its header is the format's: <c>ISO-10303-21;</c> and the schema asked for STEP, a Parasolid
/// transmit header, a binary STL whose size is its triangle count's (at least four per solid body), a 3MF zip holding
/// <c>3D/3dmodel.model</c>. Otherwise verification_failed naming the check, what was expected and what was found.
/// </summary>
public static class ExportFileCheck
{
    /// <summary>File times are not exact; a write within this much before the call began still counts as this call's.</summary>
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>The first line of every Parasolid text transmit file.</summary>
    public const string ParasolidHeader = "**ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public const string ThreeMfModel = "3D/3dmodel.model";

    static readonly Regex FileSchema = new Regex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']*)'", RegexOptions.CultureInvariant);

    public static ExportFile Check(string path, ExportFormat format, DateTime startedUtc, StepSchema stepSchema, int bodies)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw Failed("file_written", path, "the file", "no file", $"SOLIDWORKS reported {path} exported, but the file is not there");
        if (info.LastWriteTimeUtc < startedUtc - Slack)
            throw Failed("file_written", path, $"written after {startedUtc:o}", info.LastWriteTimeUtc.ToString("o"), $"{path} was last written at {info.LastWriteTimeUtc:o}, before this call: SOLIDWORKS wrote nothing");
        if (info.Length == 0) throw Failed("file_written", path, "more than 0 bytes", "0 bytes", $"SOLIDWORKS wrote {path} empty");
        var file = new ExportFile { Bytes = info.Length };
        file.Checks.Add($"{Path.GetFileName(path)} written by this call, {info.Length} bytes");
        switch (format)
        {
            case ExportFormat.Step:
                Step(path, stepSchema, file);
                break;
            case ExportFormat.Parasolid:
                Parasolid(path, file);
                break;
            case ExportFormat.Stl:
                Stl(path, bodies, file);
                break;
            default:
                ThreeMf(path, file);
                break;
        }
        return file;
    }

    static void Step(string path, StepSchema schema, ExportFile file)
    {
        string head = Head(path, 65536);
        if (!head.TrimStart().StartsWith("ISO-10303-21;", StringComparison.Ordinal))
            throw Failed("file_header", path, "ISO-10303-21;", Snippet(head), $"{path} is not a STEP file: it does not start ISO-10303-21;");
        file.Header = "ISO-10303-21;";
        var m = FileSchema.Match(head);
        string found = m.Success ? m.Groups[1].Value : "";
        string wanted = schema == StepSchema.Ap242 ? "AP242" : "AUTOMOTIVE_DESIGN";
        if (found.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw Failed("step_schema", path, wanted, found.Length == 0 ? "no FILE_SCHEMA" : found,
                $"{path} was asked as {WireNames.ForEnum(schema)} ({wanted}) but its FILE_SCHEMA is {(found.Length == 0 ? "missing" : found)}");
        }
        file.Schema = found;
        file.Checks.Add($"starts ISO-10303-21; with FILE_SCHEMA {found} ({WireNames.ForEnum(schema)})");
    }

    static void Parasolid(string path, ExportFile file)
    {
        string head = Head(path, 256);
        if (!head.StartsWith(ParasolidHeader, StringComparison.Ordinal))
            throw Failed("file_header", path, ParasolidHeader, Snippet(head), $"{path} is not a Parasolid text transmit file: its first line is not the transmit header");
        file.Header = ParasolidHeader;
        file.Checks.Add("starts with the Parasolid transmit header");
    }

    static void Stl(string path, int bodies, ExportFile file)
    {
        long length = new FileInfo(path).Length;
        byte[] header = new byte[84];
        int read;
        using (var s = File.OpenRead(path)) read = s.Read(header, 0, header.Length);
        uint count = read == 84 ? BitConverter.ToUInt32(header, 80) : 0;
        if (read < 84 || length != 84 + 50L * count)
        {
            bool ascii = Encoding.ASCII.GetString(header, 0, Math.Min(read, 5)) == "solid";
            throw Failed("file_header", path, "a binary STL of 84 + 50 × its triangle count bytes", ascii ? "an ASCII STL" : $"{length} bytes for a count of {count}",
                $"{path} is not the binary STL asked for: {(ascii ? "it is ASCII" : $"{length} bytes do not hold the {count} triangles its header counts")}");
        }
        file.Header = "binary STL";
        file.Triangles = (int)count;
        int least = 4 * Math.Max(1, bodies);
        if (count < least)
            throw Failed("stl_triangles", path, $"at least {least}", count.ToString(), $"{path} holds {count} triangles; {bodies} closed solid bodies need at least {least}");
        file.Checks.Add($"binary STL of {count} triangles ({length} = 84 + 50 × {count} bytes, at least {least} for {bodies} bodies)");
    }

    static void ThreeMf(string path, ExportFile file)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K' || bytes[2] != 3 || bytes[3] != 4)
            throw Failed("file_header", path, "a zip (PK\\x03\\x04)", Snippet(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 16))), $"{path} is not a 3MF file: it is not a zip");
        var names = ZipNames(bytes);
        file.Header = "zip";
        if (!names.Any(n => string.Equals(n.TrimStart('/'), ThreeMfModel, StringComparison.OrdinalIgnoreCase)))
            throw Failed("threemf_model", path, ThreeMfModel, string.Join(", ", names.DefaultIfEmpty("no entries")), $"{path} is a zip without {ThreeMfModel}, so not a 3MF package");
        file.Checks.Add($"a zip of {names.Count} entries holding {ThreeMfModel}");
    }

    /// <summary>The entry names in a zip's central directory; empty when it has none that can be read.</summary>
    internal static List<string> ZipNames(byte[] zip)
    {
        var names = new List<string>();
        int end = -1;
        for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 65535); i--)
        {
            if (BitConverter.ToUInt32(zip, i) == 0x06054b50)
            {
                end = i;
                break;
            }
        }
        if (end < 0) return names;
        int entries = BitConverter.ToUInt16(zip, end + 10);
        long at = BitConverter.ToUInt32(zip, end + 16);
        for (int n = 0; n < entries && at + 46 <= zip.Length; n++)
        {
            if (BitConverter.ToUInt32(zip, (int)at) != 0x02014b50) break;
            int nameLength = BitConverter.ToUInt16(zip, (int)at + 28);
            int extra = BitConverter.ToUInt16(zip, (int)at + 30);
            int comment = BitConverter.ToUInt16(zip, (int)at + 32);
            if (at + 46 + nameLength > zip.Length) break;
            names.Add(Encoding.UTF8.GetString(zip, (int)at + 46, nameLength));
            at += 46 + nameLength + extra + comment;
        }
        return names;
    }

    static string Head(string path, int bytes)
    {
        using var s = File.OpenRead(path);
        var buffer = new byte[Math.Min(bytes, s.Length)];
        int read = s.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    static string Snippet(string text)
    {
        string line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length > 60 ? line.Substring(0, 60) + "…" : line;
    }

    static SwAgentException Failed(string check, string path, string expected, string measured, string message) =>
        new SwAgentException(ErrorCode.VerificationFailed, message,
            new JsonObject { ["check"] = check, ["path"] = path, ["expected"] = expected, ["measured"] = measured });
}
