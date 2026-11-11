using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Finds the component a request names (design 455, Components): by its reference (r1.component.…), which resolves only
/// in its own assembly and never to another component, or by its name (bracket-1, sub-1/pin-1).
/// </summary>
public static class ComponentLookup
{
    public static object Find(ReferenceService references, ISwDocument doc, ISwAssembly asm, string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field} is empty; name a component by its reference or its name", new JsonObject { ["field"] = field });
        if (References.IsReference(text))
        {
            try
            {
                return references.Resolve(doc, text, field, ReferenceKind.Component).Entity;
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved && e.Error.Details is JsonObject d && d.Keys.Contains("reason") && d["reason"] as string == "suppressed")
            {
                // SOLIDWORKS answers a suppressed component's reference with "suppressed" and may hand back no object
                // (4.A live run). The component is still in the tree: find the one whose own persistent id is this
                // reference's, byte for byte, which can only be the very component it was issued for.
                var id = References.Decode(text, field).PersistId;
                var same = asm.Components(false).FirstOrDefault(c => doc.PersistId(c) is byte[] mine && mine.SequenceEqual(id));
                if (same != null && doc.KindOf(same) == ReferenceKind.Component) return same;
                e.Error.Details!["searched"] = asm.Components(false).Count;
                throw;
            }
        }
        var component = asm.ComponentByName(text);
        if (component != null) return component;
        var names = asm.Components(false).Select(c => asm.Read(c).Name).Take(50).Select(n => (object?)n).ToList();
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: {doc.Info().Title} has no component named '{text}'; its components are listed in details.components (inside a subassembly, name it sub-1/pin-1)",
            new JsonObject { ["field"] = field, ["name"] = text, ["components"] = names });
    }
}
