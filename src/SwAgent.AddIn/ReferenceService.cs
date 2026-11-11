using System;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// In an assembly (design 455, Assembly references) a reference to an entity of a component is issued against the
/// assembly and carries the component's name and file; it resolves only while that component is loaded and is still
/// that file.
/// </summary>
public sealed class ReferenceService
{
    static readonly string[] StandardPlaneNames = { "front", "top", "right" };

    readonly RenameMap _renames;

    /// <summary>
    /// The component each assembly-context entity was resolved through, for the ones SOLIDWORKS names no component for
    /// (a component's plane, named front@bracket-1): so it is described and referenced as that component's.
    /// </summary>
    readonly ConditionalWeakTable<object, object> _components = new ConditionalWeakTable<object, object>();

    public ReferenceService(RenameMap renames)
    {
        _renames = renames;
    }

    /// <summary>The entity's description with its reference, when SOLIDWORKS can give one; in an assembly, an entity of a component in the assembly's coordinates, naming it.</summary>
    public EntityInfo Describe(ISwDocument doc, object entity)
    {
        var asm = doc.Assembly();
        var component = asm == null ? null : ComponentOf(doc, entity);
        var info = component != null ? asm!.Describe(component, entity) : doc.Describe(entity);
        info.Reference = Issue(doc, entity, info);
        return info;
    }

    /// <summary>
    /// A reference to <paramref name="entity"/> in <paramref name="doc"/>, or null when SOLIDWORKS gives no persistent id.
    /// In an assembly a component's entity (and a component itself) carries the component's name and file, and
    /// <paramref name="info"/> gains the component's name.
    /// </summary>
    public string? Issue(ISwDocument doc, object entity, EntityInfo info)
    {
        var id = doc.PersistId(entity);
        if (id == null || id.Length == 0) return null;
        string? component = null, componentPath = null;
        if (doc.Assembly() is ISwAssembly asm)
        {
            var owner = info.Kind == ReferenceKind.Component ? entity : ComponentOf(doc, entity);
            if (owner != null)
            {
                var who = asm.Identify(owner);
                component = who.Name;
                componentPath = who.Path;
                if (info.Kind != ReferenceKind.Component) info.Component ??= who.Name;
            }
        }
        return References.Encode(new ReferenceData(info.Kind, DocumentKeys.Of(doc.Info()), id, EntityFilter.Describe(info), component, componentPath));
    }

    /// <summary>The component an entity of an assembly belongs to: as SOLIDWORKS says, or as it was resolved; null for the assembly's own.</summary>
    public object? ComponentOf(ISwDocument doc, object entity) =>
        doc.KindOf(entity) == ReferenceKind.Component ? null : doc.ComponentOf(entity) ?? (_components.TryGetValue(entity, out var c) ? c : null);

    void Remember(object entity, object component)
    {
        _components.Remove(entity);
        _components.Add(entity, component);
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
    /// The pair form <c>{component, entity}</c> (design 455, Assembly references): <paramref name="text"/> is a reference
    /// issued in <paramref name="component"/>'s own document, or a name there (front, top, right and origin are its own),
    /// mapped into the assembly with IComponent2.GetCorresponding. invalid_argument naming
    /// <paramref name="componentField"/> when <paramref name="doc"/> is not an assembly, or the component is not loaded
    /// in full; reference_unresolved (other_document) when the reference belongs to another file.
    /// </summary>
    public (object Entity, ReferenceKind Kind) ResolveInComponent(ISwDocument doc, string text, string component, string field, string componentField, params ReferenceKind[] accepted)
    {
        if (!(doc.Assembly() is ISwAssembly asm))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{componentField} names a component, which only an assembly has; {doc.Info().Title} is not one. Leave it out and name the entity in the part itself",
                new JsonObject { ["field"] = componentField });
        }
        var owner = ComponentLookup.Find(this, doc, asm, component, componentField);
        var (entity, kind) = Resolve(Loaded(asm, owner, componentField), text, field, accepted);
        return (InAssembly(asm, owner, entity, kind, field), kind);
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
        // A suppressed component is still in the assembly, and component.set must be able to resolve it again (4.A live run).
        bool suppressedComponent = state == PersistState.Suppressed && data.Kind == ReferenceKind.Component && entity != null;
        if ((state != PersistState.Ok && !suppressedFeature && !suppressedComponent) || entity == null)
        {
            if (Owner(doc, data, null) is (ISwAssembly asm, object owner)) NotLoaded(text, field, data, asm.Identify(owner));
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
        if (Owner(doc, data, entity) is (ISwAssembly assembly, object component))
        {
            var who = assembly.Identify(component);
            if (data.Kind != ReferenceKind.Component)
            {
                NotLoaded(text, field, data, who);
                Remember(entity, component);
            }
            if (data.ComponentPath != null && who.Path != null && !DocumentKeys.Same(_renames.Current(data.ComponentPath), who.Path))
            {
                throw Unresolved(text, field, data, "component_file_changed",
                    $"its component {who.Name} is now the file {who.Path}, not {data.ComponentPath} (SOLIDWORKS loads a same-named file from another folder when the file is not where the assembly says: document.open lists it under substituted)");
            }
        }
        return (entity, data.Kind);
    }

    /// <summary>
    /// The assembly and component an assembly reference was issued for: the resolved entity's own component, or the
    /// component with the name recorded at issue. Null for a part's references and an assembly's own entities.
    /// </summary>
    (ISwAssembly, object)? Owner(ISwDocument doc, ReferenceData data, object? entity)
    {
        if (data.Component == null || !(doc.Assembly() is ISwAssembly asm)) return null;
        object? owner = data.Kind == ReferenceKind.Component ? entity : entity == null ? null : ComponentOf(doc, entity);
        owner ??= asm.ComponentByName(data.Component);
        return owner == null ? ((ISwAssembly, object)?)null : (asm, owner);
    }

    /// <summary>reference_unresolved when an entity's component is not loaded in full (design 455): suppressed, lightweight or missing have no geometry to resolve.</summary>
    void NotLoaded(string text, string field, ReferenceData data, ComponentIdentity who)
    {
        if (data.Kind == ReferenceKind.Component || who.State == ComponentState.Resolved) return;
        string reason = "component_" + WireNames.ForEnum(who.State);
        throw Unresolved(text, field, data, reason, $"its component {who.Name} is {WireNames.ForEnum(who.State)}, so it has no geometry; {Remedy(who)}");
    }

    /// <summary>What to do about a component that is not loaded in full.</summary>
    internal static string Remedy(ComponentIdentity who) => who.State switch
    {
        ComponentState.Missing => $"its file {who.Path} is not there (document.open lists missing files)",
        _ => $"load it with component.set {{components: [\"{who.Name}\"], suppression: \"resolved\"}}",
    };

    (object, ReferenceKind) ResolveName(ISwDocument doc, string name, string field)
    {
        int at = name.IndexOf('@');
        if (at >= 0) return ResolveComponentName(doc, name.Substring(0, at), name.Substring(at + 1), field);
        object? entity = null;
        int plane = Array.FindIndex(StandardPlaneNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (plane >= 0) entity = doc.StandardPlane(plane);
        else if (string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase)) entity = doc.Origin();
        entity ??= doc.FeatureByName(name);
        var kind = entity == null ? null : doc.KindOf(entity);
        if (entity == null || kind == null)
        {
            string hint = doc.Assembly() != null
                ? "front, top, right and origin name the assembly's own; a component's is name@component, such as front@bracket-1"
                : "front, top, right and origin name the template's own";
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field}: no feature, plane, axis or point is named '{name}' in {doc.Info().Title} ({hint})",
                new JsonObject { ["field"] = field, ["name"] = name });
        }
        return (entity, kind.Value);
    }

    /// <summary>
    /// <c>name@component</c> (design 455): the component's own feature, plane, axis or point (front, top, right and origin
    /// by position, as in a part), in the assembly. Only an assembly has components.
    /// </summary>
    (object, ReferenceKind) ResolveComponentName(ISwDocument doc, string own, string component, string field)
    {
        if (!(doc.Assembly() is ISwAssembly asm))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field}: '{own}@{component}' names an entity of a component (name@component), which only an assembly has; in {doc.Info().Title} name it without @",
                new JsonObject { ["field"] = field, ["name"] = own + "@" + component });
        }
        if (own.Length == 0 || component.Length == 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field}: '{own}@{component}' needs a name before the @ and a component after it, such as front@bracket-1 or Axis1@sub-1/pin-1",
                new JsonObject { ["field"] = field, ["name"] = own + "@" + component });
        }
        var owner = ComponentLookup.Find(this, doc, asm, component, field);
        var (entity, kind) = ResolveName(Loaded(asm, owner, field), own, field);
        return (InAssembly(asm, owner, entity, kind, field), kind);
    }

    /// <summary>The document of a component loaded in full; invalid_argument naming the component when it is suppressed, lightweight or missing (design 455).</summary>
    static ISwDocument Loaded(ISwAssembly asm, object component, string field)
    {
        var who = asm.Identify(component);
        var model = who.State == ComponentState.Resolved ? asm.ModelOf(component) : null;
        if (model != null) return model;
        string state = who.State == ComponentState.Resolved ? "not loaded" : WireNames.ForEnum(who.State);
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: the component {who.Name} is {state}, so it has no geometry to name; {Remedy(who)}",
            new JsonObject { ["field"] = field, ["component"] = who.Name, ["state"] = state });
    }

    /// <summary>An entity of a component's own document, in the assembly; remembered as that component's.</summary>
    object InAssembly(ISwAssembly asm, object component, object entity, ReferenceKind kind, string field)
    {
        var mapped = asm.Corresponding(component, entity);
        if (mapped == null)
        {
            string name = asm.Identify(component).Name;
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"{field}: SOLIDWORKS gave no counterpart in the assembly for the {Wire(kind)} of {name}",
                new JsonObject { ["field"] = field, ["component"] = name });
        }
        Remember(mapped, component);
        return mapped;
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
