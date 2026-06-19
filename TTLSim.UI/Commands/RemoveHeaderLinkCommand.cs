using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Remove a header link from the schematic. Undo restores it.</summary>
public sealed class RemoveHeaderLinkCommand : ICommand
{
    private readonly HeaderLink link;

    public RemoveHeaderLinkCommand(HeaderLink link)
    {
        this.link = link;
        Description = "Remove Header Link";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Remove(link);
    public void Undo(Schematic schematic) => schematic.Add(link);
}