using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class ConfigurationListOperation : Operation<ConfigurationListRequest, ConfigurationListResult>
{
    readonly AddInServices _services;

    public ConfigurationListOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ConfigurationListResult Run(ConfigurationListRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindModel(_services.SolidWorks, request.Document, "configuration.list");
        return new ConfigurationListResult { Document = doc.Info(), Active = doc.ActiveConfiguration(), Configurations = doc.ConfigurationNames().ToList() };
    }

    /// <summary>The active configuration is one of those listed.</summary>
    protected override void Verify(ConfigurationListRequest request, ConfigurationListResult result, IOperationContext context)
    {
        if (!result.Configurations.Contains(result.Active))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the active configuration {result.Active} is not among those SOLIDWORKS listed ({string.Join(", ", result.Configurations)})");
    }
}

/// <summary>What the configuration operations share: names checked, the active one changed and recorded, the result filled.</summary>
public static class ConfigurationFacts
{
    /// <summary>A configuration the part has; invalid_argument naming the field and the ones it has.</summary>
    public static string Existing(ISwDocument doc, string? name, string field)
    {
        var names = doc.ConfigurationNames();
        if (string.IsNullOrWhiteSpace(name)) throw FeatureFacts.Invalid(field, $"{field} needs a configuration's name");
        if (!names.Contains(name!))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field}: {doc.Info().Title} has no configuration '{name}'; it has {string.Join(", ", names)}",
                new JsonObject { ["field"] = field, ["configurations"] = names.Select(n => (object?)n).ToList() });
        }
        return name!;
    }

    /// <summary>Makes <paramref name="name"/> active, recorded so a rollback makes the one before active again.</summary>
    public static void Activate(ISwDocument doc, string name, ModelTransaction tx)
    {
        string before = doc.ActiveConfiguration();
        if (before == name) return;
        tx.Edited(new ChangedItem { Name = doc.Info().Title, Property = "active_configuration", Before = before, After = name }, () => doc.ShowConfiguration(before));
        if (!doc.ShowConfiguration(name)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not make the configuration {name} active");
    }

    public static ConfigurationResult Result(ISwDocument doc) =>
        new ConfigurationResult { Active = doc.ActiveConfiguration(), Configurations = doc.ConfigurationNames().ToList() };

    /// <summary>The part lists <paramref name="name"/> and it is the active configuration, read again.</summary>
    public static void IsActive(ISwDocument doc, string name)
    {
        if (!doc.ConfigurationNames().Contains(name))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{doc.Info().Title} does not list the configuration {name}", new JsonObject { ["check"] = "configuration_exists", ["expected"] = name });
        string active = doc.ActiveConfiguration();
        if (active != name)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the active configuration is {active}, not {name}", new JsonObject { ["check"] = "active", ["expected"] = name, ["measured"] = active });
    }
}

public sealed class ConfigurationAddOperation : ModelChangeOperation<ConfigurationAddRequest, ConfigurationResult>
{
    public ConfigurationAddOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ConfigurationAddRequest request) => request.Document;

    protected override bool TakesAssemblies => true;

    protected override ConfigurationResult Change(ISwDocument doc, ConfigurationAddRequest request, ModelTransaction tx, IOperationContext context)
    {
        string name = request.Name.Trim();
        if (name.Length == 0) throw FeatureFacts.Invalid("name", "a configuration needs a name");
        if (name.IndexOfAny(new[] { '@', '<', '>', '/', '\\', ':', '"', '|', '?', '*' }) >= 0)
            throw FeatureFacts.Invalid("name", $"name: '{name}' holds a character SOLIDWORKS does not allow in a configuration's name (@ < > / \\ : \" | ? *)");
        if (doc.ConfigurationNames().Contains(name)) throw FeatureFacts.Invalid("name", $"name: {doc.Info().Title} already has a configuration '{name}'");
        string previous = doc.ActiveConfiguration();
        if (request.CopyOf != null) ConfigurationFacts.Activate(doc, ConfigurationFacts.Existing(doc, request.CopyOf, "copy_of"), tx);
        tx.Edited(new ChangedItem { Name = name, Property = "configuration", Before = "absent", After = "added" }, () =>
        {
            doc.ShowConfiguration(previous);
            doc.DeleteConfiguration(name);
        });
        if (!doc.AddConfiguration(name)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not add the configuration {name}");
        if (doc.ActiveConfiguration() != name) doc.ShowConfiguration(name);
        return ConfigurationFacts.Result(doc);
    }

    protected override void Check(ISwDocument doc, ConfigurationAddRequest request, ConfigurationResult result) => ConfigurationFacts.IsActive(doc, request.Name.Trim());
}

public sealed class ConfigurationActivateOperation : ModelChangeOperation<ConfigurationActivateRequest, ConfigurationResult>
{
    public ConfigurationActivateOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ConfigurationActivateRequest request) => request.Document;

    protected override bool TakesAssemblies => true;

    protected override ConfigurationResult Change(ISwDocument doc, ConfigurationActivateRequest request, ModelTransaction tx, IOperationContext context)
    {
        ConfigurationFacts.Activate(doc, ConfigurationFacts.Existing(doc, request.Name, "name"), tx);
        return ConfigurationFacts.Result(doc);
    }

    protected override void Check(ISwDocument doc, ConfigurationActivateRequest request, ConfigurationResult result) => ConfigurationFacts.IsActive(doc, request.Name);
}
