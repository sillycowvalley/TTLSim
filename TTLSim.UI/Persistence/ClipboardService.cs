using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using TTLSim.UI.Model;

namespace TTLSim.UI.Persistence;

/// <summary>
/// Thrown when a clipboard copy or paste genuinely failed -- the clipboard
/// could not be written or read, the payload could not be deserialised, or it
/// could not be rebuilt into an object graph.
///
/// This exists so failure is LOUD. The previous behaviour logged the reason
/// and returned null/false, which made a failed paste look identical to "there
/// was nothing to paste" -- the user saw nothing happen and the only trace was
/// a log line nobody reads. Callers are expected to catch this and show it.
///
/// Note the deliberate non-failure: an empty clipboard (no TTLSim payload
/// present) is NOT an error and does not throw -- <see cref="ClipboardService.Paste"/>
/// returns null for that, because "nothing to paste" is a normal no-op.
/// </summary>
public sealed class ClipboardException : Exception
{
    public ClipboardException(string message) : base(message) { }
    public ClipboardException(string message, Exception inner) : base(message, inner) { }
}

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
/// <para>
/// Failure policy: genuine failures throw <see cref="ClipboardException"/> so
/// the caller can surface them. The ONLY soft outcomes are the two that are
/// genuinely "nothing happened, and that's fine": an empty selection (copy)
/// and an empty clipboard (paste). Everything else -- a clipboard the OS won't
/// let us read or write, a payload that won't deserialise, content that won't
/// rebuild -- is raised, never swallowed.
/// </para>
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
    /// <para>
    /// Returns true on success, false only when there was nothing to copy (an
    /// empty item selection). Throws <see cref="ClipboardException"/> if the
    /// selection could not be serialised or the clipboard write failed --
    /// surfaced loudly rather than silently treated as "nothing happened".
    /// </para>
    /// </summary>
    public static bool Copy(
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<SchematicItem> items,
        IReadOnlyCollection<Connection> connections,
        IReadOnlyCollection<HeaderLink>? links = null,
        IReadOnlyList<Layer>? layers = null)
    {
        if (items.Count == 0)
        {
            // A selection with no items can't produce a meaningful paste --
            // connections alone have no endpoints to land on. This is a true
            // no-op, not a failure.
            return false;
        }

        string json;
        try
        {
            var dto = SchematicDtoMapper.ToDto(devices, items, connections, links, layers);
            json = JsonSerializer.Serialize(dto);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to serialise selection for copy.");
            throw new ClipboardException(
                "The selection could not be prepared for the clipboard.", ex);
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
            // ExternalException is the documented failure when another process
            // holds the clipboard. Previously this was swallowed to a false;
            // now it is raised so the user learns the copy did NOT happen
            // rather than discovering it at the next (stale) paste.
            Log.LogError(ex, "Clipboard write failed; copy did not complete.");
            throw new ClipboardException(
                "The clipboard could not be written (another application may be holding it). " +
                "Nothing was copied.", ex);
        }
    }

    /// <summary>
    /// Identical to <see cref="Copy"/>. Cut and copy place the SAME payload on
    /// the clipboard; the difference is purely that the caller removes the
    /// originals after a successful cut. Removal is intentionally NOT done
    /// here -- it belongs on the undo stack, which the canvas owns. Kept as a
    /// separate named method so call sites read clearly and so a future cut
    /// could diverge without churning callers.
    ///
    /// <para>
    /// Throws <see cref="ClipboardException"/> on a genuine clipboard failure,
    /// exactly like <see cref="Copy"/>. A caller implementing cut MUST let that
    /// propagate (or catch it) BEFORE deleting the originals -- a cut whose
    /// copy failed must never destroy anything.
    /// </para>
    /// </summary>
    public static bool Cut(
        IReadOnlyCollection<Device> devices,
        IReadOnlyCollection<SchematicItem> items,
        IReadOnlyCollection<Connection> connections,
        IReadOnlyCollection<HeaderLink>? links = null,
        IReadOnlyList<Layer>? layers = null)
        => Copy(devices, items, connections, links, layers);

    // ----------------------------------------------------------------- Paste

    /// <summary>
    /// True when the clipboard currently holds a TTLSim payload. Cheap enough
    /// to call from a menu's enabled-state binding. Guarded: a clipboard probe
    /// can itself throw, in which case we report false -- a probe failure only
    /// affects whether a menu item is greyed, so degrading it to "nothing to
    /// paste" is harmless and must not throw from a property getter.
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
    /// <para>
    /// Returns null ONLY when there is genuinely nothing to paste -- the
    /// clipboard holds no TTLSim payload. That is a normal no-op.
    /// </para>
    ///
    /// <para>
    /// Throws <see cref="ClipboardException"/> for every actual failure: the
    /// clipboard could not be read, the payload was present but empty, it would
    /// not deserialise, or it would not rebuild. These were previously logged
    /// and returned as null -- indistinguishable from "nothing to paste", which
    /// is exactly how a real failure went unnoticed.
    /// </para>
    ///
    /// <para>
    /// A successful rebuild can still be PARTIAL: see
    /// <see cref="SchematicDtoMapper.MapResult.IsPartial"/>. That is not a
    /// failure (the parts that could be rebuilt are returned), but the caller
    /// must surface it -- a paste that quietly placed fewer parts than were
    /// copied is the same class of silent failure this method now avoids.
    /// </para>
    /// </summary>
    public static SchematicDtoMapper.MapResult? Paste(Schematic designatorScope)
    {
        if (designatorScope is null)
            throw new ArgumentNullException(nameof(designatorScope));

        string? json;
        try
        {
            if (!Clipboard.ContainsData(ClipboardFormat))
                return null;   // nothing to paste -- normal no-op (see remarks)
            json = Clipboard.GetData(ClipboardFormat) as string;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Clipboard read failed; paste did not complete.");
            throw new ClipboardException(
                "The clipboard could not be read (another application may be holding it). " +
                "Nothing was pasted.", ex);
        }

        if (string.IsNullOrEmpty(json))
        {
            // ContainsData said yes but the data is empty -- a genuinely
            // malformed clipboard state, not "nothing to paste".
            Log.LogError("Clipboard reported a TTLSim payload but it was empty.");
            throw new ClipboardException(
                "The clipboard reported a TTLSim payload but it was empty. Nothing was pasted.");
        }

        SchematicDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SchematicDto>(json);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Clipboard payload could not be deserialised.");
            throw new ClipboardException(
                "The clipboard contents could not be read as a TTLSim selection " +
                "(it may have been copied by an incompatible version). Nothing was pasted.", ex);
        }

        if (dto is null)
            throw new ClipboardException(
                "The clipboard contents could not be read as a TTLSim selection. Nothing was pasted.");

        SchematicDtoMapper.MapResult result;
        try
        {
            result = SchematicDtoMapper.FromDto(dto, IdPolicy.Fresh, designatorScope);
        }
        catch (Exception ex)
        {
            // FromDto throws on genuinely malformed content (unknown part
            // number, bad family, ...). For a payload this app wrote that means
            // a real bug -- exactly the case that must NOT be swallowed.
            Log.LogError(ex, "Failed to rebuild clipboard payload; paste did not complete.");
            throw new ClipboardException(
                $"The clipboard selection could not be rebuilt: {ex.Message}", ex);
        }

        if (result.IsPartial)
        {
            // Not a failure -- return what rebuilt -- but log it at warning so
            // it is distinct from a clean paste. The caller surfaces the
            // user-facing "pasted N of M" message.
            Log.LogWarning(
                "Partial paste: {SkippedUnits} unit(s), {DroppedConnections} connection(s), and " +
                "{DroppedLinks} header link(s) in the payload could not be rebuilt and were omitted.",
                result.SkippedUnits, result.DroppedConnections, result.DroppedLinks);
        }

        Log.LogInformation(
            "Pasted {DeviceCount} device(s), {ItemCount} item(s), {ConnectionCount} connection(s).",
            result.Devices.Count, result.Items.Count, result.Connections.Count);
        return result;
    }
}