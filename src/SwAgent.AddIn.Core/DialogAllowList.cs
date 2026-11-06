using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SwAgent.AddIn.Core;

/// <summary>A known prompt and the button that is always safe to press on it.</summary>
public sealed class DialogRule
{
    public DialogRule(string name, string titlePattern, string? textPattern, string button)
    {
        Name = name;
        Title = new Regex(titlePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Text = textPattern == null ? null : new Regex(textPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Button = button;
    }

    public string Name { get; }
    public Regex Title { get; }
    public Regex? Text { get; }

    /// <summary>The button to press: its text (case and & ignored) or control id.</summary>
    public string Button { get; }

    public bool Matches(NativeDialog dialog) =>
        Title.IsMatch(dialog.Title) && (Text == null || Text.IsMatch(string.Join("\n", dialog.Text)));
}

/// <summary>
/// Prompts the add-in answers by itself. It holds only prompts actually met and measured in a live run, each with a
/// test; it is not user-configurable. A rule whose button the dialog does not have never matches.
/// </summary>
public sealed class DialogAllowList
{
    /// <summary>The shipped list. Empty until a live run meets and measures a prompt that is always safe to answer.</summary>
    public static readonly DialogAllowList Default = new DialogAllowList(Array.Empty<DialogRule>());

    public DialogAllowList(IEnumerable<DialogRule> rules)
    {
        Rules = rules.ToList();
    }

    public IReadOnlyList<DialogRule> Rules { get; }

    public DialogRule? Match(NativeDialog dialog) =>
        Rules.FirstOrDefault(r => r.Matches(dialog) && DialogButtons.Find(dialog, r.Button) != null);
}

/// <summary>
/// Windows only a person may answer (issue #10): a 3DEXPERIENCE sign-in or licence window. Closing one ended a
/// SOLIDWORKS session in the 3.C live run, so they are never answered by the add-in, its allow-list or dialog.respond.
/// </summary>
public static class DialogGuards
{
    static readonly Regex PersonOnly = new Regex(@"3DEXPERIENCE|\bLog\s?-?in\b|\bSign\s?-?in\b|\bLicen[cs]e",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool NeedsPerson(string title) => PersonOnly.IsMatch(title ?? "");
}

/// <summary>How a button is named in a request: by its text (case and & ignored) or, failing that, by its control id.</summary>
public static class DialogButtons
{
    public static string Clean(string text) => text.Replace("&", "").Trim();

    public static NativeButton? Find(NativeDialog dialog, string button)
    {
        string wanted = Clean(button);
        var byText = dialog.Buttons.FirstOrDefault(b => string.Equals(Clean(b.Text), wanted, StringComparison.OrdinalIgnoreCase));
        if (byText != null) return byText;
        return int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? dialog.Buttons.FirstOrDefault(b => b.ControlId == id)
            : null;
    }
}
