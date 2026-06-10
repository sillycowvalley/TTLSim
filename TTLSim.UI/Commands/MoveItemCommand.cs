using System.Drawing;
using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Move an item from one grid position to another. Used for drag-to-move:
/// the interactive drag mutates Position directly, and on mouse-up we record
/// a single MoveItemCommand capturing the net delta.
/// </summary>
public sealed class MoveItemCommand : ICommand
{
    private readonly SchematicItem item;
    private readonly Point from;
    private readonly Point to;

    public MoveItemCommand(SchematicItem item, Point from, Point to)
    {
        this.item = item;
        this.from = from;
        this.to = to;
        Description = $"Move {item.GetType().Name}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => item.Position = to;
    public void Undo(Schematic schematic) => item.Position = from;
}