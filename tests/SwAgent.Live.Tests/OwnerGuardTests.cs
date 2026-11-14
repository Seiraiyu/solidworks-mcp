using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>SOLIDWORKS processes as the guard sees them, recording what it was asked to do.</summary>
sealed class FakeSolidWorksProcesses : ISolidWorksProcesses
{
    public Dictionary<int, string> Names { get; } = new Dictionary<int, string>();
    public List<int> Killed { get; } = new List<int>();
    public List<int> Closed { get; } = new List<int>();
    public bool ExitsOnClose { get; set; } = true;

    public string? NameOf(int processId) => Names.TryGetValue(processId, out var name) ? name : null;

    public void Kill(int processId) => Killed.Add(processId);

    public bool Close(int processId, TimeSpan wait)
    {
        Closed.Add(processId);
        return ExitsOnClose;
    }
}

public class OwnerGuardTests : IDisposable
{
    const int Pid = 4242;
    readonly string _scratch = Path.Combine(Path.GetTempPath(), "swmcp-guard-" + Guid.NewGuid().ToString("N"));
    readonly FakeSolidWorksProcesses _processes = new FakeSolidWorksProcesses();
    readonly OwnerGuard _guard;

    public OwnerGuardTests()
    {
        _processes.Names[Pid] = "SLDWORKS";
        _guard = new OwnerGuard(_scratch, _processes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    static AddinStateResult State(params DocumentInfo[] documents)
    {
        var state = new AddinStateResult { ProcessId = Pid };
        state.Documents.AddRange(documents);
        return state;
    }

    static DocumentInfo Doc(string? path, bool dirty = false) =>
        new DocumentInfo { Title = path == null ? "Part1" : Path.GetFileName(path), Path = path, Type = DocumentType.Part, Dirty = dirty };

    /// <summary>The state while the suite's diag.sleep holds SOLIDWORKS' UI thread, the only time a kill is allowed.</summary>
    static AddinStateResult Sleeping(params DocumentInfo[] documents)
    {
        var state = State(documents);
        state.Running = new RunningRequestInfo { Id = 7, Method = "diag.sleep", RunningMs = 500 };
        return state;
    }

    static Func<Task<AddinStateResult>> Reads(params AddinStateResult[] states)
    {
        var queue = new Queue<AddinStateResult>(states);
        return () => Task.FromResult(queue.Dequeue());
    }

    [Fact]
    public async Task Kills_by_its_id_when_only_the_suites_own_documents_are_open()
    {
        string mine = _guard.NewScratchPath("killed");
        _guard.Created(mine);
        var killed = await _guard.KillAsync(Pid, Reads(Sleeping(Doc(mine, dirty: true))));
        Assert.Equal(new[] { Pid }, _processes.Killed);
        Assert.Equal("diag.sleep", killed.State.Running!.Method);
        Assert.True(killed.KilledAt > 0 && killed.ExitedAt >= killed.KilledAt);
        Assert.Contains(Path.GetFullPath(mine), File.ReadAllText(Path.Combine(_scratch, "created.txt")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("session.status")]
    public async Task Refuses_a_kill_unless_diag_sleep_holds_the_UI_thread(string? running)
    {
        var state = State();
        if (running != null) state.Running = new RunningRequestInfo { Id = 3, Method = running, RunningMs = 10 };
        var e = await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.KillAsync(Pid, Reads(state)));
        Assert.Contains("diag.sleep", e.Message);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Refuses_when_a_document_it_did_not_create_is_open_and_names_it()
    {
        var e = await Assert.ThrowsAsync<GuardRefusedException>(() =>
            _guard.KillAsync(Pid, Reads(Sleeping(Doc(@"C:\work\bracket.SLDPRT")))));
        Assert.Contains("bracket.SLDPRT", e.Message);
        Assert.Single(e.Documents);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void A_hidden_document_in_its_scratch_directory_is_its_own_and_one_elsewhere_or_with_a_window_is_not()
    {
        var model = Doc(_guard.NewScratchPath("block"));
        model.Visible = false;
        var shown = Doc(_guard.NewScratchPath("plate"));
        var hiddenElsewhere = Doc(@"C:\work\bracket.SLDPRT");
        hiddenElsewhere.Visible = false;
        var beside = Doc(_scratch + "-other" + Path.DirectorySeparatorChar + "pin.SLDPRT");
        beside.Visible = false;
        var foreign = _guard.Foreign(State(model, shown, hiddenElsewhere, beside));
        Assert.Equal(new[] { "plate.SLDPRT", "bracket.SLDPRT", "pin.SLDPRT" }, System.Linq.Enumerable.Select(foreign, d => d.Title));
    }

    [Fact]
    public async Task Refuses_an_unsaved_untitled_document()
    {
        await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.KillAsync(Pid, Reads(Sleeping(Doc(null)))));
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Checks_the_documents_and_dialogs_on_demand()
    {
        await _guard.CheckAsync(Pid, Reads(State()));
        await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.CheckAsync(Pid, Reads(State(Doc(@"C:\work\bracket.SLDPRT")))));
        Assert.Empty(_processes.Killed);
        Assert.Empty(_processes.Closed);
    }

    [Fact]
    public async Task Reads_the_documents_again_for_every_kill_or_close()
    {
        var read = Reads(State(), Sleeping(Doc(@"C:\work\opened-meanwhile.SLDPRT")));
        await _guard.CloseAsync(Pid, read, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.KillAsync(Pid, read));
        Assert.Equal(new[] { Pid }, _processes.Closed);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Refuses_when_the_addin_speaks_for_another_process()
    {
        var state = State();
        state.ProcessId = Pid + 1;
        var e = await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.KillAsync(Pid, Reads(state)));
        Assert.Contains("process", e.Message);
        Assert.Empty(_processes.Killed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("notepad")]
    public async Task Refuses_a_process_that_is_not_SOLIDWORKS(string? name)
    {
        if (name == null) _processes.Names.Remove(Pid);
        else _processes.Names[Pid] = name;
        await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.KillAsync(Pid, Reads(State())));
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Refuses_while_a_dialog_is_open()
    {
        var state = State();
        state.Dialog = new DialogInfo { Id = "d1-1", Title = "Save changes?", OpenedAt = "t" };
        var e = await Assert.ThrowsAsync<GuardRefusedException>(() => _guard.CloseAsync(Pid, Reads(state), TimeSpan.FromSeconds(1)));
        Assert.Contains("Save changes?", e.Message);
        Assert.Empty(_processes.Closed);
    }

    [Fact]
    public async Task A_close_refuses_unsaved_changes_even_in_its_own_document()
    {
        Assert.EndsWith("stand.SLDASM", _guard.NewScratchPath("stand", ".SLDASM"));
        string mine = _guard.NewScratchPath("dirty");
        _guard.Created(mine);
        var e = await Assert.ThrowsAsync<GuardRefusedException>(() =>
            _guard.CloseAsync(Pid, Reads(State(Doc(mine, dirty: true))), TimeSpan.FromSeconds(1)));
        Assert.Contains("save prompt", e.Message);
        Assert.Empty(_processes.Closed);
    }

    [Fact]
    public async Task A_close_that_does_not_end_SOLIDWORKS_says_so_and_does_not_kill()
    {
        _processes.ExitsOnClose = false;
        Assert.False(await _guard.CloseAsync(Pid, Reads(State()), TimeSpan.FromSeconds(1)));
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void Scratch_paths_are_parts_in_the_runs_scratch_directory()
    {
        string path = _guard.NewScratchPath("killed");
        Assert.Equal(Path.Combine(_scratch, "killed.SLDPRT"), path);
        Assert.True(Directory.Exists(_scratch));
    }
}
