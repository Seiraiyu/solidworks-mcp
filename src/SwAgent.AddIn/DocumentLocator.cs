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
/// contracts): absent means the active document; otherwise its full path, its title, or its file name with or without
/// the extension. An extension names the type too, and a name that fits more than one open document is refused, never
/// resolved to one of them (issue #36).
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
        // Issue #36: never pick one of several (a part and its drawing share a name); the caller names which.
        var candidates = matches.Select(d => Label(d.Info())).ToList();
        details["candidates"] = candidates.Select(c => (object?)c).ToList();
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"'{document}' names more than one open document ({string.Join(", ", candidates)}); name one by its full path, or by its file name with the extension (.SLDPRT, .SLDASM or .SLDDRW)",
            details);
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

    /// <summary>As <see cref="Find"/>, and wrong_document_type unless it is a drawing (design 611); returns the document and the drawing it is.</summary>
    public static (ISwDocument Doc, ISwDrawing Drawing) FindDrawing(ISolidWorksApplication sw, string? document, string operation, string field = "document")
    {
        var doc = FindOfType(sw, document, operation, DocumentType.Drawing, field);
        return (doc, doc.Drawing() ?? throw new SwAgentException(ErrorCode.ApiError, $"{doc.Info().Title} says it is a drawing, but SOLIDWORKS gives no drawing for it"));
    }

    static ISwDocument FindOfType(ISolidWorksApplication sw, string? document, string operation, DocumentType type, string field)
    {
        var doc = Find(sw, document, field);
        var info = doc.Info();
        if (info.Type != type)
        {
            var details = new JsonObject { ["document"] = Label(info), ["type"] = WireNames.ForEnum(info.Type) };
            string message = $"{operation} works on {Plural(type)}, and {info.Title} is {Article(info.Type)}";
            if (type == DocumentType.Part && info.Type == DocumentType.Assembly)
            {
                // Design 455, Error handling: name what to do instead.
                string instead = InsteadInAssembly.TryGetValue(operation, out var tool) ? tool : PartOnly;
                details["instead"] = instead;
                message += "; " + instead;
            }
            if (type == DocumentType.Drawing)
            {
                string instead = operation == "export.drawing" ? ModelExports : DrawingInstead;
                details["instead"] = instead;
                message += "; " + instead;
            }
            else if (info.Type == DocumentType.Drawing && operation.StartsWith("export.", StringComparison.Ordinal))
            {
                // Design 611, Error handling: a drawing given to a model export names the drawing export.
                details["instead"] = DrawingExport;
                message += "; " + DrawingExport;
            }
            throw new SwAgentException(ErrorCode.WrongDocumentType, message, details);
        }
        return doc;
    }

    /// <summary>What a part-only operation asked of an assembly should use instead (design 455, Inspection and the part tools).</summary>
    static readonly Dictionary<string, string> InsteadInAssembly = new Dictionary<string, string>
    {
        ["model.tree"] = "assembly.components lists an assembly's components, and model.find its components' faces, edges and vertices",
        ["model.dimensions"] = "open a component's part with document.open to read its dimensions",
        ["export.part"] = "export.assembly writes an assembly as STEP, Parasolid, STL or 3MF",
    };

    /// <summary>The way to change a component's geometry, for every other part-only operation.</summary>
    const string PartOnly = "to change a component's geometry, open its part with document.open, change it there, then document.rebuild the assembly";

    /// <summary>What a model export asked of a drawing should use instead (design 611, Error handling).</summary>
    const string DrawingExport = "export.drawing writes a drawing's sheets as PDF, DXF or DWG";

    /// <summary>What export.drawing asked of a part or assembly should use instead.</summary>
    const string ModelExports = "export.part writes a part, export.assembly an assembly (STEP, Parasolid, STL or 3MF) and export.flat_pattern a sheet-metal part's flat pattern (DXF or DWG)";

    /// <summary>What a drawing operation asked of a part or assembly should use instead (design 611, Error handling).</summary>
    const string DrawingInstead = "name a drawing: document.new with type drawing makes one, document.open opens one, and view.create puts a part or assembly on it";

    static string Plural(DocumentType type) => type switch
    {
        DocumentType.Assembly => "assemblies",
        DocumentType.Drawing => "drawings",
        _ => "parts",
    };

    static string Article(DocumentType type) => type switch
    {
        DocumentType.Assembly => "an assembly",
        DocumentType.Drawing => "a drawing",
        _ => "a part",
    };

    /// <summary>
    /// Whether <paramref name="name"/> names the document (issue #36): a full path names the file at it; otherwise the
    /// document's exact title, or its file name. A file name with an extension names only a document of that type, and
    /// one without names every document whose file name it is less the extension.
    /// </summary>
    static bool Names(DocumentInfo info, string name)
    {
        if (Path.IsPathRooted(name))
            return info.Path != null && DocumentKeys.Same(Path.GetFullPath(name), Path.GetFullPath(info.Path));
        if (string.Equals(info.Title, name, StringComparison.OrdinalIgnoreCase)) return true;
        var named = TypeOf(Path.GetExtension(name));
        if (named != null && named != info.Type) return false;
        string stem = named == null ? name : name.Substring(0, name.Length - Path.GetExtension(name).Length);
        return string.Equals(Stem(info), stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The document's file name without its extension: from its path once saved, else from its title, which for a
    /// drawing ends in " - " and the active sheet's name, and shows the extension or not as Windows is set.
    /// </summary>
    static string Stem(DocumentInfo info)
    {
        if (info.Path != null) return Path.GetFileNameWithoutExtension(info.Path);
        string title = info.Title;
        if (info.Type == DocumentType.Drawing)
        {
            int sheet = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sheet > 0) title = title.Substring(0, sheet);
        }
        return TypeOf(Path.GetExtension(title)) != null ? title.Substring(0, title.Length - Path.GetExtension(title).Length) : title;
    }

    /// <summary>The document type a SOLIDWORKS file extension says, or null for any other extension.</summary>
    static DocumentType? TypeOf(string extension) => extension.ToUpperInvariant() switch
    {
        ".SLDPRT" => DocumentType.Part,
        ".SLDASM" => DocumentType.Assembly,
        ".SLDDRW" => DocumentType.Drawing,
        _ => null,
    };

    internal static string Label(DocumentInfo info) => info.Path ?? info.Title;
}
