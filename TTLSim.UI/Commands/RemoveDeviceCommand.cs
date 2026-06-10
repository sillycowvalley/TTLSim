using TTLSim.UI.Model;

namespace TTLSim.UI.Commands;

/// <summary>
/// Remove a Device record from the schematic. The Device's Units must be
/// removed separately via RemoveItemCommands; this command only manages the
/// Devices list. Always paired inside a composite.
/// </summary>
public sealed class RemoveDeviceCommand : ICommand
{
    private readonly Device device;

    public RemoveDeviceCommand(Device device)
    {
        this.device = device;
        Description = $"Delete {device.Designator}";
    }

    public string Description { get; }

    public void Execute(Schematic schematic) => schematic.Devices.Remove(device);
    public void Undo(Schematic schematic) => schematic.Devices.Add(device);
}