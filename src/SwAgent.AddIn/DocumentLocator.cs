using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Finds the document an operation acts on from its optional <c>document</c> field (design 371, Protocol and
/// contracts): absent means the active document; otherwise its full path, its title, or its title without the extension
/// (SOLIDWORKS shows titles with or without it, as Windows is set).
/// </summary>
public static class DocumentLocator
{
    public static ISwDocument Find(ISolidWorksApplication sw, string? document, string field = "document")
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return sw.ActiveDoc() ?? throw new SwAgentException(ErrorCode.InvalidArgument,
                "no document is open in SOLIDWORKS; create one with document.new or open one with document.open",
                new JsonObject { ["field"] = field });
        }
        var open = sw.Documents();
        var matches = open.Where(d => Names(d.Info(), document!)).ToList();
        if (matches.Count == 1) return matches[0];
        var details = new JsonObject
        {
            ["field"] = field,
            ["document"] = document,
            ["open"] = open.Select(d => (object?)Label(d.Info())).ToList(),
        };
        if (matches.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, $"no open document is '{document}'; open documents are listed in details.open", details);
        throw new SwAgentException(ErrorCode.InvalidArgument, $"more than one open document is called '{document}'; name it by its full path", details);
    }

    /// <summary>As <see cref="Find"/>, and wrong_document_type unless it is a part.</summary>
    public static ISwDocument FindPart(ISolidWorksApplication sw, string? document, string operation, string field = "document")
    {
        var doc = Find(sw, document, field);
        var info = doc.Info();
        if (info.Type != DocumentType.Part)
        {
            throw new SwAgentException(ErrorCode.WrongDocumentType,
                $"{operation} works on parts, and {info.Title} is {(info.Type == DocumentType.Assembly ? "an assembly" : "a drawing")}",
                new JsonObject { ["document"] = Label(info), ["type"] = WireNames.ForEnum(info.Type) });
        }
        return doc;
    }

    static bool Names(DocumentInfo info, string name)
    {
        if (info.Path != null && Path.IsPathRooted(name) && DocumentKeys.Same(Path.GetFullPath(name), Path.GetFullPath(info.Path))) return true;
        if (string.Equals(info.Title, name, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(StripExtension(info.Title), StripExtension(name), StringComparison.OrdinalIgnoreCase);
    }

    static string StripExtension(string title)
    {
        string ext = Path.GetExtension(title);
        return ext.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) || ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase)
            ? title.Substring(0, title.Length - ext.Length)
            : title;
    }

    internal static string Label(DocumentInfo info) => info.Path ?? info.Title;
}
