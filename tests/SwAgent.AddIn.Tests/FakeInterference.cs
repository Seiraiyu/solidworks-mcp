using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>Two components of a fake assembly that overlap (volume 0: faces that touch).</summary>
sealed class FakeClash
{
    public FakeClash(string a, string b, double volumeMm3, Box3? box)
    {
        A = a;
        B = b;
        VolumeMm3 = volumeMm3;
        Box = box;
    }

    public string A { get; }
    public string B { get; }
    public double VolumeMm3 { get; }
    public Box3? Box { get; }
}

/// <summary>An interference session of a <see cref="FakeAssembly"/>: what was asked, and how often it was read and closed.</summary>
sealed class FakeInterferenceSession : IInterferenceSession
{
    public string Note => "";

    readonly FakeAssembly _asm;

    public FakeInterferenceSession(FakeAssembly asm, InterferenceOptions options)
    {
        _asm = asm;
        Options = options;
    }

    public InterferenceOptions Options { get; }

    public int Reads { get; private set; }

    public int Dones { get; private set; }

    public IReadOnlyList<InterferenceFacts> Read()
    {
        Reads++;
        _asm.DuringInterference?.Invoke();
        if (_asm.InterferenceThrows) throw new System.Runtime.InteropServices.COMException("interference detection failed", unchecked((int)0x80004005));
        var list = new List<InterferenceFacts>();
        foreach (var c in _asm.Clashes)
        {
            if (c.VolumeMm3 <= 0 && !Options.CoincidentIsInterference) continue;
            var a = _asm.Named(c.A);
            var b = _asm.Named(c.B);
            if (!Options.IncludeHidden && (a.Info.Hidden || b.Info.Hidden)) continue;
            var facts = new InterferenceFacts { VolumeMm3 = c.VolumeMm3, Box = c.Box };
            facts.Components.Add(a.Entity);
            facts.Components.Add(b.Entity);
            list.Add(facts);
        }
        return list;
    }

    public void Done()
    {
        Dones++;
        if (_asm.DoneThrows) throw new InvalidOperationException("Done failed");
    }
}

sealed partial class FakeAssembly
{
    public List<FakeClash> Clashes { get; } = new List<FakeClash>();

    /// <summary>When true, reading the session throws as SOLIDWORKS would.</summary>
    public bool InterferenceThrows { get; set; }

    /// <summary>When true, Done throws.</summary>
    public bool DoneThrows { get; set; }

    /// <summary>Runs inside every read: a test makes the deadline pass here.</summary>
    public Action? DuringInterference { get; set; }

    public List<FakeInterferenceSession> Sessions { get; } = new List<FakeInterferenceSession>();

    public IInterferenceSession OpenInterference(InterferenceOptions options)
    {
        var s = new FakeInterferenceSession(this, options);
        Sessions.Add(s);
        return s;
    }
}
