using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ConfigurationOperationTests : FeatureTestBase
{
    ConfigurationResult Add(ConfigurationAddRequest request) => Run(new ConfigurationAddOperation(Services), request);

    ConfigurationResult Activate(string name) => Run(new ConfigurationActivateOperation(Services), new ConfigurationActivateRequest { Name = name });

    [Fact]
    public void The_list_names_every_configuration_and_the_active_one()
    {
        var doc = Part();
        doc.Configurations.Add("Long");
        var list = Run(new ConfigurationListOperation(Services), new ConfigurationListRequest());
        Assert.Equal("Part1", list.Document.Title);
        Assert.Equal("Default", list.Active);
        Assert.Equal(new[] { "Default", "Long" }, list.Configurations.ToArray());
    }

    [Fact]
    public void An_added_configuration_is_active_and_records_what_it_changed()
    {
        var doc = Part();
        Block();
        var added = Add(new ConfigurationAddRequest { Name = "Long" });
        Assert.Equal("Long", added.Active);
        Assert.Equal(new[] { "Default", "Long" }, added.Configurations.ToArray());
        var changed = Assert.Single(added.Change.Changed);
        Assert.Equal(("Long", "configuration", "absent", "added"), (changed.Name, changed.Property, changed.Before, changed.After));
        Assert.Equal(8000, added.Change.VolumeAfterMm3, 6);
        Assert.Equal("Long", doc.Active);
    }

    [Fact]
    public void A_copy_of_another_configuration_activates_that_one_first()
    {
        var doc = Part();
        doc.Configurations.Add("Short");
        var added = Add(new ConfigurationAddRequest { Name = "Short copy", CopyOf = "Short" });
        Assert.Equal(new[] { "active_configuration", "configuration" }, added.Change.Changed.Select(c => c.Property).ToArray());
        Assert.Equal(("Default", "Short"), (added.Change.Changed[0].Before, added.Change.Changed[0].After));
        Assert.Equal("Short copy", doc.Active);
    }

    [Fact]
    public void SOLIDWORKS_leaving_the_old_one_active_is_put_right()
    {
        var doc = Part();
        doc.ConfigurationAddStaysPut = true;
        Assert.Equal("Long", Add(new ConfigurationAddRequest { Name = "Long" }).Active);
    }

    [Fact]
    public void An_add_that_does_not_take_is_rolled_back_to_the_configuration_that_was_active()
    {
        var doc = Part();
        doc.Configurations.Add("Short");
        doc.ConfigurationAddStaysPut = true;
        doc.ShowConfigurationIgnored = true;
        var e = Fails(() => Add(new ConfigurationAddRequest { Name = "Long" }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("active", e.Error.Details!["check"]);
        Assert.Equal(new[] { "Default", "Short" }, doc.Configurations.ToArray());
        Assert.Equal("Default", doc.Active);
    }

    [Fact]
    public void Activating_switches_and_activating_the_active_one_changes_nothing()
    {
        var doc = Part();
        doc.Configurations.Add("Long");
        var on = Activate("Long");
        Assert.Equal("Long", on.Active);
        Assert.Equal(("Default", "Long"), (on.Change.Changed.Single().Before, on.Change.Changed.Single().After));
        Assert.Empty(Activate("Long").Change.Changed);
        Assert.Equal("Long", doc.Active);
    }

    [Fact]
    public void Names_that_cannot_be_used_name_their_field()
    {
        var doc = Part();
        Assert.Equal("name", Fails(() => Add(new ConfigurationAddRequest { Name = " " })).Error.Details!["field"]);
        Assert.Equal("name", Fails(() => Add(new ConfigurationAddRequest { Name = "Default" })).Error.Details!["field"]);
        Assert.Equal("name", Fails(() => Add(new ConfigurationAddRequest { Name = "a@b" })).Error.Details!["field"]);
        var e = Fails(() => Add(new ConfigurationAddRequest { Name = "Long", CopyOf = "Short" }));
        Assert.Equal("copy_of", e.Error.Details!["field"]);
        Assert.Contains("it has Default", e.Message);
        Assert.Equal("name", Fails(() => Activate("Short")).Error.Details!["field"]);
        Assert.Equal(new[] { "Default" }, doc.Configurations.ToArray());
    }
}
