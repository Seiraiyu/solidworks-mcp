using System.Collections.Generic;
using System.ComponentModel;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

[Operation("api.call", typeof(ApiCallResult))]
[Description("SOLIDWORKS' own units: metres and radians, not the mm and degrees of every other tool. " +
    "The escape hatch: calls any member of SOLIDWORKS' API that the other tools do not cover yet, by late binding. " +
    "target is app (ISldWorks), document (the document's IModelDoc2) or a reference (the face, edge, feature or other entity it names; a feature's, plane's, axis's or point's name works too, and front, top, right and origin). " +
    "member is a dotted path: every part but the last a property or a method without arguments, the last called with args (Extension.GetMassProperties2). " +
    "args are JSON; {\"ref\": \"r1...\"} passes the entity a reference (or a feature's name) names. The result is the member's value as JSON, ref and out arguments in out, and an object as its reference when SOLIDWORKS can persist it, else only its interface's name: there are no handles, so a chain of calls is one dotted member. " +
    "With rebuild (the default for document and reference targets) the document is rebuilt afterwards and its rebuild errors returned; a call that rebuilds waits (busy, user_editing) while the user is editing a sketch or has a command open, and one with rebuild false, or on app, does not wait. Nothing is checked and nothing is rolled back: it does what it is told. " +
    "Refused: the members that end SOLIDWORKS, close documents or run code (ExitApp, CloseAllDocuments, CloseDoc, QuitDoc, CloseAndReopen and every other Close or Quit member of ISldWorks and IModelDoc2, RunMacro, RunMacro2, RunCommand, LoadAddIn, UnloadAddIn), and those that close a document under another name (every member containing Reload, ForceReleaseLocks, SetReadOnlyState, the Replace… members that swap a document, ConnectToSW, DisconnectFromSW), and every other member that runs code (RunAttachedMacro, RunJournalCmd, RunBatchSaveProcess, the callback members, the macro-feature members, RunXMLCommand, WriteJournalToMacro, EmbedMacroFile, GetAddInObject); close a document with document.close. " +
    "Every call is logged in full to api-calls.log. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ApiCallRequest
{
    [Required]
    [Description("What to call the member on: app (ISldWorks), document (the document's IModelDoc2), or an entity of the document: its reference (r1.…), or the name of a feature, plane, axis or point (Boss-Extrude1; front, top, right, origin).")]
    public string Target { get; set; } = "";

    [Description("The document: its title or full path. The active document when absent. The one a document or reference target is in, and the one rebuilt.")]
    public string? Document { get; set; }

    [Required]
    [Description("The member, or a dotted path to it: SOLIDWORKS' API names, for example GetTitle, Extension.GetMassProperties2, FeatureManager.FeatureExtrusion3.")]
    public string Member { get; set; } = "";

    [Description("The last member's arguments in order, in SOLIDWORKS' units (metres, radians): numbers, strings, booleans, arrays, null, and {\"ref\": \"r1...\"} for an entity (a reference, or the name of a feature, plane, axis or point). A placeholder (0 or null) for each out argument.")]
    public List<object?>? Args { get; set; }

    [Description("Rebuild the document afterwards and report its rebuild errors. Default true for document and reference targets, false for app. A call that rebuilds waits (busy) while the user is editing a sketch or has a command open; one that does not rebuild never waits.")]
    public bool? Rebuild { get; set; }
}

[Description("What the member returned, as JSON, and the document afterwards.")]
public sealed class ApiCallResult
{
    [Required]
    [Description("The member path as SOLIDWORKS spells it.")]
    public string Member { get; set; } = "";

    [Required]
    [Description("The interface of each object the path was called on, in order, for example IModelDoc2, IModelDocExtension.")]
    public List<string> Interfaces { get; set; } = new List<string>();

    [Description("The document a document or reference target was in, or the one rebuilt; absent for an app call that rebuilt nothing.")]
    public DocumentInfo? Document { get; set; }

    [Description("The member's value, in SOLIDWORKS' units: a number, string, boolean or array as SOLIDWORKS returned it (enums as their number, NaN as null); an object as {\"ref\": \"r1...\", \"interface\": \"IFace2\"} when SOLIDWORKS can persist it, else {\"interface\": \"IModelDocExtension\"} only. Absent for nothing returned.")]
    public object? Result { get; set; }

    [Required]
    [Description("Each ref or out argument after the call, by its parameter's name, for example {\"Status\": 0}.")]
    public JsonObject Out { get; set; } = new JsonObject();

    [Description("When the document was rebuilt: its rebuild errors and warnings afterwards.")]
    public RebuildStatus? Rebuild { get; set; }

    [Description("How long the call took, ms.")]
    public long DurationMs { get; set; }
}
