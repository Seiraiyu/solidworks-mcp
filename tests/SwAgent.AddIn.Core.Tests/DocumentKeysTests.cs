using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DocumentKeysTests
{
    [Fact]
    public void A_saved_document_is_keyed_by_its_full_path_and_a_new_one_by_its_title()
    {
        Assert.Equal(@"C:\parts\a.SLDPRT", DocumentKeys.Of(new DocumentInfo { Title = "a.SLDPRT", Path = @"C:\parts\sub\..\a.SLDPRT" }));
        Assert.Equal("Part1", DocumentKeys.Of(new DocumentInfo { Title = "Part1" }));
        Assert.True(DocumentKeys.Same(@"C:\Parts\A.sldprt", @"c:\parts\a.SLDPRT"));
    }

    [Fact]
    public void Renames_are_followed_through_every_hop()
    {
        var map = new RenameMap();
        map.Record("Part1", @"C:\p\a.SLDPRT");
        map.Record(@"C:\p\a.SLDPRT", @"C:\p\b.SLDPRT");
        Assert.Equal(@"C:\p\b.SLDPRT", map.Current("Part1"));
        Assert.Equal(@"C:\p\b.SLDPRT", map.Current(@"c:\P\A.sldprt"));
        Assert.Equal("Part2", map.Current("Part2"));
    }

    [Fact]
    public void Saving_back_under_an_earlier_name_does_not_loop()
    {
        var map = new RenameMap();
        map.Record(@"C:\p\a.SLDPRT", @"C:\p\b.SLDPRT");
        map.Record(@"C:\p\b.SLDPRT", @"C:\p\a.SLDPRT");
        Assert.Equal(@"C:\p\a.SLDPRT", map.Current(@"C:\p\b.SLDPRT"));
        Assert.Equal(@"C:\p\a.SLDPRT", map.Current(@"C:\p\a.SLDPRT"));
    }

    [Fact]
    public void A_key_opened_again_forgets_where_it_was_renamed_to()
    {
        var map = new RenameMap();
        map.Record(@"C:\p\a.SLDPRT", @"C:\p\b.SLDPRT");
        map.Forget(@"c:\P\A.sldprt");
        Assert.Equal(@"C:\p\a.SLDPRT", map.Current(@"C:\p\a.SLDPRT"));
    }

    [Fact]
    public void Owned_documents_move_with_a_rename_and_clear()
    {
        var owned = new OwnedDocuments();
        owned.Add("Part1");
        Assert.True(owned.Contains("part1"));
        owned.Rename("Part1", @"C:\p\a.SLDPRT");
        Assert.False(owned.Contains("Part1"));
        Assert.True(owned.Contains(@"C:\p\a.SLDPRT"));
        owned.Rename("Part9", @"C:\p\z.SLDPRT");
        Assert.False(owned.Contains(@"C:\p\z.SLDPRT"));
        owned.Remove(@"C:\p\a.SLDPRT");
        Assert.False(owned.Contains(@"C:\p\a.SLDPRT"));
        owned.Add("Part2");
        owned.Clear();
        Assert.Equal(0, owned.Count);
    }

    [Fact]
    public void Reopening_a_file_saved_under_a_new_name_makes_it_itself_and_the_clients_again()
    {
        var runtime = new AgentRuntime("t", 1, NullLog.Instance);
        runtime.DocumentOpened(@"C:\p\a.SLDPRT", owned: true);
        runtime.DocumentRenamed(@"C:\p\a.SLDPRT", @"C:\p\b.SLDPRT");
        Assert.Equal(@"C:\p\b.SLDPRT", runtime.Renames.Current(@"C:\p\a.SLDPRT"));
        Assert.True(runtime.Owned.Contains(@"C:\p\b.SLDPRT"));

        runtime.DocumentOpened(@"C:\p\a.SLDPRT", owned: true);
        Assert.Equal(@"C:\p\a.SLDPRT", runtime.Renames.Current(@"C:\p\a.SLDPRT"));
        Assert.True(runtime.Owned.Contains(@"C:\p\a.SLDPRT"));
        Assert.True(runtime.Owned.Contains(@"C:\p\b.SLDPRT"));
    }
}
