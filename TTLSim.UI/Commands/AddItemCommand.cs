using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Add an item to the schematic. Undo removes it.</summary>
public sealed class AddItemCommand : ICommand
{
    private readonly SchematicItem item;

    public AddItemCommand(SchematicItem item)
    {
        this.item = item;
        Description = $"Add {item.GetType().Name}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Add(item);
    public void Undo(Schematic schematic) => schematic.Remove(item);
}