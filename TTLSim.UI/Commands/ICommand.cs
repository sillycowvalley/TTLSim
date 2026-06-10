using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// A reversible mutation of a Schematic. Commands are pushed onto the UndoStack
/// after being executed; the stack calls Undo / Execute again on undo / redo.
/// </summary>
public interface ICommand
{
    /// <summary>Short human-readable description, e.g. "Move 7400". Shown in menus.</summary>
    string Description { get; }

    void Execute(Schematic schematic);
    void Undo(Schematic schematic);
}