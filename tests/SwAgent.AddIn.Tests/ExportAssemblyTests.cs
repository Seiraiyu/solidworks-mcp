using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>stand.SLDASM holding plate-1 and pin-1, each a part with one body, and SOLIDWORKS' export preferences as the user has them.</summary>
public abstract class ExportAssemblyTestBase : AssemblyTestBase
{
    internal readonly FakePreferences Prefs = new FakePreferences();

    protected ExportAssemblyTestBase()
    {
        // The user's own values: AP203, an ASCII STL in inches, coarse, translated to positive space, one STL file per
        // component, and an interference check before STL export.
        Prefs.Values[(PreferenceKind.Integer, 75)] = 203;
        Prefs.Values[(PreferenceKind.Toggle, 69)] = 0;
        Prefs.Values[(PreferenceKind.Integer, 211)] = 3;
        Prefs.Values[(PreferenceKind.Integer, 78)] = 1;
        Prefs.Values[(PreferenceKind.Toggle, 70)] = 1;
        Prefs.Values[(PreferenceKind.Toggle, 72)] = 0;
        Prefs.Values[(PreferenceKind.Toggle, 73)] = 1;
        Services.ExportPreferences = new ScopedPreferences(Prefs, PathOf("export-preferences-journal.json"), NullLog.Instance);
        Doc.Prefs = Prefs;
        Doc.Document.Dirty = true;
        foreach (var (name, file) in new[] { ("plate-1", "base.SLDPRT"), ("pin-1", "pin.SLDPRT") })
        {
            var c = Asm.AddComponent(name, PathOf(file));
            c.Model = FakeDocument.Part(file, PathOf(file));
            c.Model.Bodies = 1;
        }
        Doc.StepOccurrences = 2;
    }

    internal ExportAssemblyResult Export(ExportAssemblyRequest request)
    {
        request.Document ??= "stand.SLDASM";
        return Run(new ExportAssemblyOperation(Services), request);
    }
}

public class ExportAssemblyTests : ExportAssemblyTestBase
{
    [Fact]
    public void A_STEP_export_holds_one_occurrence_per_component_and_the_assembly_is_left_as_it_was()
    {
        string path = PathOf("stand.step");
        var r = Export(new ExportAssemblyRequest { Path = path, Format = ExportFormat.Step });
        Assert.Equal("ISO-10303-21;", r.Header);
        Assert.Equal(2, r.Components);
        Assert.Equal(2, r.StepOccurrences);
        Assert.Contains(r.Checks, c => c.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE"));
        Assert.Contains(r.Checks, c => c.Contains("kept its path"));
        Assert.Equal(PathOf("stand.SLDASM"), r.Document.Path);
        Assert.True(r.Document.Dirty);
        Assert.Equal(new[] { "swStepAP = 214" }, r.Preferences);
        Assert.Equal(203, Prefs.Values[(PreferenceKind.Integer, 75)]);
        Assert.False(File.Exists(PathOf("export-preferences-journal.json")));
    }

    [Fact]
    public void A_STEP_file_missing_a_component_is_verification_failed()
    {
        Doc.StepOccurrences = 1;
        var e = Fails(() => Export(new ExportAssemblyRequest { Path = PathOf("stand.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("step_structure", e.Error.Details!["check"]);
        Assert.Equal(2, e.Error.Details["expected"]);
        Assert.Equal(1, e.Error.Details["measured"]);
    }

    [Fact]
    public void An_STL_export_is_one_file_of_the_whole_assembly_and_the_users_settings_come_back()
    {
        var r = Export(new ExportAssemblyRequest { Path = PathOf("stand.stl"), Format = ExportFormat.Stl });
        Assert.Equal("binary STL", r.Header);
        var during = Doc.Exports.Single().Prefs;
        Assert.Equal(1, during[(PreferenceKind.Toggle, 72)]);
        Assert.Equal(0, during[(PreferenceKind.Toggle, 73)]);
        Assert.Equal(1, during[(PreferenceKind.Toggle, 69)]);
        Assert.Equal(0, Prefs.Values[(PreferenceKind.Toggle, 72)]);
        Assert.Equal(1, Prefs.Values[(PreferenceKind.Toggle, 73)]);
        Assert.Contains(r.Preferences, p => p.StartsWith("swSTLComponentsIntoOneFile"));
        Assert.Null(r.StepOccurrences);
    }

    [Fact]
    public void A_Parasolid_export_says_its_structure_is_not_read()
    {
        var r = Export(new ExportAssemblyRequest { Path = PathOf("stand.x_t"), Format = ExportFormat.Parasolid });
        Assert.Contains(r.Checks, c => c.Contains("structure is not read"));
    }

    [Fact]
    public void A_3MF_export_is_a_zip_with_its_model()
    {
        var r = Export(new ExportAssemblyRequest { Path = PathOf("stand.3mf"), Format = ExportFormat.ThreeMf });
        Assert.Equal("zip", r.Header);
    }

    [Fact]
    public void A_lightweight_component_is_refused_before_anything_is_written()
    {
        Asm.Named("pin-1").Info.State = ComponentState.Lightweight;
        var e = Fails(() => Export(new ExportAssemblyRequest { Path = PathOf("stand.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("pin-1", (List<object?>)e.Error.Details!["components"]!);
        Assert.Contains("component.set", e.Message);
        Assert.Empty(Doc.Exports);
    }

    [Fact]
    public void Suppressed_components_are_not_counted()
    {
        Asm.Named("pin-1").Info.State = ComponentState.Suppressed;
        Doc.StepOccurrences = 1;
        Assert.Equal(1, Export(new ExportAssemblyRequest { Path = PathOf("stand.step"), Format = ExportFormat.Step }).Components);
    }

    [Fact]
    public void An_assembly_with_no_body_is_refused()
    {
        foreach (var c in Asm.Items) c.Model!.Bodies = 0;
        var e = Fails(() => Export(new ExportAssemblyRequest { Path = PathOf("stand.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Empty(Doc.Exports);
    }

    [Fact]
    public void A_part_is_refused_and_export_part_on_an_assembly_names_export_assembly()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Export(new ExportAssemblyRequest { Document = "block.SLDPRT", Path = PathOf("b.step"), Format = ExportFormat.Step })).Code);
        var e = Fails(() => Run(new ExportPartOperation(Services), new ExportPartRequest { Document = "stand.SLDASM", Path = PathOf("s.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("export.assembly", (string)e.Error.Details!["instead"]!);
    }
}
