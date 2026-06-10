using System.Collections.Generic;
using System.Linq;
using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// A command made up of several sub-commands, executed in order and undone in
/// reverse order. Used for actions like "delete component" that imply removing
/// connected wires as well -- the whole thing is one undo step to the user.
/// </summary>
public sealed class CompositeCommand : ICommand
{
    private readonly List<ICommand> children;

    public CompositeCommand(string description, IEnumerable<ICommand> commands)
    {
        Description = description;
        children = commands.ToList();
    }

    public string Description { get; }

    public bool IsEmpty => children.Count == 0;

    public void Execute(Schematic schematic)
    {
        foreach (var c in children)
            c.Execute(schematic);
    }

    public void Undo(Schematic schematic)
    {
        for (int i = children.Count - 1; i >= 0; i--)
            children[i].Undo(schematic);
    }
}