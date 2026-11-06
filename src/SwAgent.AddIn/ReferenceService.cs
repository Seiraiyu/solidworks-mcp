using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Issues and resolves references (design 371, References). A reference names its document; it resolves only in that
/// document, following saves under a new name, and only to an entity of its own kind. It never substitutes a nearby
/// entity: anything else is reference_unresolved, with what the entity was when the reference was issued. A string that
/// is not a reference is a name: front, top, right and origin first (the template's own planes and origin, whatever
/// their language), then a feature's exact name. UI thread only.
/// </summary>
public sealed class ReferenceService
{
    static readonly string[] StandardPlaneNames = { "front", "top", "right" };

    readonly RenameMap _renames;

    public ReferenceService(RenameMap renames)
    {
        _renames = renames;
    }

    /// <summary>The entity's description with its reference, when SOLIDWORKS can give one.</summary>
    public EntityInfo Describe(ISwDocument doc, object entity)
    {
        var info = doc.Describe(entity);
        info.Reference = Issue(doc, entity, info);
        return info;
    }

    /// <summary>A reference to <paramref name="entity"/> in <paramref name="doc"/>, or null when SOLIDWORKS gives no persistent id.</summary>
    public string? Issue(ISwDocument doc, object entity, EntityInfo info)
    {
        var id = doc.PersistId(entity);
        if (id == null || id.Length == 0) return null;
        return References.Encode(new ReferenceData(info.Kind, DocumentKeys.Of(doc.Info()), id, EntityFilter.Describe(info)));
    }

    /// <summary>
    /// The entity <paramref name="text"/> (a reference or a name) names in <paramref name="doc"/>, which must be one of
    /// <paramref name="accepted"/> kinds (any when none are given). invalid_argument for a malformed reference or an
    /// unknown name; reference_unresolved for a reference that no longer finds its entity.
    /// </summary>
    public (object Entity, ReferenceKind Kind) Resolve(ISwDocument doc, string text, string field, params ReferenceKind[] accepted)
    {
        var (entity, kind) = References.IsReference(text) ? ResolveReference(doc, text, field) : ResolveName(doc, text, field);
        if (accepted.Length > 0 && !accepted.Contains(kind))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field} names a {Wire(kind)}, but this needs {string.Join(" or ", accepted.Select(Wire))}",
                new JsonObject { ["field"] = field, ["kind"] = Wire(kind), ["accepted"] = accepted.Select(k => (object?)Wire(k)).ToList() });
        }
        return (entity, kind);
    }

    /// <summary>
    /// For api.call only: as <see cref="Resolve"/>, except that a feature SOLIDWORKS reports suppressed resolves, to the
    /// entity SOLIDWORKS returns for that very reference (never another), so the escape hatch can act on it (unsuppress
    /// it, read it). Anything else that does not resolve, a deleted feature or a suppressed face included, is still
    /// reference_unresolved.
    /// </summary>
    public (object Entity, ReferenceKind Kind) ResolveForApiCall(ISwDocument doc, string text, string field) =>
        References.IsReference(text) ? ResolveReference(doc, text, field, allowSuppressedFeature: true) : ResolveName(doc, text, field);

    (object, ReferenceKind) ResolveReference(ISwDocument doc, string text, string field, bool allowSuppressedFeature = false)
    {
        var data = References.Decode(text, field);
        string docKey = DocumentKeys.Of(doc.Info());
        string now = _renames.Current(data.DocumentKey);
        if (!DocumentKeys.Same(now, docKey))
            throw Unresolved(text, field, data, "other_document", $"it belongs to {now}, not {docKey}");
        var entity = doc.Resolve(data.PersistId, out var state);
        bool suppressedFeature = allowSuppressedFeature && state == PersistState.Suppressed && data.Kind == ReferenceKind.Feature && entity != null;
        if ((state != PersistState.Ok && !suppressedFeature) || entity == null)
        {
            string reason = state switch
            {
                PersistState.Deleted => "deleted",
                PersistState.Suppressed => "suppressed",
                _ => "not_found",
            };
            string why = reason == "not_found" ? "SOLIDWORKS no longer finds it" : $"it was {reason}";
            if (allowSuppressedFeature && state == PersistState.Suppressed && data.Kind == ReferenceKind.Feature && entity == null)
                why += " and SOLIDWORKS returned no object for it";
            throw Unresolved(text, field, data, reason, why);
        }
        var kind = doc.KindOf(entity);
        if (kind != data.Kind)
            throw Unresolved(text, field, data, "changed_kind", $"it now names a {(kind == null ? "different kind of entity" : Wire(kind.Value))}");
        return (entity, data.Kind);
    }

    (object, ReferenceKind) ResolveName(ISwDocument doc, string name, string field)
    {
        object? entity = null;
        int plane = Array.FindIndex(StandardPlaneNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (plane >= 0) entity = doc.StandardPlane(plane);
        else if (string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase)) entity = doc.Origin();
        entity ??= doc.FeatureByName(name);
        var kind = entity == null ? null : doc.KindOf(entity);
        if (entity == null || kind == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field}: no feature, plane, axis or point is named '{name}' in {doc.Info().Title} (front, top, right and origin name the template's own)",
                new JsonObject { ["field"] = field, ["name"] = name });
        }
        return (entity, kind.Value);
    }

    static SwAgentException Unresolved(string text, string field, ReferenceData data, string reason, string why) =>
        new SwAgentException(ErrorCode.ReferenceUnresolved,
            $"{field} no longer resolves: {why}. It was the {data.Description}. Find it again (model.find, model.tree) rather than guessing",
            new JsonObject
            {
                ["field"] = field,
                ["reference"] = text,
                ["kind"] = Wire(data.Kind),
                ["last_was"] = data.Description,
                ["reason"] = reason,
            });

    static string Wire(ReferenceKind kind) => WireNames.ForEnum(kind);
}
