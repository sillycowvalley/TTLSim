using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>Add a header link to the schematic. Undo removes it.</summary>
public sealed class AddHeaderLinkCommand : ICommand
{
    private readonly HeaderLink link;

    public AddHeaderLinkCommand(HeaderLink link)
    {
        this.link = link;
        Description = "Add Header Link";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Add(link);
    public void Undo(Schematic schematic) => schematic.Remove(link);
}