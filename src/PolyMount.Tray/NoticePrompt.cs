using System.Runtime.InteropServices;
using QtSharp.QtCore;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>
/// The boxes a notice puts up: the question with its action, and the one line that says how it
/// went.
///
/// They exist because of where they come from: a click on a corner notification, with no window of
/// this process open. Qt's <c>QMessageBox.Question</c> builds a parentless box and runs it, and on
/// Windows that box is not brought to the foreground — the notifier's click does not make this
/// process the foreground one, so the dialog can land behind whatever the reader had in front of
/// them, which reads as nothing having happened. So each box is built here, shown, raised,
/// activated, and — if Windows still disagrees — asked again through its window handle. The click
/// that opened it is user input to this process, and that is what makes the request succeed.
/// </summary>
internal static class NoticePrompt
{
    /// <summary>The answers <see cref="Ask"/> returns: the first action, declined, never again.</summary>
    internal const int Accepted = 0;
    internal const int Declined = 1;
    internal const int Never = 2;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Asks the question and answers which button was pressed.
    /// <paramref name="acceptLabel"/> is null when the notice has no action, in which case only
    /// "close" and "don't ask again" are offered.</summary>
    internal static int Ask(string title, string body, string? acceptLabel, string closeLabel, string neverLabel)
    {
        using var box = new QMessageBox();
        box.SetWindowTitle(title);
        box.SetText(body);
        box.SetIcon(QMessageBox.Icon.Warning);
        QAbstractButton? accept = acceptLabel == null ? null : box.AddButton(acceptLabel, QMessageBox.ButtonRole.AcceptRole);
        box.AddButton(closeLabel, QMessageBox.ButtonRole.RejectRole);
        QAbstractButton? never = box.AddButton(neverLabel, QMessageBox.ButtonRole.DestructiveRole);
        Run(box);

        var clicked = box.ClickedButton();
        if (accept != null && ReferenceEquals(clicked, accept)) return Accepted;
        if (never != null && ReferenceEquals(clicked, never)) return Never;
        return Declined;
    }

    /// <summary>One line and a button: how the notice's action went.</summary>
    internal static void Tell(string title, string body, string closeLabel)
    {
        using var box = new QMessageBox();
        box.SetWindowTitle(title);
        box.SetText(body);
        box.SetIcon(QMessageBox.Icon.Warning);
        box.AddButton(closeLabel, QMessageBox.ButtonRole.AcceptRole);
        Run(box);
    }

    /// <summary>Shows a box, brings it in front and runs it.
    ///
    /// These boxes have no parent — they are not the main window's children, and that is on
    /// purpose: the main window may be minimized or behind another program, neither of which is a
    /// reason for a notice to be invisible. What makes them invisible is the Windows foreground
    /// rule: the click that opened one went to the shell's notification, not to a window of ours,
    /// so this process never received the input that would let <c>SetForegroundWindow</c> succeed.
    ///
    /// A topmost window does not need that permission, which is why the flag is set before the box
    /// is shown: a modal question that the reader asked for belongs in front, and being on top is
    /// the only way to be sure of it here. The activation and the window-handle request stay as
    /// well — when one of them is allowed, the box also gets the keyboard, which a merely topmost
    /// window does not.
    /// </summary>
    private static void Run(QMessageBox box)
    {
        box.SetWindowFlag(Qt.WindowType.WindowStaysOnTopHint, true);
        box.Show();
        box.Raise();
        box.ActivateWindow();
        QApplication.Alert(box, 0);   // flashes the taskbar entry while the box is up
        ToFront(box.WinId());
        box.Exec();
    }

    /// <summary>The native second attempt: a window Qt has shown but Windows has not foregrounded.
    /// Nothing here is fatal if it is refused — the box is still up and clickable, just not on
    /// top, which is the state this is here to avoid.</summary>
    private static void ToFront(ulong window)
    {
        if (window == 0) return;
        var handle = (IntPtr)(long)window;
        if (GetForegroundWindow() == handle) return;
        BringWindowToTop(handle);
        SetForegroundWindow(handle);
    }
}
