using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TTLSim.Chips.Displays;
using TTLSim.Core;
using TTLSim.UI.Commands;
using TTLSim.UI.Components;
using TTLSim.UI.Logging;
using TTLSim.UI.Model;
using TTLSim.UI.Persistence;

namespace TTLSim.UI.View;

public sealed partial class SchematicCanvas
{
    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // Sim mode: pressing a button symbol.
        if (ButtonPressHandler is not null && e.Button == MouseButtons.Left)
        {
            var hit = HitTestButton(e.Location);
            if (hit is not null)
            {
                heldButton = hit;
                hit.IsPressed = true;
                ButtonPressHandler(hit, true);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        // Sim mode: clicking a switch symbol toggles it.
        if (SwitchToggleHandler is not null && e.Button == MouseButtons.Left)
        {
            var hitSw = HitTestSwitch(e.Location);
            if (hitSw is not null)
            {
                hitSw.IsClosed = !hitSw.IsClosed;
                SwitchToggleHandler(hitSw, hitSw.IsClosed);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        // Sim mode: clicking an SPDT switch symbol flips it between throws.
        if (SpdtToggleHandler is not null && e.Button == MouseButtons.Left)
        {
            var hitSpdt = HitTestSpdt(e.Location);
            if (hitSpdt is not null)
            {
                hitSpdt.ThrowB = !hitSpdt.ThrowB;
                SpdtToggleHandler(hitSpdt, hitSpdt.ThrowB);
                Invalidate();
                return;   // don't fall through to edit-mode handling
            }
        }

        if (e.Button == MouseButtons.Right && IsPlacingWire)
        {
            CancelWirePlacement();
            return;
        }

        if (e.Button == MouseButtons.Right && IsPlacingHeaderLink)
        {
            CancelHeaderLinkPlacement();
            return;
        }

        if (e.Button == MouseButtons.Middle)
        {
            panning = true;
            panAnchor = e.Location;
            panStart = PanOffset;
            Cursor = Cursors.SizeAll;
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            var grid = ScreenToGrid(e.Location);

            if (awaitingWireStart)
            {
                var pin = Schematic.PinAt(grid);
                if (pin != null)
                {
                    wireStartPin = pin;
                    awaitingWireStart = false;
                    wirePreviewEnd = grid;
                    Invalidate();
                }
                return;
            }
            if (wireStartPin != null)
            {
                var pin = Schematic.PinAt(grid);
                if (pin != null && pin != wireStartPin)
                {
                    var connection = new Connection(wireStartPin, pin);
                    UndoStack.Do(new AddConnectionCommand(connection));

                    wireStartPin = null;
                    awaitingWireStart = true;
                    Invalidate();
                }
                return;
            }

            if (awaitingHeaderLinkStart)
            {
                if (Schematic.HitTest(grid) is HeaderOutputUnit header)
                {
                    headerLinkStart = header;
                    awaitingHeaderLinkStart = false;
                    headerLinkPreviewEnd = grid;
                    Invalidate();
                }
                return;
            }
            if (headerLinkStart != null)
            {
                if (Schematic.HitTest(grid) is HeaderOutputUnit header
                    && header != headerLinkStart)
                {
                    if (header.Pins.Count() == headerLinkStart.Pins.Count())
                    {
                        var link = new HeaderLink(headerLinkStart, header);
                        UndoStack.Do(new AddHeaderLinkCommand(link));

                        headerLinkStart = null;
                        awaitingHeaderLinkStart = true;
                        Invalidate();
                    }
                    else
                    {
                        // Pin counts differ: reject and stay armed for a valid
                        // second header.
                        System.Media.SystemSounds.Beep.Play();
                    }
                }
                return;
            }

            // Selection respects the paint Z-order: foreground items and wires
            // sit in front of cosmetic background items (rectangles, labels),
            // so a click on a wire crossing a rectangle's bare interior takes
            // the wire, not the rectangle. Foreground first, then wires, then
            // links, and only then cosmetic background items.
            var hit = Schematic.HitTestForeground(grid);
            Connection? connectionHit = hit == null
                ? HitTestConnection(grid)
                : null;
            HeaderLink? linkHit = (hit == null && connectionHit == null)
                ? HitTestHeaderLink(grid)
                : null;
            if (hit == null && connectionHit == null && linkHit == null)
                hit = Schematic.HitTestBackground(grid);

            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;

            if (hit != null)
            {
                // Ctrl + press on an ALREADY-selected item is the duplicate
                // gesture: copy the selection, paste it in place, and drag
                // the pasted copy. Ctrl + press on an UNSELECTED item still
                // toggles selection (handled in the else-if below), so the
                // two Ctrl behaviours don't collide.
                if (ctrl && hit.Selected && BeginCopyDrag(grid))
                {
                    Invalidate();
                    OnSelectionChanged();
                    return;
                }

                if (ctrl)
                {
                    // Ctrl-click toggles membership of the hit item without
                    // disturbing the rest of the selection.
                    hit.Selected = !hit.Selected;
                }
                else if (!hit.Selected)
                {
                    // Clicking an unselected item replaces the selection.
                    Schematic.ClearSelection();
                    hit.Selected = true;
                }
                // else: clicking an already-selected item without Ctrl
                // preserves the existing selection so the whole group drags.

                if (hit.Selected)
                {
                    dragging = true;
                    dragGridAnchor = grid;
                    dragItems = Schematic.Selected
                        .Select(i => (i, i.Position))
                        .ToList();
                }
            }
            else if (connectionHit != null)
            {
                if (ctrl)
                {
                    connectionHit.Selected = !connectionHit.Selected;
                }
                else if (!connectionHit.Selected)
                {
                    Schematic.ClearSelection();
                    connectionHit.Selected = true;
                }
            }
            else if (linkHit != null)
            {
                if (ctrl)
                {
                    linkHit.Selected = !linkHit.Selected;
                }
                else if (!linkHit.Selected)
                {
                    Schematic.ClearSelection();
                    linkHit.Selected = true;
                }
            }
            else
            {
                // Empty grid -- start a marquee. Don't clear selection yet;
                // commit happens on mouse-up so a click without movement
                // still ends up clearing (or preserving if Ctrl) cleanly.
                marqueeing = true;
                marqueeStart = grid;
                marqueeEnd = grid;
                marqueeCtrl = ctrl;
            }

            Invalidate();
            OnSelectionChanged();
        }
    }

    /// <summary>
    /// Start a Ctrl-drag duplicate. Opens a single "Duplicate" composite,
    /// pastes a copy of the current selection into it at zero offset, makes
    /// the pasted copy the selection, and arms a normal drag against those
    /// pasted items anchored at <paramref name="grid"/>.
    ///
    /// <para>
    /// The composite is intentionally left OPEN: OnMouseUp records the
    /// MoveItemCommands into it and closes it, so the whole gesture
    /// (paste + move) is one undo step. <see cref="copyDragInProgress"/>
    /// tells OnMouseUp to take that path.
    /// </para>
    ///
    /// <para>
    /// Returns false (composite not opened, nothing changed) if there is
    /// nothing on the clipboard to paste -- e.g. the very first Ctrl-drag in
    /// a session before any copy. The caller then falls through to normal
    /// Ctrl-click handling.
    /// </para>
    /// </summary>
    private bool BeginCopyDrag(Point grid)
    {
        // Snapshot the current selection and copy it to the clipboard. We
        // copy-then-paste (rather than cloning directly) so duplicate uses
        // exactly the same path as Ctrl+C / Ctrl+V -- one code path, no
        // second cloning implementation to keep in sync.
        var (devices, items, connections, links) = GatherSelectionForClipboard();
        if (items.Count == 0)
            return false;

        if (!ClipboardService.Copy(devices, items, connections, links, Schematic.Layers))
            return false;

        UndoStack.BeginComposite("Duplicate");
        try
        {
            var pasted = PasteFromClipboardInto(Point.Empty);
            if (pasted is null || pasted.Items.Count == 0)
            {
                // Nothing came back -- close the (empty) composite so we
                // don't leave the undo stack mid-recording. EndComposite
                // discards an empty buffer without polluting the stack.
                UndoStack.EndComposite();
                return false;
            }

            // The pasted items are now the selection (PasteFromClipboardInto
            // did that). Arm a drag against them. The composite stays open;
            // OnMouseUp closes it.
            copyDragInProgress = true;
            dragging = true;
            dragGridAnchor = grid;
            dragItems = Schematic.Selected
                .Select(i => (i, i.Position))
                .ToList();
            return true;
        }
        catch
        {
            // Defensive: if paste threw after BeginComposite, make sure the
            // composite is closed so the undo stack isn't left recording.
            UndoStack.EndComposite();
            copyDragInProgress = false;
            throw;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        isMouseOverCanvas = false;

        if (heldButton is not null)
        {
            heldButton.IsPressed = false;
            ButtonPressHandler?.Invoke(heldButton, false);
            heldButton = null;
            Invalidate();
        }

        probeTooltip.Hide(this);
        hoveredConnection = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Track the cursor in grid units for keyboard/menu paste positioning.
        lastMouseGrid = ScreenToGrid(e.Location);
        isMouseOverCanvas = true;

        // Sim mode: hover-probe tooltip on wires.
        if (ProbeProvider is not null)
        {
            Point grid = ScreenToGrid(e.Location);
            Connection? hit = HitTestConnection(grid);

            if (!ReferenceEquals(hit, hoveredConnection))
            {
                hoveredConnection = hit;
                if (hit is not null)
                {
                    string? text = ProbeProvider(hit);
                    if (text is not null)
                        probeTooltip.Show(text, this, e.Location.X + 16, e.Location.Y + 16);
                    else
                        probeTooltip.Hide(this);
                }
                else
                {
                    probeTooltip.Hide(this);
                }
            }
            // don't return -- let normal mouse-move handling continue too
        }

        if (wireStartPin != null)
        {
            wirePreviewEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (headerLinkStart != null)
        {
            headerLinkPreviewEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (panning)
        {
            PanOffset = new PointF(
                panStart.X + (e.X - panAnchor.X),
                panStart.Y + (e.Y - panAnchor.Y));
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (marqueeing)
        {
            marqueeEnd = ScreenToGrid(e.Location);
            Invalidate();
            return;
        }

        if (dragging && dragItems.Count > 0)
        {
            var grid = ScreenToGrid(e.Location);
            int dx = grid.X - dragGridAnchor.X;
            int dy = grid.Y - dragGridAnchor.Y;
            foreach (var (item, start) in dragItems)
                item.Position = new Point(start.X + dx, start.Y + dy);
            routeCache = null;
            coincidentCornersCache = null;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // Sim mode: releasing a held button.
        if (heldButton is not null)
        {
            heldButton.IsPressed = false;
            ButtonPressHandler?.Invoke(heldButton, false);
            heldButton = null;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Middle && panning)
        {
            panning = false;
            Cursor = Cursors.Default;
        }
        if (e.Button == MouseButtons.Left && dragging)
        {
            // Capture each item's destination, revert to start, then push
            // a MoveItemCommand per moved item.
            var moves = dragItems
                .Select(p => (p.Item, From: p.Start, To: p.Item.Position))
                .Where(m => m.From != m.To)
                .ToList();

            foreach (var (item, from, _) in moves)
                item.Position = from;

            if (copyDragInProgress)
            {
                // This drag began as a Ctrl-drag duplicate. BeginCopyDrag
                // already opened the "Duplicate" composite and pasted the
                // clones into it. Record the moves into that SAME composite
                // and close it, so paste + move are one undo step.
                //
                // A zero-move Ctrl-drag (press and release without dragging)
                // still produces a "Duplicate": a copy placed exactly on the
                // original. That's the documented behaviour of the gesture --
                // Ctrl-press on a selected item means "duplicate".
                if (moves.Count > 0)
                {
                    foreach (var (item, from, to) in moves)
                        UndoStack.Do(new MoveItemCommand(item, from, to));
                }
                UndoStack.EndComposite();
                copyDragInProgress = false;
            }
            else if (moves.Count > 0)
            {
                // Ordinary move drag: its own self-contained composite.
                string desc = moves.Count == 1
                    ? $"Move {moves[0].Item.GetType().Name}"
                    : $"Move {moves.Count} items";

                UndoStack.DoComposite(desc, () =>
                {
                    foreach (var (item, from, to) in moves)
                        UndoStack.Do(new MoveItemCommand(item, from, to));
                });
            }

            dragging = false;
            dragItems.Clear();
        }
        if (e.Button == MouseButtons.Left && marqueeing)
        {
            CommitMarquee();
            marqueeing = false;
            Invalidate();
            OnSelectionChanged();
        }
    }

    /// <summary>
    /// Apply the marquee selection. Left-to-right drag (end.X >= start.X)
    /// selects items strictly contained by the rectangle. Right-to-left
    /// drag selects items whose bounding rectangle overlaps. Plain marquee
    /// replaces the selection; Ctrl-marquee adds.
    /// </summary>
    private void CommitMarquee()
    {
        int minX = Math.Min(marqueeStart.X, marqueeEnd.X);
        int maxX = Math.Max(marqueeStart.X, marqueeEnd.X);
        int minY = Math.Min(marqueeStart.Y, marqueeEnd.Y);
        int maxY = Math.Max(marqueeStart.Y, marqueeEnd.Y);
        var marquee = new Rectangle(minX, minY, maxX - minX, maxY - minY);
        bool overlapMode = marqueeEnd.X < marqueeStart.X;

        // Treat a zero-area marquee (a click without drag) as just "clear
        // selection" -- skip the actual hit-test so we don't accidentally
        // pick up degenerate items.
        bool isClick = marquee.Width == 0 && marquee.Height == 0;

        if (!marqueeCtrl) Schematic.ClearSelection();
        if (isClick) return;

        foreach (var item in Schematic.ActiveItems)
        {
            var b = item.Bounds;
            bool match = overlapMode ? marquee.IntersectsWith(b) : marquee.Contains(b);
            if (match) item.Selected = true;
        }

        foreach (var c in Schematic.ActiveConnections)
        {
            var a = c.A.WorldPosition;
            var bp = c.B.WorldPosition;
            int cMinX = Math.Min(a.X, bp.X);
            int cMaxX = Math.Max(a.X, bp.X);
            int cMinY = Math.Min(a.Y, bp.Y);
            int cMaxY = Math.Max(a.Y, bp.Y);
            var cb = new Rectangle(cMinX, cMinY, cMaxX - cMinX, cMaxY - cMinY);

            bool match = overlapMode ? marquee.IntersectsWith(cb) : marquee.Contains(cb);
            if (match) c.Selected = true;
        }
    }

    // ---------------------------------------------------------------- keyboard

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl + C / X / V -- copy / cut / paste. Checked before the
        // unmodified-key branches below so e.g. Ctrl+V isn't mistaken for a
        // bare keystroke. Each sets Handled so the key doesn't travel on.
        if (e.Control && e.KeyCode == Keys.C)
        {
            Copy();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.X)
        {
            Cut();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.V)
        {
            Paste();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete)
        {
            HandleDelete();
        }
        else if (e.KeyCode == Keys.Home)
        {
            ResetView();
        }
        else if (e.KeyCode == Keys.Escape && (awaitingWireStart || wireStartPin != null))
        {
            CancelWirePlacement();
        }
        else if (e.KeyCode == Keys.Escape && (awaitingHeaderLinkStart || headerLinkStart != null))
        {
            CancelHeaderLinkPlacement();
        }
        else if (e.KeyCode == Keys.Escape && marqueeing)
        {
            marqueeing = false;
            Invalidate();
        }
        else if (e.KeyCode == Keys.W && !awaitingWireStart && wireStartPin == null)
        {
            BeginWirePlacement();
        }
        else if (e.KeyCode == Keys.L && !awaitingHeaderLinkStart && headerLinkStart == null
                 && !awaitingWireStart && wireStartPin == null)
        {
            BeginHeaderLinkPlacement();
        }
        else if (e.KeyCode == Keys.Space)
        {
            bool ccw = (e.Modifiers & Keys.Shift) == Keys.Shift;
            RotateSelection(clockwise: !ccw);
            e.Handled = true;
            e.SuppressKeyPress = true;  // don't let Space trigger anything else
        }
        else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                 && e.Modifiers == Keys.None)
        {
            // One press = one one-cell nudge. OnKeyDown fires repeatedly while
            // the key is held; nudgeKeyHeld blocks all but the first event for
            // the current physical press.
            if (nudgeKeyHeld != e.KeyCode)
            {
                nudgeKeyHeld = e.KeyCode;
                int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                NudgeSelection(dx, dy);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == nudgeKeyHeld) nudgeKeyHeld = Keys.None;
    }

    /// <summary>
    /// Move every selected item by (dx, dy) grid units as one composite undo
    /// step. Skipped while a drag, wire-placement, or marquee is in progress
    /// -- those gestures own the canvas. Selected connections are ignored
    /// (they have no Position; they follow their endpoint pins).
    /// </summary>
    private void NudgeSelection(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (dragging || marqueeing || awaitingWireStart || wireStartPin != null) return;

        var items = Schematic.Selected.ToList();
        if (items.Count == 0) return;

        string desc = items.Count == 1
            ? $"Nudge {items[0].GetType().Name}"
            : $"Nudge {items.Count} items";

        UndoStack.DoComposite(desc, () =>
        {
            foreach (var item in items)
            {
                var from = item.Position;
                var to = new Point(from.X + dx, from.Y + dy);
                UndoStack.Do(new MoveItemCommand(item, from, to));
            }
        });

        Invalidate();
    }
}
