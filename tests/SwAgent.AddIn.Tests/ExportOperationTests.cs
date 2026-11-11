using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The 40 x 20 x 10 block of <see cref="FeatureTestBase"/>, and SOLIDWORKS' export preferences as the user has them.</summary>
public abstract class ExportTestBase : FeatureTestBase
{
    internal readonly FakePreferences Prefs = new FakePreferences();

    protected ExportTestBase()
    {
        // The user's own values: AP203, an ASCII STL in inches, coarse, translated to positive space, with the info box.
        Prefs.Values[(PreferenceKind.Integer, 75)] = 203;
        Prefs.Values[(PreferenceKind.Toggle, 69)] = 0;
        Prefs.Values[(PreferenceKind.Integer, 211)] = 3;
        Prefs.Values[(PreferenceKind.Integer, 78)] = 1;
        Prefs.Values[(PreferenceKind.Toggle, 70)] = 1;
        Prefs.Values[(PreferenceKind.Toggle, 643)] = 1;
        Prefs.Values[(PreferenceKind.Double, 2)] = 0.0005;
        Prefs.Values[(PreferenceKind.Double, 3)] = 0.2;
        Services.ExportPreferences = new ScopedPreferences(Prefs, PathOf("export-preferences-journal.json"), NullLog.Instance);
    }

    /// <summary>The block, with the user's preferences for its exports.</summary>
    internal FakeDocument ExportBlock()
    {
        var doc = Part();
        Block();
        doc.Prefs = Prefs;
        doc.Document.Dirty = true;
        return doc;
    }

    internal ExportPartResult Export(ExportPartRequest request) => Run(new ExportPartOperation(Services), request);

    /// <summary>Every preference is the user's again and the journal is gone.</summary>
    internal void UsersPreferencesBack()
    {
        Assert.Equal(203, Prefs.Values[(PreferenceKind.Integer, 75)]);
        Assert.Equal(0, Prefs.Values[(PreferenceKind.Toggle, 69)]);
        Assert.Equal(3, Prefs.Values[(PreferenceKind.Integer, 211)]);
        Assert.Equal(1, Prefs.Values[(PreferenceKind.Integer, 78)]);
        Assert.Equal(1, Prefs.Values[(PreferenceKind.Toggle, 70)]);
        Assert.Equal(0.0005, Prefs.Values[(PreferenceKind.Double, 2)]);
        Assert.False(File.Exists(PathOf("export-preferences-journal.json")));
    }
}

public class ExportPartTests : ExportTestBase
{
    [Fact]
    public void A_STEP_export_is_AP214_by_default_and_the_part_is_left_as_it_was()
    {
        var doc = ExportBlock();
        string path = PathOf("block.step");
        var result = Export(new ExportPartRequest { Path = path, Format = ExportFormat.Step });
        Assert.Equal(path, result.Path);
        Assert.Equal("ISO-10303-21;", result.Header);
        Assert.StartsWith("AUTOMOTIVE_DESIGN", result.StepSchema);
        Assert.Equal(new[] { "swStepAP = 214" }, result.Preferences);
        Assert.Equal(214, doc.Exports.Single().Prefs[(PreferenceKind.Integer, 75)]);
        Assert.Equal(PathOf("part1.SLDPRT"), result.Document.Path);
        Assert.True(result.Document.Dirty);
        Assert.Contains(result.Checks, c => c.Contains("kept its path"));
        UsersPreferencesBack();
    }

    [Fact]
    public void AP242_goes_through_PublishSTEP242File_and_is_found_in_the_file()
    {
        // 3.E live run: SaveAs3 with swStepAP = 242 writes STEP AP203, so AP242 is published (the owner's choice).
        var doc = ExportBlock();
        var result = Export(new ExportPartRequest { Path = PathOf("block.stp"), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } });
        Assert.StartsWith("AP242", result.StepSchema);
        Assert.Empty(doc.Exports);
        Assert.Equal(PathOf("block.stp"), Assert.Single(doc.Published).Path);
        Assert.Equal(242, doc.Published.Single().Prefs[(PreferenceKind.Integer, 75)]);
        Assert.Contains(result.Checks, c => c.Contains("PublishSTEP242File"));
        Assert.True(result.Document.Dirty);
        UsersPreferencesBack();
    }

    [Theory]
    [InlineData(4, "swPublishStep242_MBDLicenseNotAvailable")]
    [InlineData(3, "swPublishStep242_UnknownError")]
    public void When_every_AP242_attempt_fails_it_is_operation_failed_naming_each(int code, string name)
    {
        // 3.E live run, round 4: PublishSTEP242File answered InvalidPath. Round 5 tries a chain and reports every attempt.
        var doc = ExportBlock();
        doc.PublishFails = code;
        doc.PublishWithDataFails = 1;
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        var attempts = ((List<object?>)e.Error.Details!["attempts"]!).Cast<string>().ToList();
        Assert.Equal(3, attempts.Count);
        Assert.Equal($"PublishSTEP242File({PathOf("block.step")}): {name}", attempts[0]);
        Assert.Equal($"PublishSTEP242File({PathOf("block.STEP")}): {name}", attempts[1]);
        Assert.Equal("GetMBDSTEP242Data + PublishSTEP242File2: swPublishStep242_InvalidPath", attempts[2]);
        Assert.All(attempts, a => Assert.Contains(a, e.Message));
        Assert.Equal(new List<object?> { name, name, "swPublishStep242_InvalidPath" }, (List<object?>)e.Error.Details["errors"]!);
        Assert.Equal(new[] { PathOf("block.step"), PathOf("block.STEP") }, doc.Published.Select(p => p.Path));
        Assert.Equal(new[] { PathOf("block.step") }, doc.PublishedWithData);
        Assert.False(File.Exists(PathOf("block.step")));
        UsersPreferencesBack();
    }

    [Fact]
    public void The_AP242_chain_stops_at_the_first_attempt_that_writes_and_says_which()
    {
        var doc = ExportBlock();
        doc.PublishAnswer = p => p.EndsWith(".STEP", StringComparison.Ordinal) ? 0 : 1;
        var result = Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } });
        Assert.StartsWith("AP242", result.StepSchema);
        Assert.Equal(2, doc.Published.Count);
        Assert.Empty(doc.PublishedWithData);
        Assert.Contains(result.Checks, c => c.Contains($"PublishSTEP242File({PathOf("block.step")}): swPublishStep242_InvalidPath") && c.Contains($"PublishSTEP242File({PathOf("block.STEP")}): swPublishStep242_Success"));
        UsersPreferencesBack();
    }

    [Fact]
    public void The_AP242_chain_ends_with_PublishSTEP242File2_and_keeps_a_stp_extension_in_upper_case()
    {
        var doc = ExportBlock();
        doc.PublishFails = 1;
        var result = Export(new ExportPartRequest { Path = PathOf("block.stp"), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } });
        Assert.StartsWith("AP242", result.StepSchema);
        Assert.Equal(new[] { PathOf("block.stp"), PathOf("block.STP") }, doc.Published.Select(p => p.Path));
        Assert.Equal(new[] { PathOf("block.stp") }, doc.PublishedWithData);
        Assert.Contains(result.Checks, c => c.Contains("GetMBDSTEP242Data + PublishSTEP242File2: swPublishStep242_Success"));
        UsersPreferencesBack();
    }

    [Fact]
    public void AP214_still_goes_through_SaveAs3()
    {
        var doc = ExportBlock();
        Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step });
        Assert.Single(doc.Exports);
        Assert.Empty(doc.Published);
    }

    [Fact]
    public void A_Parasolid_export_sets_nothing()
    {
        var doc = ExportBlock();
        var result = Export(new ExportPartRequest { Path = PathOf("block.x_t"), Format = ExportFormat.Parasolid });
        Assert.Equal(ExportFileCheck.ParasolidHeader, result.Header);
        Assert.Empty(result.Preferences);
        Assert.Single(doc.Exports);
    }

    [Fact]
    public void An_STL_export_is_binary_in_mm_and_in_model_coordinates_with_the_users_own_resolution()
    {
        // 3.E live run, round 3: with no resolution asked, the user's mesh settings are used and never touched.
        var doc = ExportBlock();
        var result = Export(new ExportPartRequest { Path = PathOf("block.stl"), Format = ExportFormat.Stl });
        Assert.Equal(12, result.Triangles);
        Assert.Equal(new[]
        {
            "swExportStlUnits = 0", "swSTLBinaryFormat = true", "swSTLDontTranslateToPositive = true",
            "swSTLShowInfoOnSave = false", "swSTLPreview = false",
        }, result.Preferences);
        var during = doc.Exports.Single().Prefs;
        Assert.Equal(1, during[(PreferenceKind.Integer, 78)]);
        Assert.Equal(1, during[(PreferenceKind.Toggle, 69)]);
        Assert.Equal(0, during[(PreferenceKind.Toggle, 70)]);
        UsersPreferencesBack();
    }

    [Fact]
    public void Fine_is_set_for_the_call_when_asked()
    {
        var doc = ExportBlock();
        var result = Export(new ExportPartRequest { Path = PathOf("fine.stl"), Format = ExportFormat.Stl, Options = new ExportOptions { Resolution = MeshResolution.Fine } });
        Assert.Equal(44, result.Triangles);
        Assert.Contains("swSTLQuality = 2", result.Preferences);
        UsersPreferencesBack();
    }

    [Theory]
    [InlineData(MeshResolution.Coarse)]
    [InlineData(MeshResolution.Fine)]
    public void Coarse_or_fine_over_the_users_custom_resolution_is_refused_before_anything_is_set(MeshResolution resolution)
    {
        // 3.E live run, round 3: SOLIDWORKS does not take swSTLQuality = 3 (custom) through its API, so a custom
        // resolution changed for the call could never be put back.
        var doc = ExportBlock();
        Prefs.Values[(PreferenceKind.Integer, 78)] = 3;
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block.stl"), Format = ExportFormat.Stl, Options = new ExportOptions { Resolution = resolution } }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("options.resolution", e.Error.Details!["field"]);
        Assert.Contains("custom", e.Message);
        Assert.Contains("omit options.resolution", e.Message);
        Assert.Empty(doc.Exports);
        Assert.Equal(3, Prefs.Values[(PreferenceKind.Integer, 78)]);
        Assert.Equal(0, Prefs.Values[(PreferenceKind.Toggle, 69)]);
        Assert.False(File.Exists(PathOf("export-preferences-journal.json")));
        // Without a resolution the user's custom mesh is used as it is.
        Assert.Equal(100, Export(new ExportPartRequest { Path = PathOf("own.stl"), Format = ExportFormat.Stl }).Triangles);
        Assert.Equal(3, Prefs.Values[(PreferenceKind.Integer, 78)]);
    }

    [Fact]
    public void A_3MF_export_is_a_zip_with_its_model_and_takes_the_mesh_options()
    {
        var doc = ExportBlock();
        var result = Export(new ExportPartRequest { Path = PathOf("block.3mf"), Format = ExportFormat.ThreeMf, Options = new ExportOptions { Resolution = MeshResolution.Coarse } });
        Assert.Equal("zip", result.Header);
        Assert.Equal(new[] { "swExportStlUnits = 0", "swSTLQuality = 1", "sw3MFShowInfoOnSave = false" }, result.Preferences);
        Assert.Equal(0, doc.Exports.Single().Prefs[(PreferenceKind.Toggle, 643)]);
        Assert.Equal(1, Prefs.Values[(PreferenceKind.Toggle, 643)]);
    }

    [Theory]
    [InlineData(ExportFormat.Stl, "step_schema", "options.step_schema")]
    [InlineData(ExportFormat.Step, "resolution", "options.resolution")]
    [InlineData(ExportFormat.Parasolid, "resolution", "options.resolution")]
    public void Options_the_format_does_not_take_name_their_field(ExportFormat format, string options, string field)
    {
        var doc = ExportBlock();
        var o = options switch
        {
            "step_schema" => new ExportOptions { StepSchema = StepSchema.Ap242 },
            _ => new ExportOptions { Resolution = MeshResolution.Fine },
        };
        string ext = ExportSettings.Extensions(format)[0];
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block" + ext), Format = format, Options = o }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(doc.Exports);
    }

    [Fact]
    public void The_path_must_have_the_formats_extension_and_not_exist_unless_overwrite()
    {
        var doc = ExportBlock();
        Assert.Equal("path", Fails(() => Export(new ExportPartRequest { Path = PathOf("block.stl"), Format = ExportFormat.Step })).Error.Details!["field"]);
        Assert.Equal("path", Fails(() => Export(new ExportPartRequest { Path = "block.step", Format = ExportFormat.Step })).Error.Details!["field"]);
        string path = PathOf("there.step");
        File.WriteAllText(path, "old");
        var e = Fails(() => Export(new ExportPartRequest { Path = path, Format = ExportFormat.Step }));
        Assert.Contains("overwrite", e.Message);
        Assert.Empty(doc.Exports);
        Assert.Equal("ISO-10303-21;", Export(new ExportPartRequest { Path = path, Format = ExportFormat.Step, Overwrite = true }).Header);
    }

    [Fact]
    public void A_part_that_is_not_active_is_activated_for_the_export_and_the_active_one_put_back()
    {
        var doc = ExportBlock();
        var other = Sw.Track(FakeDocument.Part("Part2"));
        Sw.CurrentDoc = other;
        var result = Export(new ExportPartRequest { Document = doc.Info().Path, Path = PathOf("block.step"), Format = ExportFormat.Step });
        Assert.Equal(1, doc.Activations);
        Assert.Equal(1, other.Activations);
        Assert.Same(other, Sw.CurrentDoc);
        Assert.Contains(result.Checks, c => c.Contains("Part2 active again"));
    }

    [Fact]
    public void A_failed_export_puts_the_active_document_back_too()
    {
        var doc = ExportBlock();
        var other = Sw.Track(FakeDocument.Part("Part2"));
        Sw.CurrentDoc = other;
        doc.ExportFails = true;
        Fails(() => Export(new ExportPartRequest { Document = doc.Info().Path, Path = PathOf("block.step"), Format = ExportFormat.Step }));
        Assert.Same(other, Sw.CurrentDoc);
    }

    [Fact]
    public void Exporting_the_active_part_activates_nothing()
    {
        var doc = ExportBlock();
        Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step });
        Assert.Equal(0, doc.Activations);
    }

    [Fact]
    public void A_part_with_no_body_is_refused()
    {
        var doc = Part();
        doc.Prefs = Prefs;
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("empty.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("document", e.Error.Details!["field"]);
    }

    [Fact]
    public void SOLIDWORKS_refusing_is_operation_failed_and_the_preferences_are_back()
    {
        var doc = ExportBlock();
        doc.ExportFails = true;
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block.stl"), Format = ExportFormat.Stl }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("swGenericSaveError", e.Message);
        UsersPreferencesBack();
    }

    [Fact]
    public void A_file_not_written_or_a_part_moved_is_verification_failed()
    {
        var doc = ExportBlock();
        doc.ExportWritesNothing = true;
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("none.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("file_written", e.Error.Details!["check"]);
        doc.ExportWritesNothing = false;
        doc.ExportMovesDocument = true;
        var moved = Fails(() => Export(new ExportPartRequest { Path = PathOf("moved.step"), Format = ExportFormat.Step }));
        Assert.Equal("document_unchanged", moved.Error.Details!["check"]);
        UsersPreferencesBack();
    }

    [Fact]
    public void A_STEP_schema_not_written_reports_the_file_description_and_the_preferences_read_back()
    {
        // 3.E live run, round 1: SOLIDWORKS kept swStepAP = 242 but wrote STEP AP203 (CONFIG_CONTROL_DESIGN).
        var doc = ExportBlock();
        doc.ExportWrites = System.Text.Encoding.ASCII.GetBytes(
            "ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION (( 'STEP AP203' ),\r\n    '1' );\r\nFILE_SCHEMA (( 'CONFIG_CONTROL_DESIGN' ));\r\nENDSEC;\r\n");
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("step_schema", e.Error.Details!["check"]);
        Assert.Equal("STEP AP203", e.Error.Details["description"]);
        Assert.Contains("FILE_DESCRIPTION is STEP AP203", e.Message);
        Assert.Equal(new List<object?> { "swStepAP = 242" }, (List<object?>)e.Error.Details["read_back"]!);
        Assert.Contains("read back just before the export: swStepAP = 242", e.Message);
        UsersPreferencesBack();
    }

    [Fact]
    public void Custom_is_not_a_resolution_and_deviation_and_angle_are_not_options()
    {
        // 3.E live run, round 3: the API cannot set a custom mesh (swSTLQuality = 3 is not kept), so it is not offered.
        Assert.Equal(new[] { "Coarse", "Fine" }, Enum.GetNames(typeof(MeshResolution)));
        Assert.Null(typeof(ExportOptions).GetProperty("DeviationMm"));
        Assert.Null(typeof(ExportOptions).GetProperty("AngleDeg"));
    }

    [Fact]
    public void A_preference_SOLIDWORKS_does_not_keep_exports_nothing()
    {
        var doc = ExportBlock();
        Prefs.Ignored.Add(75);
        var e = Fails(() => Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Equal("swStepAP", e.Error.Details!["preference"]);
        Assert.Empty(doc.Exports);
    }

    [Fact]
    public void Without_export_preferences_nothing_is_exported()
    {
        var doc = ExportBlock();
        Services.ExportPreferences = null;
        Assert.Equal(ErrorCode.ApiError, Fails(() => Export(new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step })).Code);
        Assert.Empty(doc.Exports);
    }
}

/// <summary>Every error code export.part lists in the plan's code table is reachable (design 371, Error handling), one case per code.</summary>
public class ExportErrorCodeTests : ExportTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Export_part_reaches_every_listed_code(ErrorCode code)
    {
        var request = new ExportPartRequest { Path = PathOf("block.step"), Format = ExportFormat.Step };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Export(request)).Code);
            return;
        }
        var doc = ExportBlock();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Options = new ExportOptions { Resolution = MeshResolution.Fine };
                break;
            case ErrorCode.OperationFailed:
                doc.ExportFails = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.ExportWrites = System.Text.Encoding.ASCII.GetBytes("not a STEP file");
                break;
            case ErrorCode.ApiError:
                Prefs.Ignored.Add(75);
                break;
        }
        Assert.Equal(code, Fails(() => Export(request)).Code);
    }
}
