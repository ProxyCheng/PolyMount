using QtSharp.QtCore;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>The one rule every form in these windows obeys: the labels are a column, and the fields
/// are the column after it.
///
/// It is worth checking because Qt makes the mistake easy and silent. A row whose field is itself a
/// widget laid out with a form of its own — the account and the password rows of the credential form
/// used to be exactly that — sizes its columns to *its own* labels, so its fields start left of
/// everything above them (measured 2026-10-29: x=78 against x=124, which is what a reader sees as
/// "the text and the box do not line up"). Comparing global coordinates is the only comparison that
/// catches it: a nested field's own geometry is relative to the block it sits in, so every one of
/// them claims x=0 and agrees with every other.
/// </summary>
internal static class FormColumns
{
    /// <summary>Throws when a visible row's field starts anywhere but the first field's x.
    /// <paramref name="rows"/> is every row of one form, hidden ones included: a row that is not on
    /// screen because the chosen kind has no use for it is not a column of its own.</summary>
    public static void Verify(IEnumerable<(string Title, QWidget Field)> rows)
    {
        var shown = rows.Where(row => row.Field is { } field && !field.IsHidden()).ToList();
        if (shown.Count < 2) return;
        int expected = shown[0].Field.MapToGlobal(new QPoint(0, 0)).X();
        foreach (var (title, field) in shown)
        {
            int at = field.MapToGlobal(new QPoint(0, 0)).X();
            if (at != expected)
                throw new InvalidOperationException(
                    $"The field of '{title}' starts at x={at} where the first field starts at x={expected}");
        }
    }
}
