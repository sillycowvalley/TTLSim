using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Remove a connection from the schematic. Undo puts it back.</summary>
public sealed class RemoveConnectionCommand : ICommand
{
    private readonly Connection connection;

    public RemoveConnectionCommand(Connection connection)
    {
        this.connection = connection;
        Description = "Delete Connection";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Remove(connection);
    public void Undo(Schematic schematic) => schematic.Add(connection);
}