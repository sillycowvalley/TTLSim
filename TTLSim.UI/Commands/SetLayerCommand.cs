using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Move a single item to a different layer. Undo restores its previous layer.
///
/// Layer membership is the only layer operation that goes on the undo stack:
/// it mutates the design (which layer a part lives on). Layer-table changes
/// (add / rename / delete) and visibility toggles are view state and are not
/// recorded -- see <see cref="Schematic.AddLayer"/> and friends.
///
/// A multi-selection "move to layer" wraps one command per item in a composite
/// at the call site (UndoStack.DoComposite), exactly as Nudge and Paste do.
/// </summary>
public sealed class SetLayerCommand : ICommand
{
    private readonly SchematicItem item;
    private readonly int oldLayerId;
    private readonly int newLayerId;

    public SetLayerCommand(SchematicItem item, int oldLayerId, int newLayerId)
    {
        this.item = item;
        this.oldLayerId = oldLayerId;
        this.newLayerId = newLayerId;
        Description = $"Move {item.GetType().Name} to layer";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => item.LayerId = newLayerId;
    public void Undo(Schematic schematic) => item.LayerId = oldLayerId;
}