using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("diag.sleep", typeof(DiagSleepResult), Internal = true)]
[Description("Fault testing only: holds SOLIDWORKS' UI thread for a time without pumping messages. Refused unless diagnostics are on.")]
public sealed class DiagSleepRequest
{
    [Description("How long to hold the UI thread, 1 to 600000 ms.")]
    public int Ms { get; set; }
}

[Description("How long the UI thread was held.")]
public sealed class DiagSleepResult
{
    [Description("Measured milliseconds.")]
    public long SleptMs { get; set; }
}
