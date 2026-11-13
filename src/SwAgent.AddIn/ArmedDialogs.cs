using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// diag.arm_dialog's message box, if one is armed: shown once, after a change is made and before it is checked, as a
/// feature or mate that raises a prompt mid-operation would (design 371 and 455, Testing: the dialog-during-feature and
/// dialog-mid-mate fault cases). Every part change (ModelChangeOperation) and assembly change (AssemblyChangeOperation)
/// calls it at that point. The dialog watch answers the request dialog_open meanwhile; cancel or no rolls the change back.
/// </summary>
internal static class ArmedDialogs
{
    public static void Show(AddInServices services, string operation, IOperationContext context)
    {
        var box = services.Runtime.TakeArmedDialog();
        if (box == null) return;
        context.Log.Warn($"{operation}: showing the armed dialog \"{box.Title}\" mid-change (diag.arm_dialog)");
        string pressed = services.ShowBox(box);
        if (pressed == "cancel" || pressed == "no")
        {
            throw new SwAgentException(ErrorCode.OperationFailed, $"the dialog \"{box.Title}\" was answered {pressed} mid-change, so {operation} was taken back",
                new JsonObject { ["dialog"] = box.Title, ["pressed"] = pressed });
        }
    }
}
