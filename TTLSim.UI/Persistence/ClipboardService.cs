using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence;

/// <summary>
/// Copy / cut / paste of a schematic selection, via the Windows clipboard.
///
/// The payload is a <see cref="SchematicDto"/> serialised to JSON and stored
/// under a single TTLSim-specific clipboard format -- nothing readable by
/// other applications is placed on the clipboard, so a stray Ctrl+V into a
/// text editor does nothing and paste is unambiguous: either the clipboard
/// holds a TTLSim payload or there is nothing to paste.
///
/// <para>
/// This service is deliberately thin. It touches ONLY the clipboard. It never
/// mutates a <see cref="Schematic"/>:
/// </para>
/// <list type="bullet">
///   <item><see cref="Copy"/> / <see cref="Cut"/> only read the selection and
///   write the clipboard. The actual removal for a cut is the caller's job
///   (the canvas does it through its existing delete-composite path) so undo
///   stays correct and lives in one place.</item>
///   <item><see cref="Paste"/> rebuilds the object graph with fresh ids but
///   does NOT add anything to the schematic and does NOT apply a paste
///   offset. The caller positions the returned items (cursor point or
///   cascade offset) and adds them through the undo stack.</item>
/// </list>
///
/// All clipboard access is guarded: the Windows clipboard can genuinely throw
/// (<see cref="System.Runtime.InteropServices.ExternalException"/>) when
/// another process has it locked. A failure logs and degrades to a no-op
/// rather than taking the editor down.
/// </summary>
public static class ClipboardService
{
    /// <summary>
    /// Clipboard format name for the TTLSim payload. Private on purpose --
    /// callers ask <see cref="CanPaste"/> rather than poking the clipboard
    /// directly.
    /// </summary>
    private const string ClipboardFormat = "TTLSim.Schematic";

    private static readonly ILogger Log = Logging.Log.For(nameof(ClipboardService));

    // ------------------------------------------------------------------ Copy

    /// <summary>
    /// Serialise a selection to the clipboard.
    ///
    /// <para>
    /// The caller supplies the sets directly. Typically that is every
    /// selected item, every <see cref="Device"/> owning a selected unit, and
    /// every selected connection. <see cref="SchematicDtoMapper.ToDto"/> drops
    /// connections whose endpoints fall outside <paramref name="items"/>, so
    /// the caller does not need to pre-filter them.
    /// </para>
    ///
    /// <para>Returns true on success, false if there was nothing to copy or
    /// the clipboard write failed.</para>
    /// </summary>
    public static bool Copy(
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<SchematicItem> items,
        IReadOnlyCollection<Connection> connections)
    {
        if (items.Count == 0)
        {
            // A selection with no items can't produce a meaningful paste --
            // connections alone have no endpoints to land on.
            return false;
        }

        string json;
        try
        {
            var dto = SchematicDtoMapper.ToDto(devices, items, connections);
            json = JsonSerializer.Serialize(dto);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to serialise selection for copy.");
            return false;
        }

        try
        {
            // Copy: true so the data survives this process exiting, matching
            // normal clipboard expectations.
            Clipboard.SetData(ClipboardFormat, json);
            Log.LogInformation(
                "Copied {DeviceCount} device(s), {ItemCount} item(s), {ConnectionCount} connection(s) to clipboard.",
                devices.Count, items.Count, connections.Count);
            return true;
        }
        catch (Exception ex)
        {
            // ExternalException is the documented failure when another
            // process holds the clipboard; catch broadly so a copy can never
            // crash the editor.
            Log.LogWarning(ex, "Clipboard write failed; copy did not complete.");
            return false;
        }
    }

    /// <summary>
    /// Identical to <see cref="Copy"/>. Cut and copy place the SAME payload on
    /// the clipboard; the difference is purely that the caller removes the
    /// originals after a successful cut. Removal is intentionally NOT done
    /// here -- it belongs on the undo stack, which the canvas owns. Kept as a
    /// separate named method so call sites read clearly and so a future cut
    /// could diverge without churning callers.
    /// </summary>
    public static bool Cut(
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<SchematicItem> items,
        IReadOnlyCollection<Connection> connections)
        => Copy(devices, items, connections);

    // ----------------------------------------------------------------- Paste

    /// <summary>
    /// True when the clipboard currently holds a TTLSim payload. Cheap enough
    /// to call from a menu's enabled-state binding. Guarded: a clipboard probe
    /// can itself throw, in which case we report false.
    /// </summary>
    public static bool CanPaste
    {
        get
        {
            try
            {
                return Clipboard.ContainsData(ClipboardFormat);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Clipboard probe failed; reporting nothing to paste.");
                return false;
            }
        }
    }

    /// <summary>
    /// Read the clipboard payload and rebuild it as a detached object graph
    /// with fresh ids and designators.
    ///
    /// <para>
    /// The result is NOT added to <paramref name="designatorScope"/> -- that
    /// schematic is passed only so new designators can be made unique against
    /// what is already placed. The caller applies a position offset to the
    /// returned items and commits them through the undo stack.
    /// </para>
    ///
    /// <para>Returns null when there is nothing to paste or the payload could
    /// not be read or rebuilt; in every failure case the reason is logged.</para>
    /// </summary>
    public static SchematicDtoMapper.MapResult? Paste(Schematic designatorScope)
    {
        if (designatorScope is null)
            throw new ArgumentNullException(nameof(designatorScope));

        string? json;
        try
        {
            if (!Clipboard.ContainsData(ClipboardFormat))
                return null;
            json = Clipboard.GetData(ClipboardFormat) as string;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Clipboard read failed; paste did not complete.");
            return null;
        }

        if (string.IsNullOrEmpty(json))
        {
            Log.LogWarning("Clipboard reported a TTLSim payload but it was empty.");
            return null;
        }

        SchematicDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SchematicDto>(json);
        }
        catch (Exception ex)
        {
            // A payload that won't deserialise is most likely stale -- e.g.
            // copied before an incompatible change to the DTO shape. Treat it
            // as "nothing to paste" rather than an error the user must see.
            Log.LogWarning(ex, "Clipboard payload could not be deserialised; treating as nothing to paste.");
            return null;
        }

        if (dto is null)
        {
            Log.LogWarning("Clipboard payload deserialised to null; treating as nothing to paste.");
            return null;
        }

        try
        {
            var result = SchematicDtoMapper.FromDto(dto, IdPolicy.Fresh, designatorScope);
            Log.LogInformation(
                "Pasted {DeviceCount} device(s), {ItemCount} item(s), {ConnectionCount} connection(s).",
                result.Devices.Count, result.Items.Count, result.Connections.Count);
            return result;
        }
        catch (Exception ex)
        {
            // FromDto throws on genuinely malformed content (unknown part
            // number, bad family, ...). That shouldn't happen for a payload
            // this app wrote, but if it does, fail soft.
            Log.LogError(ex, "Failed to rebuild clipboard payload; paste did not complete.");
            return null;
        }
    }
}