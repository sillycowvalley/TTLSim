using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Add a connection to the schematic. Undo removes it.</summary>
public sealed class AddConnectionCommand : ICommand
{
    private readonly Connection connection;

    public AddConnectionCommand(Connection connection)
    {
        this.connection = connection;
        Description = "Add Connection";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Add(connection);
    public void Undo(Schematic schematic) => schematic.Remove(connection);
}