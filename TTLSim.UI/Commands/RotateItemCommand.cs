using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Rotate an item from one rotation to another. Used for Space/Shift+Space:
/// the canvas computes the new rotation and pushes a single command.
/// </summary>
public sealed class RotateItemCommand : ICommand
{
    private readonly SchematicItem item;
    private readonly Rotation from;
    private readonly Rotation to;

    public RotateItemCommand(SchematicItem item, Rotation from, Rotation to)
    {
        this.item = item;
        this.from = from;
        this.to = to;
        Description = $"Rotate {item.GetType().Name}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => item.Rotation = to;
    public void Undo(Schematic schematic) => item.Rotation = from;
}
