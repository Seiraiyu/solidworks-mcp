using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ApiCallGuardTests
{
    static SwAgentException Refused(string segment, ApiObjectKind kind, string? spelling = null)
    {
        var e = Assert.Throws<SwAgentException>(() => ApiCallGuard.Check(segment, kind, spelling, segment));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("member", e.Error.Details!["field"]);
        Assert.NotNull(e.Error.Details["refused"]);
        return e;
    }

    [Fact]
    public void The_list_is_the_designs()
    {
        Assert.Equal(new[] { "ExitApp", "CloseAllDocuments", "CloseDoc", "QuitDoc", "CloseAndReopen", "RunMacro", "RunMacro2", "RunCommand", "LoadAddIn", "UnloadAddIn" },
            ApiCallGuard.Refused.ToArray());
    }

    [Theory]
    [InlineData("ExitApp", "ends_session")]
    [InlineData("CloseAllDocuments", "closes_document")]
    [InlineData("CloseDoc", "closes_document")]
    [InlineData("QuitDoc", "closes_document")]
    [InlineData("CloseAndReopen", "closes_document")]
    [InlineData("RunMacro", "runs_code")]
    [InlineData("RunMacro2", "runs_code")]
    [InlineData("RunCommand", "runs_code")]
    [InlineData("LoadAddIn", "runs_code")]
    [InlineData("UnloadAddIn", "runs_code")]
    public void Every_listed_member_is_refused_on_any_object_in_any_case(string member, string reason)
    {
        foreach (var kind in new[] { ApiObjectKind.App, ApiObjectKind.Document, ApiObjectKind.Other })
        {
            foreach (var spelt in new[] { member, member.ToLowerInvariant(), member.ToUpperInvariant() })
            {
                var e = Refused(spelt, kind, spelling: member);
                Assert.Equal(member, e.Error.Details!["refused"]);
                Assert.Equal(reason, e.Error.Details["reason"]);
                // A refused member is refused whether or not SOLIDWORKS' spelling is known.
                Assert.Equal(member, Refused(spelt, kind, spelling: null).Error.Details!["refused"]);
            }
        }
    }

    [Theory]
    [InlineData("ReloadOrReplace")]
    [InlineData("ReloadWithReferences")]
    [InlineData("FileReload")]
    [InlineData("ComponentReload2")]
    [InlineData("IComponentReload3")]
    [InlineData("ForceReleaseLocks")]
    [InlineData("SetReadOnlyState")]
    [InlineData("ReplaceReferencedDocument")]
    [InlineData("ReplaceComponents2")]
    [InlineData("ReplacePLMComponents")]
    [InlineData("ReplaceReference")]
    [InlineData("ReplaceViewModel")]
    public void A_member_that_reloads_replaces_or_releases_a_document_is_refused_on_any_object(string member)
    {
        foreach (var kind in new[] { ApiObjectKind.App, ApiObjectKind.Document, ApiObjectKind.Other })
        {
            foreach (var spelt in new[] { member, member.ToLowerInvariant() })
            {
                var e = Refused(spelt, kind, spelling: null);
                Assert.Equal("closes_document", e.Error.Details!["reason"]);
                Assert.Contains("only with document.close", e.Message);
            }
        }
        Assert.Equal(member, Refused(member.ToUpperInvariant(), ApiObjectKind.Other, member).Error.Details!["refused"]);
    }

    [Theory]
    [InlineData("RunAttachedMacro")]
    [InlineData("RunJournalCmd")]
    [InlineData("RunBatchSaveProcess")]
    [InlineData("CallBack")]
    [InlineData("AddCallback")]
    [InlineData("SetAddinCallbackInfo")]
    [InlineData("SetAddinCallbackInfo2")]
    [InlineData("InsertMacroFeature")]
    [InlineData("InsertMacroFeature2")]
    [InlineData("InsertMacroFeature3")]
    [InlineData("IInsertMacroFeature")]
    [InlineData("IInsertMacroFeature2")]
    [InlineData("IInsertMacroFeature3")]
    [InlineData("ToolsMacro")]
    [InlineData("RunXMLCommand")]
    [InlineData("WriteJournalToMacro")]
    [InlineData("EmbedMacroFile")]
    [InlineData("GetAddInObject")]
    public void Every_other_member_that_runs_code_is_refused_on_any_object(string member)
    {
        foreach (var kind in new[] { ApiObjectKind.App, ApiObjectKind.Document, ApiObjectKind.Other })
        {
            var e = Refused(member.ToLowerInvariant(), kind);
            Assert.Equal(member, e.Error.Details!["refused"]);
            Assert.Equal("runs_code", e.Error.Details["reason"]);
        }
    }

    [Theory]
    [InlineData("ConnectToSW")]
    [InlineData("disconnectfromsw")]
    public void An_addins_own_start_and_stop_are_refused_on_any_object(string member)
    {
        Assert.Equal("ends_session", Refused(member, ApiObjectKind.Other).Error.Details!["reason"]);
    }

    [Fact]
    public void CheckNames_refuses_any_segment_before_anything_is_looked_up()
    {
        var e = Assert.Throws<SwAgentException>(() => ApiCallGuard.CheckNames(ApiCallGuard.Split("Extension.ReloadOrReplace"), "Extension.ReloadOrReplace"));
        Assert.Equal("ReloadOrReplace", e.Error.Details!["refused"]);
        ApiCallGuard.CheckNames(ApiCallGuard.Split("Extension.GetMassProperties2"), "Extension.GetMassProperties2");
    }

    [Theory]
    [InlineData("Close")]
    [InlineData("ClosePrintPreview")]
    [InlineData("CloseFamilyTable")]
    [InlineData("CloseAndReopen2")]
    [InlineData("CloseUserNotification")]
    [InlineData("Quit")]
    public void Every_other_Close_or_Quit_member_of_SOLIDWORKS_or_a_document_is_refused(string member)
    {
        Assert.Equal("closes_document", Refused(member, ApiObjectKind.App, member).Error.Details!["reason"]);
        Assert.Equal("closes_document", Refused(member.ToLowerInvariant(), ApiObjectKind.Document, member).Error.Details!["reason"]);
    }

    [Fact]
    public void A_Close_or_Quit_member_whose_spelling_is_not_known_is_refused()
    {
        Refused("closeeverything", ApiObjectKind.Document);
        Refused("QUITNOW", ApiObjectKind.App);
        Refused("ClosestDistance", ApiObjectKind.Document);
    }

    [Fact]
    public void ClosestDistance_as_SOLIDWORKS_spells_it_measures_and_is_not_refused()
    {
        ApiCallGuard.Check("ClosestDistance", ApiObjectKind.Document, "ClosestDistance", "ClosestDistance");
        ApiCallGuard.Check("closestdistance", ApiObjectKind.Document, "ClosestDistance", "closestdistance");
    }

    [Fact]
    public void Close_members_of_other_objects_are_not_the_guards()
    {
        ApiCallGuard.Check("CloseSketch", ApiObjectKind.Other, "CloseSketch", "SketchManager.CloseSketch");
        ApiCallGuard.Check("GetMassProperties2", ApiObjectKind.Other, "GetMassProperties2", "Extension.GetMassProperties2");
        ApiCallGuard.Check("EditRebuild3", ApiObjectKind.Document, "EditRebuild3", "EditRebuild3");
    }

    [Fact]
    public void A_closing_refusal_points_to_document_close()
    {
        Assert.Contains("only with document.close", Refused("CloseDoc", ApiObjectKind.App).Message);
        Assert.Contains("only with document.close", Refused("Close", ApiObjectKind.Document, "Close").Message);
    }

    [Theory]
    [InlineData("Extension.GetMassProperties2", 2)]
    [InlineData("EditRebuild3", 1)]
    [InlineData("Extension.SelectionManager.GetSelectedObjectCount2", 3)]
    public void A_path_splits_into_its_members(string member, int count)
    {
        Assert.Equal(count, ApiCallGuard.Split(member).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[DISPID=12]")]
    [InlineData("Extension..SelectByID2")]
    [InlineData("Extension.")]
    [InlineData("Extension.SelectByID2()")]
    [InlineData("2Extension")]
    [InlineData("Extension SelectByID2")]
    [InlineData("CloseDoc\n")]
    [InlineData("Extension.CloseDoc\n")]
    public void A_path_that_is_not_names_and_dots_is_refused(string member)
    {
        var e = Assert.Throws<SwAgentException>(() => ApiCallGuard.Split(member));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("member", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_path_has_at_most_sixteen_members()
    {
        Assert.Equal(16, ApiCallGuard.Split(string.Join(".", Enumerable.Repeat("A", 16))).Count);
        Assert.Throws<SwAgentException>(() => ApiCallGuard.Split(string.Join(".", Enumerable.Repeat("A", 17))));
        Assert.Throws<SwAgentException>(() => ApiCallGuard.Split(null));
    }
}
