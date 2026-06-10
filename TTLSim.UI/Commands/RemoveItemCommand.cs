using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Remove an item from the schematic. Undo puts it back.</summary>
public sealed class RemoveItemCommand : ICommand
{
    private readonly SchematicItem item;

    public RemoveItemCommand(SchematicItem item)
    {
        this.item = item;
        Description = $"Delete {item.GetType().Name}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Remove(item);
    public void Undo(Schematic schematic) => schematic.Add(item);
}