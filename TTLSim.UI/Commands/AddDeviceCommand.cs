using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Add a Device record to the schematic. Note that the Device's Units must
/// be added separately via AddItemCommand -- this command only manages the
/// Devices list. Always paired with AddItemCommands inside a composite.
/// </summary>
public sealed class AddDeviceCommand : ICommand
{
    private readonly Device device;

    public AddDeviceCommand(Device device)
    {
        this.device = device;
        Description = $"Add {device.Designator}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Devices.Add(device);
    public void Undo(Schematic schematic) => schematic.Devices.Remove(device);
}