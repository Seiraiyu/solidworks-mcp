using System;
using System.IO;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>The checks every operation makes on a file path it is given, each failing invalid_argument naming the field.</summary>
public static class PathRules
{
    /// <summary>A full path with one of <paramref name="extensions"/> to a file that exists; returned normalised.</summary>
    public static string Existing(string? path, string field, params string[] extensions)
    {
        string full = Full(path, field, extensions);
        if (!File.Exists(full)) throw Invalid(field, path!, $"{full} does not exist");
        return full;
    }

    /// <summary>
    /// A full path with one of <paramref name="extensions"/> in a folder that exists; refused when the file exists unless
    /// <paramref name="overwrite"/>. Returned normalised.
    /// </summary>
    public static string Writable(string? path, string field, bool overwrite, params string[] extensions)
    {
        string full = Full(path, field, extensions);
        string? folder = Path.GetDirectoryName(full);
        if (folder == null || !Directory.Exists(folder)) throw Invalid(field, path!, $"the folder {folder} does not exist");
        if (File.Exists(full) && !overwrite) throw Invalid(field, path!, $"{full} exists; pass overwrite: true to replace it");
        return full;
    }

    /// <summary>The document type a file's extension names.</summary>
    public static DocumentType TypeOf(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".SLDASM" => DocumentType.Assembly,
        ".SLDDRW" => DocumentType.Drawing,
        _ => DocumentType.Part,
    };

    static string Full(string? path, string field, string[] extensions)
    {
        if (path == null || string.IsNullOrWhiteSpace(path)) throw Invalid(field, path ?? "", "a full path is required");
        if (!IsFull(path)) throw Invalid(field, path, "it must be a full path, such as C:\\parts\\bracket.SLDPRT");
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
        {
            throw Invalid(field, path, e.Message);
        }
        if (!extensions.Any(x => full.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw Invalid(field, path, $"it must end in {string.Join(" or ", extensions)}");
        return full;
    }

    /// <summary>A drive path (C:\…) or a UNC path (\\server\share\…); not one relative to the current drive or folder.</summary>
    internal static bool IsFull(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2;

    static SwAgentException Invalid(string field, string path, string why) =>
        new SwAgentException(ErrorCode.InvalidArgument, $"{field} '{path}': {why}", new JsonObject { ["field"] = field, ["path"] = path });
}
