using System;
using System.Windows.Forms;

namespace TTLSim.UI;

/// <summary>
/// A PropertyGrid that reports when the user accepts a value with Enter.
///
/// The grid hosts its value editor in an internal text box, so a plain
/// KeyDown on the grid never sees the Enter key. ProcessCmdKey, however, is
/// routed by the framework up from the focused edit control through its
/// parent chain -- which includes this grid -- so it is the reliable place
/// to observe the Enter press.
///
/// The focus change is deferred with BeginInvoke so the grid finishes
/// committing the edited value first; only then does <see cref="EnterPressed"/>
/// fire and the host move focus elsewhere (e.g. back to the canvas).
/// </summary>
public sealed class EnterAwarePropertyGrid : PropertyGrid
{
    /// <summary>
    /// Raised after the user presses Enter in the property editor and the
    /// value has been committed. The host typically responds by returning
    /// focus to the editing surface.
    /// </summary>
    public event EventHandler? EnterPressed;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let the grid handle Enter normally (commit the value) first.
        bool handled = base.ProcessCmdKey(ref msg, keyData);

        if (keyData == Keys.Enter && IsHandleCreated)
        {
            // Post the notification so it runs after the commit completes,
            // rather than yanking focus out from under the in-flight edit.
            BeginInvoke(new Action(() => EnterPressed?.Invoke(this, EventArgs.Empty)));
        }

        return handled;
    }
}