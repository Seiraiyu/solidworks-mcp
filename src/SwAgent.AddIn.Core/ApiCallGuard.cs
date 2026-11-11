using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>What an object an api.call walks through is, for the refused-member rules.</summary>
public enum ApiObjectKind
{
    /// <summary>SOLIDWORKS itself (ISldWorks).</summary>
    App,

    /// <summary>A document (IModelDoc2, and the IPartDoc, IAssemblyDoc or IDrawingDoc of the same object).</summary>
    Document,

    /// <summary>Anything else: an extension, a manager, a feature, a face, …</summary>
    Other,
}

/// <summary>
/// The escape hatch's guard rails on the member path (design 371, Escape hatch). Refused: the members that end the
/// session, close documents or run code -- ExitApp, CloseAllDocuments, CloseDoc, QuitDoc, CloseAndReopen, RunMacro,
/// RunMacro2, RunCommand, LoadAddIn, UnloadAddIn wherever they are reached -- and every other member whose name starts
/// Close or Quit on ISldWorks or IModelDoc2. IDispatch matches names without regard to case, so every comparison
/// ignores case; a Close or Quit member is told by SOLIDWORKS' own spelling of its name (ClosestDistance measures and
/// is not refused), and one whose spelling is not known is refused. Every segment of the path is checked against the
/// object it is called on, so a refused member cannot be reached as an intermediate step either. Each segment must be
/// a plain identifier, so IDispatch's [DISPID=n] form cannot name a member by number.
/// The design's "close documents" also covers the members that close a document under another name -- reloading it
/// (every member whose name contains Reload: ReloadOrReplace, ReloadWithReferences, FileReload, ComponentReload…),
/// replacing it (ReplaceReferencedDocument, ReplaceComponents…, ReplaceReference, ReplaceViewModel), or releasing its
/// file (ForceReleaseLocks, SetReadOnlyState) -- and its "end the session" covers ConnectToSW and DisconnectFromSW, an
/// add-in's own start and stop, the add-in this hatch runs in included. These are refused on any object too.
/// </summary>
public static class ApiCallGuard
{
    /// <summary>The members refused on any object, as the design names them.</summary>
    public static readonly IReadOnlyList<string> Refused = new[]
    {
        "ExitApp", "CloseAllDocuments", "CloseDoc", "QuitDoc", "CloseAndReopen",
        "RunMacro", "RunMacro2", "RunCommand", "LoadAddIn", "UnloadAddIn",
    };

    /// <summary>
    /// Members refused on any object because they close, reload or replace a document, or release its file, under a
    /// name the design's list does not give (swept from the 2026 interop); and every member whose name contains Reload.
    /// </summary>
    public static readonly IReadOnlyList<string> ClosesOrReplaces = new[]
    {
        "ForceReleaseLocks", "SetReadOnlyState", "ReplaceReferencedDocument", "ReplaceComponents", "ReplaceComponents2",
        "ReplacePLMComponents", "ReplaceReference", "ReplaceViewModel",
    };

    /// <summary>
    /// Members refused on any object because they run code the design's list does not name (owner's answer to Q241,
    /// swept from the 2026 interop): attached macros, journals, batch processes, callbacks into add-ins, macro features,
    /// and the add-in objects themselves.
    /// </summary>
    public static readonly IReadOnlyList<string> RunsCode = new[]
    {
        "RunAttachedMacro", "RunJournalCmd", "RunBatchSaveProcess", "CallBack", "AddCallback", "SetAddinCallbackInfo",
        "SetAddinCallbackInfo2", "InsertMacroFeature", "InsertMacroFeature2", "InsertMacroFeature3", "IInsertMacroFeature",
        "IInsertMacroFeature2", "IInsertMacroFeature3", "ToolsMacro", "RunXMLCommand", "WriteJournalToMacro",
        "EmbedMacroFile", "GetAddInObject",
    };

    /// <summary>An add-in's own start and stop (ISwAddin), refused on any object: calling them ends a session.</summary>
    public static readonly IReadOnlyList<string> EndsSession = new[] { "ConnectToSW", "DisconnectFromSW" };

    /// <summary>The most segments a member path may have.</summary>
    public const int MaxSegments = 16;

    // \z, not $: in .NET, $ also matches before a final newline, which would let "CloseDoc\n" past the name lists.
    static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>A Close or Quit member as SOLIDWORKS spells it: the word, then nothing, a capital, a digit or an underscore.</summary>
    static readonly Regex ClosingWord = new Regex("^(Close|Quit)($|[A-Z0-9_])", RegexOptions.CultureInvariant);

    /// <summary>The path's segments; invalid_argument (field member) when it is empty, too long, or a segment is not a plain identifier.</summary>
    public static IReadOnlyList<string> Split(string? member)
    {
        if (member == null || member.Trim().Length == 0)
            throw Invalid("member is required: a member of the target, or a dotted path to one, such as Extension.GetMassProperties2", member ?? "");
        var segments = member.Split('.');
        if (segments.Length > MaxSegments)
            throw Invalid($"member has {segments.Length} segments; at most {MaxSegments}", member);
        foreach (var s in segments)
        {
            if (!Identifier.IsMatch(s))
            {
                throw Invalid(
                    $"member segment '{s}' is not a member name (letters, digits and underscores, not starting with a digit); write the path as names separated by dots, such as Extension.SelectByID2",
                    member);
            }
        }
        return segments;
    }

    /// <summary>
    /// Throws invalid_argument with details.refused when <paramref name="segment"/>, called on an object of
    /// <paramref name="kind"/>, is a refused member. <paramref name="spelling"/> is SOLIDWORKS' own spelling of the
    /// member on that object when it is known, else null.
    /// </summary>
    public static void Check(string segment, ApiObjectKind kind, string? spelling, string member)
    {
        string? listed = Refused.FirstOrDefault(r => string.Equals(r, segment, StringComparison.OrdinalIgnoreCase));
        if (listed != null) throw Refusal(listed, ReasonOf(listed), member);
        string? running = RunsCode.FirstOrDefault(r => string.Equals(r, segment, StringComparison.OrdinalIgnoreCase));
        if (running != null) throw Refusal(running, "runs_code", member);
        string? ending = EndsSession.FirstOrDefault(r => string.Equals(r, segment, StringComparison.OrdinalIgnoreCase));
        if (ending != null) throw Refusal(ending, "ends_session", member);
        string? replacing = ClosesOrReplaces.FirstOrDefault(r => string.Equals(r, segment, StringComparison.OrdinalIgnoreCase));
        if (replacing == null && segment.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0) replacing = spelling ?? segment;
        if (replacing != null) throw Refusal(replacing, "closes_document", member);
        if (kind == ApiObjectKind.Other) return;
        bool closing = spelling != null
            ? ClosingWord.IsMatch(spelling)
            : segment.StartsWith("close", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("quit", StringComparison.OrdinalIgnoreCase);
        if (closing) throw Refusal(spelling ?? segment, "closes_document", member, kind);
    }

    /// <summary>Every segment against the names refused on any object, before anything is looked up or called.</summary>
    public static void CheckNames(IReadOnlyList<string> segments, string member)
    {
        foreach (var s in segments) Check(s, ApiObjectKind.Other, null, member);
    }

    static string ReasonOf(string listed) => listed switch
    {
        "ExitApp" => "ends_session",
        "RunMacro" or "RunMacro2" or "RunCommand" or "LoadAddIn" or "UnloadAddIn" => "runs_code",
        _ => "closes_document",
    };

    static SwAgentException Refusal(string name, string reason, string member, ApiObjectKind? on = null)
    {
        string why = reason switch
        {
            "ends_session" => $"{name} ends SOLIDWORKS or an add-in's session, which this project never does",
            "runs_code" => $"{name} runs code inside SOLIDWORKS (a macro, a command or an add-in), which the escape hatch never does",
            _ => (on == null ? $"{name} closes, reloads or replaces a document" : $"{name} is a Close or Quit member of {(on == ApiObjectKind.App ? "ISldWorks" : "IModelDoc2")}") +
                 ", which the escape hatch never calls: close a document only with document.close, which refuses one with unsaved changes unless discard, and one the agent did not open or create unless users_document",
        };
        return new SwAgentException(ErrorCode.InvalidArgument, $"api.call refuses {name}: {why}",
            new JsonObject { ["field"] = "member", ["member"] = member, ["refused"] = name, ["reason"] = reason });
    }

    static SwAgentException Invalid(string message, string member) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = "member", ["member"] = member });
}
