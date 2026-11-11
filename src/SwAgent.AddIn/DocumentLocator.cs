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

    /// <summary>As <see cref="Find"/>, and wrong_document_type for a drawing: the part or assembly a model, selection or configuration operation reads (plan 4.B).</summary>
    public static ISwDocument FindModel(ISolidWorksApplication sw, string? document, string operation, string field = "document")
    {
        var doc = Find(sw, document, field);
        var info = doc.Info();
        if (info.Type == DocumentType.Drawing)
        {
            throw new SwAgentException(ErrorCode.WrongDocumentType,
                $"{operation} works on parts and assemblies, and {info.Title} is a drawing",
                new JsonObject { ["document"] = Label(info), ["type"] = WireNames.ForEnum(info.Type) });
        }
        return doc;
    }

    /// <summary>As <see cref="Find"/>, and wrong_document_type unless it is a part.</summary>
    public static ISwDocument FindPart(ISolidWorksApplication sw, string? document, string operation, string field = "document") =>
        FindOfType(sw, document, operation, DocumentType.Part, field);

    /// <summary>As <see cref="Find"/>, and wrong_document_type unless it is an assembly; returns the document and the assembly it is.</summary>
    public static (ISwDocument Doc, ISwAssembly Assembly) FindAssembly(ISolidWorksApplication sw, string? document, string operation, string field = "document")
    {
        var doc = FindOfType(sw, document, operation, DocumentType.Assembly, field);
        return (doc, doc.Assembly() ?? throw new SwAgentException(ErrorCode.ApiError, $"{doc.Info().Title} says it is an assembly, but SOLIDWORKS gives no assembly for it"));
    }

    static ISwDocument FindOfType(ISolidWorksApplication sw, string? document, string operation, DocumentType type, string field)
    {
        var doc = Find(sw, document, field);
        var info = doc.Info();
        if (info.Type != type)
        {
            throw new SwAgentException(ErrorCode.WrongDocumentType,
                $"{operation} works on {(type == DocumentType.Assembly ? "assemblies" : "parts")}, and {info.Title} is {Article(info.Type)}",
                new JsonObject { ["document"] = Label(info), ["type"] = WireNames.ForEnum(info.Type) });
        }
        return doc;
    }

    static string Article(DocumentType type) => type switch
    {
        DocumentType.Assembly => "an assembly",
        DocumentType.Drawing => "a drawing",
        _ => "a part",
    };

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
