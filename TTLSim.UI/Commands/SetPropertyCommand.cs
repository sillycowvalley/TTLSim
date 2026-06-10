using System.Reflection;

namespace TTLSim.UI.Commands;

/// <summary>
/// Generic property setter command. Captures an object, a PropertyInfo, and
/// the old and new values. Execute writes the new value; Undo writes the old.
/// Used to make PropertyGrid edits undoable.
/// </summary>
public sealed class SetPropertyCommand : ICommand
{
    private readonly object target;
    private readonly PropertyInfo property;
    private readonly object? oldValue;
    private readonly object? newValue;

    public SetPropertyCommand(object target, PropertyInfo property,
                              object? oldValue, object? newValue)
    {
        this.target = target;
        this.property = property;
        this.oldValue = oldValue;
        this.newValue = newValue;

        string typeName = target.GetType().Name;
        Description = $"Change {typeName}.{property.Name}";
    }

    public string Description { get; }

    public void Execute(TTLSim.UI.Model.Schematic schematic)
        => property.SetValue(target, newValue);

    public void Undo(TTLSim.UI.Model.Schematic schematic)
        => property.SetValue(target, oldValue);
}