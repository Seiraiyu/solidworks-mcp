using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Registration;

/// <summary>The identity of an add-in DLL, read from its metadata without loading it.</summary>
public sealed class AddinAssembly
{
    AddinAssembly(string path, string fullName, string version, string clsid)
    {
        Path = path;
        FullName = fullName;
        Version = version;
        Clsid = clsid;
    }

    public string Path { get; }

    /// <summary>For example <c>SwAgent.AddIn, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null</c>.</summary>
    public string FullName { get; }

    /// <summary>The assembly version, for example <c>0.1.0.0</c>.</summary>
    public string Version { get; }

    /// <summary>The add-in class's CLSID, without braces.</summary>
    public string Clsid { get; }

    /// <summary>Throws invalid_argument unless <paramref name="path"/> is the solidworks-mcp add-in.</summary>
    public static AddinAssembly Read(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw Invalid(full, $"there is no file at {full}");
        try
        {
            using var stream = File.OpenRead(full);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw Invalid(full, $"{full} is not a .NET assembly");
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) throw Invalid(full, $"{full} is not a .NET assembly");

            string? clsid = null;
            bool found = false;
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (md.GetString(type.Namespace) + "." + md.GetString(type.Name) != AddInIdentity.ClassName) continue;
                found = true;
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var attribute = md.GetCustomAttribute(attributeHandle);
                    if (AttributeTypeName(md, attribute) != "System.Runtime.InteropServices.GuidAttribute") continue;
                    var blob = md.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() == 1) clsid = blob.ReadSerializedString();
                }
            }
            if (!found) throw Invalid(full, $"{full} is not the solidworks-mcp add-in: it has no {AddInIdentity.ClassName} class");
            if (!string.Equals(clsid, AddInIdentity.Clsid, StringComparison.OrdinalIgnoreCase))
                throw Invalid(full, $"{full} carries CLSID {clsid ?? "none"}, not {AddInIdentity.Clsid}; it is not a build of this add-in");

            var name = md.GetAssemblyDefinition().GetAssemblyName();
            return new AddinAssembly(full, name.FullName, name.Version!.ToString(), AddInIdentity.Clsid);
        }
        catch (BadImageFormatException e)
        {
            throw Invalid(full, $"{full} is not a .NET assembly ({e.Message})");
        }
    }

    static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        EntityHandle type;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        else
            return "";

        if (type.Kind == HandleKind.TypeReference)
        {
            var reference = md.GetTypeReference((TypeReferenceHandle)type);
            return md.GetString(reference.Namespace) + "." + md.GetString(reference.Name);
        }
        if (type.Kind == HandleKind.TypeDefinition)
        {
            var definition = md.GetTypeDefinition((TypeDefinitionHandle)type);
            return md.GetString(definition.Namespace) + "." + md.GetString(definition.Name);
        }
        return "";
    }

    static SwAgentException Invalid(string path, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["path"] = path });
}
