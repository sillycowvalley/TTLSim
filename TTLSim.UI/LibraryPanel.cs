using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TTLSim.UI.Components;
using TTLSim.UI.Model;

namespace TTLSim.UI.View;

/// <summary>
/// Library of available parts. TreeView grouped by category. Drag a leaf
/// onto the canvas to instantiate that part at the drop point. The drag
/// payload is a PartDefinition; the canvas's drop handler runs DeviceFactory
/// to materialise the Device and its Units.
///
/// 74xx Logic is sub-grouped. Gate-style parts come from
/// IcPartDefinition.Catalogue and are grouped by their IcCategory.
/// Box-shaped 74xx parts (flip-flops, counters drawn as ChipPartDefinition
/// boxes until dedicated symbol classes land) are added directly to their
/// subcategory node here.
///
/// Timer ICs ("Other ICs"), SRAM, EEPROMs/GALs, passives and power symbols
/// are kept as flat top-level groups.
///
/// VCC and GND symbols are not parts in the Device sense -- they're rail
/// markers, so they carry a SchematicItem factory instead. The drop handler
/// distinguishes the two payload shapes.
/// </summary>
public sealed class LibraryPanel : UserControl
{
    private readonly TreeView tree;

    /// <summary>
    /// Part identifiers whose chips have a working simulation model in
    /// ChipFactory. Everything else in the library renders in grey to flag
    /// it as "drawable but not yet simulated". Update this when a new chip
    /// model lands.
    ///
    /// Passives/sources (resistor, button, VCC, GND, CLK) are always
    /// supported, so they aren't listed here -- AddPart/AddSymbol skip the
    /// lookup for non-chip parts.
    /// </summary>
    private static readonly HashSet<string> SimulatedChipIdentifiers = new(StringComparer.Ordinal)
    {
        "00", "02", "04", "08", "10", "14", "20", "30", "32", "86",
        "47", "74", "139", "153", "157", "161", "163", "173", "181", "182", "191", "244", "245", "257", "273", "283", "374", "377", "390", "393", "541", "688",
        "DS1813",
        "7seg-ca",
        "NE555", "NE556",
        "28C256", "28C128", "28C64", "28C16", "62256", "CY7C199", "6116", "2114", "6264", "W24512",
        "GAL16V8", "GAL20V8", "GAL22V10"
    };

    public LibraryPanel()
    {
        Dock = DockStyle.Fill;

        tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            ItemHeight = 20,
            Font = new Font("Segoe UI", 9f)
        };

        tree.ItemDrag += OnItemDrag;

        PopulateLibrary();

        Controls.Add(tree);
    }

    private void PopulateLibrary()
    {
        var logic = tree.Nodes.Add("74xx Logic");
        PopulateLogicByCategory(logic);
        PopulateLogicBoxParts(logic);
        logic.Expand();

        var other = tree.Nodes.Add("Other ICs");
        AddPart(other, "NE555 - Single Timer", ChipPartDefinition.IcNe555);
        AddPart(other, "NE556 - Dual Timer", ChipPartDefinition.IcNe556);
        AddPart(other, "DS1813 - Reset Supervisor", ChipPartDefinition.Ds1813);
        other.Expand();

        var sram = tree.Nodes.Add("SRAM");
        AddPart(sram, "2114 - 1K x 4 SRAM (200ns)", ChipPartDefinition.Ic2114);
        AddPart(sram, "6116 - 2K x 8 SRAM (70ns)", ChipPartDefinition.Ic6116);
        AddPart(sram, "6264 - 8K x 8 SRAM (20ns)", ChipPartDefinition.Ic6264);
        AddPart(sram, "62256 - 32K x 8 SRAM (55ns)", ChipPartDefinition.Ic62256);
        AddPart(sram, "CY7C199 - 32K x 8 SRAM (35ns)", ChipPartDefinition.Ic7C199);
        AddPart(sram, "W24512 - 64K x 8 SRAM (15ns)", ChipPartDefinition.IcW24512);
        sram.Expand();

        var eepromGal = tree.Nodes.Add("EEPROMs and GALs");
        AddPart(eepromGal, "28C16 - 2K x 8 EEPROM (150ns)", ChipPartDefinition.Ic28C16);
        AddPart(eepromGal, "28C64 - 8K x 8 EEPROM (150ns)", ChipPartDefinition.Ic28C64);
        AddPart(eepromGal, "28C128 - 16K x 8 EEPROM (150ns)", ChipPartDefinition.Ic28C128);
        AddPart(eepromGal, "28C256 - 32K x 8 EEPROM (150ns)", ChipPartDefinition.Ic28C256);
        AddPart(eepromGal, "GAL16V8 - PLD, 8 macrocells (15ns)", ChipPartDefinition.IcGal16V8);
        AddPart(eepromGal, "GAL20V8 - PLD, 8 macrocells (25ns)", ChipPartDefinition.IcGal20V8);
        AddPart(eepromGal, "GAL22V10 - PLD, 10 macrocells (20ns)", ChipPartDefinition.IcGal22V10);
        eepromGal.Expand();

        var passive = tree.Nodes.Add("Passive");
        AddPart(passive, "Resistor", PassivePartDefinition.Resistor);
        AddPart(passive, "Resistor Network", PassivePartDefinition.ResistorNetwork);
        AddPart(passive, "Capacitor", PassivePartDefinition.Capacitor);
        AddPart(passive, "Capacitor, polarized", PassivePartDefinition.PolarizedCapacitor);
        AddPart(passive, "LED", PassivePartDefinition.Led);
        AddPart(passive, "Crystal", PassivePartDefinition.Crystal);
        AddPart(passive, "Diode", PassivePartDefinition.Diode);
        AddPart(passive, "7-Segment, common anode", DisplayPartDefinition.SevenSegmentCommonAnode);
        AddPart(passive, "7-Segment, common cathode", DisplayPartDefinition.SevenSegmentCommonCathode);
        passive.Expand();

        var switches = tree.Nodes.Add("Switches");
        AddPart(switches, "Pushbutton, 2-pin", PassivePartDefinition.Button);
        AddPart(switches, "Pushbutton, 4-pin", PassivePartDefinition.Button4);
        AddPart(switches, "SPST Switch", PassivePartDefinition.Switch);
        AddPart(switches, "SPDT Switch", PassivePartDefinition.SpdtSwitch);
        AddPart(switches, "Jumper, 2-pin", PassivePartDefinition.Jumper2);
        AddPart(switches, "Jumper, 3-pin", PassivePartDefinition.Jumper3);
        switches.Expand();

        var io = tree.Nodes.Add("I/O");
        AddPart(io, "Header out, 2-pin", HeaderPartDefinition.HeaderOut2);
        AddPart(io, "Header out, 3-pin", HeaderPartDefinition.HeaderOut3);
        AddPart(io, "Header out, 4-pin", HeaderPartDefinition.HeaderOut4);
        AddPart(io, "Header out, 6-pin", HeaderPartDefinition.HeaderOut6);
        AddPart(io, "Header out, 8-pin", HeaderPartDefinition.HeaderOut8);
        io.Expand();

        var power = tree.Nodes.Add("Power");
        AddSymbol(power, "VCC", () => new VccSymbol());
        AddSymbol(power, "GND", () => new GndSymbol());
        AddSymbol(power, "CLK", () => new ClockSource());
        AddSymbol(power, "OSC - Canned Oscillator (DIP-14)", () => new CanOscillator());
        AddSymbol(power, "OSC - Canned Oscillator (DIP-8)", () => new CanOscillatorDip8());
        power.Expand();

        // Cosmetic annotations: no electrical meaning, rendered behind the
        // schematic. Standalone SchematicItems, so they use the symbol factory
        // payload like the power rails do.
        var annotation = tree.Nodes.Add("Annotation");
        AddSymbol(annotation, "Rectangle", () => new RectangleItem());
        AddSymbol(annotation, "Text Label", () => new TextLabelItem());
        annotation.Expand();
    }

    /// <summary>
    /// Build 74xx Logic sub-tree from the IcPartDefinition catalogue.
    /// Iterates the catalogue, groups by IcCategory in enum order.
    /// Categories with no entries are skipped so the tree doesn't show
    /// empty headings until the relevant parts land.
    /// </summary>
    private void PopulateLogicByCategory(TreeNode logicRoot)
    {
        foreach (IcCategory category in Enum.GetValues<IcCategory>())
        {
            var inCategory = IcPartDefinition.Catalogue
                .Where(p => p.Category == category)
                .ToList();
            if (inCategory.Count == 0) continue;

            var node = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(category));
            foreach (var part in inCategory)
            {
                AddPart(node, FormatPartLabel(part), part);
            }
            node.Expand();
        }
    }

    /// <summary>
    /// 74xx parts that don't (yet) have a dedicated unit class and are
    /// drawn as named-pin boxes via ChipPartDefinition. Listed here rather
    /// than in a categorised catalogue because there are only a handful
    /// and they're a transitional form until proper symbol classes exist.
    /// </summary>
    private void PopulateLogicBoxParts(TreeNode logicRoot)
    {
        var gates = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Gates));
        AddBoxPart(gates, ChipPartDefinition.Ic7400, "Quad 2-input NAND");
        AddBoxPart(gates, ChipPartDefinition.Ic7402, "Quad 2-input NOR");
        AddBoxPart(gates, ChipPartDefinition.Ic7404, "Hex Inverter");
        AddBoxPart(gates, ChipPartDefinition.Ic7408, "Quad 2-input AND");
        AddBoxPart(gates, ChipPartDefinition.Ic7410, "Triple 3-input NAND");
        AddBoxPart(gates, ChipPartDefinition.Ic7414, "Hex Schmitt Inverter");
        AddBoxPart(gates, ChipPartDefinition.Ic7420, "Dual 4-input NAND");
        AddBoxPart(gates, ChipPartDefinition.Ic7430, "8-input NAND");
        AddBoxPart(gates, ChipPartDefinition.Ic7432, "Quad 2-input OR");
        AddBoxPart(gates, ChipPartDefinition.Ic7486, "Quad 2-input XOR");
        gates.Expand();

        var flipFlops = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.FlipFlops));
        AddBoxPart(flipFlops, ChipPartDefinition.Ic7474, "Dual D Flip-flop");
        AddBoxPart(flipFlops, ChipPartDefinition.Ic74107, "Dual JK Flip-flop, /CLR");
        AddBoxPart(flipFlops, ChipPartDefinition.Ic74175, "Quad D Flip-flop, /MR");
        flipFlops.Expand();

        var registers = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Registers));
        AddBoxPart(registers, ChipPartDefinition.Ic74173, "4-bit D Register, 3-state");
        AddBoxPart(registers, ChipPartDefinition.Ic74273, "Octal D Register, /CLR");
        AddBoxPart(registers, ChipPartDefinition.Ic74299, "8-bit Universal Shift Register");
        AddBoxPart(registers, ChipPartDefinition.Ic74373, "Octal D Latch, 3-state");
        AddBoxPart(registers, ChipPartDefinition.Ic74374, "Octal D Register, 3-state");
        AddBoxPart(registers, ChipPartDefinition.Ic74377, "Octal D Register, /EN");
        AddBoxPart(registers, ChipPartDefinition.Ic74574, "Octal D Register, 3-state");
        AddBoxPart(registers, ChipPartDefinition.Ic74595, "8-bit Shift Register, 3-state");
        registers.Expand();

        var counters = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Counters));
        AddBoxPart(counters, ChipPartDefinition.Ic74161, "4-bit Binary Counter, async clear");
        AddBoxPart(counters, ChipPartDefinition.Ic74163, "4-bit Binary Counter, sync clear");
        AddBoxPart(counters, ChipPartDefinition.Ic74191, "4-bit Up/Down Counter, 1 clock");
        AddBoxPart(counters, ChipPartDefinition.Ic74193, "4-bit Up/Down Counter");
        AddBoxPart(counters, ChipPartDefinition.Ic74390, "Dual Decade Counter");
        AddBoxPart(counters, ChipPartDefinition.Ic74393, "Dual 4-bit Counter");
        counters.Expand();
        counters.Expand();

        // RAM is a one-off subcategory -- not in IcCategory because there's
        // only the '189 in this slot at the moment. Promote to enum if more
        // RAM parts land.
        var ram = logicRoot.Nodes.Add("RAM");
        AddBoxPart(ram, ChipPartDefinition.Ic74189, "64-bit RAM (16x4), 3-state, inverted outputs");
        ram.Expand();

        var decoders = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Decoders));
        AddBoxPart(decoders, ChipPartDefinition.Ic74138, "3-to-8 Decoder");
        AddBoxPart(decoders, ChipPartDefinition.Ic74139, "Dual 2-to-4 Decoder");
        AddBoxPart(decoders, ChipPartDefinition.Ic74154, "4-to-16 Decoder");
        AddBoxPart(decoders, ChipPartDefinition.Ic7447, "BCD-to-7-seg Decoder, common-anode");
        AddBoxPart(decoders, ChipPartDefinition.Ic7448, "BCD-to-7-seg Decoder, common-cathode");
        decoders.Expand();

        var muxes = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Multiplexers));
        AddBoxPart(muxes, ChipPartDefinition.Ic74151, "8-to-1 Mux");
        AddBoxPart(muxes, ChipPartDefinition.Ic74153, "Dual 4-to-1 Mux");
        AddBoxPart(muxes, ChipPartDefinition.Ic74157, "Quad 2-to-1 Mux");
        AddBoxPart(muxes, ChipPartDefinition.Ic74257, "Quad 2-to-1 Mux, 3-state");
        muxes.Expand();

        var buffers = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Buffers));
        AddBoxPart(buffers, ChipPartDefinition.Ic74244, "Octal Buffer, 2x4-bit");
        AddBoxPart(buffers, ChipPartDefinition.Ic74245, "Octal Bus Transceiver");
        AddBoxPart(buffers, ChipPartDefinition.Ic74541, "Octal Buffer, flow-through");
        buffers.Expand();

        var alu = logicRoot.Nodes.Add(IcCategoryLabels.DisplayName(IcCategory.Alu));
        AddBoxPart(alu, ChipPartDefinition.Ic74181, "4-bit ALU");
        AddBoxPart(alu, ChipPartDefinition.Ic74182, "Carry Lookahead Generator");
        AddBoxPart(alu, ChipPartDefinition.Ic74283, "4-bit Binary Adder, fast carry");
        AddBoxPart(alu, ChipPartDefinition.Ic74688, "8-bit Identity Comparator");
        alu.Expand();
    }

    /// <summary>
    /// Add a 74-series boxed chip to the tree with a "74<bare-number> - description"
    /// label. The label carries the bare part number ("7400", "74393", "7447")
    /// but no family letters -- family is per-device and rendered on the canvas
    /// via Device.FullPartNumber, not pinned in the library.
    /// </summary>
    private static void AddBoxPart(TreeNode parent, ChipPartDefinition part, string description) =>
        AddPart(parent, $"74{part.PartNumber} - {description}", part);

    /// <summary>
    /// Display label for a 74xx gate part in the tree: "74<bare-number> - description".
    /// Family letters are NOT included here -- family is per-device and rendered on
    /// the canvas via Device.FullPartNumber, not pinned in the library.
    /// Descriptions live here rather than on the PartDefinition because they're a UI
    /// concern -- if we later want them for tooltips on the canvas too, they move
    /// onto the definition.
    /// </summary>
    private static string FormatPartLabel(IcPartDefinition part) =>
        $"74{part.PartNumber} - {ShortDescription(part)}";

    private static string ShortDescription(IcPartDefinition part) => part.PartNumber switch
    {
        "00" => "Quad NAND",
        "02" => "Quad NOR",
        "04" => "Hex Inverter",
        "14" => "Hex Schmitt Inverter",
        "08" => "Quad AND",
        "10" => "Triple 3-input NAND",
        "20" => "Dual 4-input NAND",
        "30" => "8-input NAND",
        "32" => "Quad OR",
        "86" => "Quad XOR",
        _ => part.PartNumber
    };

    private static void AddPart(TreeNode parent, string displayName, PartDefinition definition)
    {
        var node = parent.Nodes.Add(displayName);
        node.Tag = new LibraryPartDragData(displayName, definition);
        if (!IsSimulated(definition))
            node.ForeColor = SystemColors.GrayText;
    }

    /// <summary>
    /// True if this part has a working simulation model. Passives and signal
    /// sources are always simulated; chips are looked up by part number.
    /// </summary>
    private static bool IsSimulated(PartDefinition definition) => definition switch
    {
        PassivePartDefinition => true,
        HeaderPartDefinition => true,
        IcPartDefinition ic => SimulatedChipIdentifiers.Contains(ic.PartNumber),
        ChipPartDefinition chip => SimulatedChipIdentifiers.Contains(chip.PartNumber),
        DisplayPartDefinition display => SimulatedChipIdentifiers.Contains(display.Identifier),
        _ => false
    };

    private static void AddSymbol(TreeNode parent, string displayName, Func<SchematicItem> factory)
    {
        var node = parent.Nodes.Add(displayName);
        node.Tag = new LibrarySymbolDragData(displayName, factory);
    }

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not TreeNode node) return;

        switch (node.Tag)
        {
            case LibraryPartDragData part:
                DoDragDrop(part, DragDropEffects.Copy);
                break;
            case LibrarySymbolDragData symbol:
                DoDragDrop(symbol, DragDropEffects.Copy);
                break;
        }
    }
}

/// <summary>Drag payload for a part (chip or passive) -- materialises a Device.</summary>
public sealed record LibraryPartDragData(string DisplayName, PartDefinition Definition);

/// <summary>Drag payload for a standalone schematic item (VCC, GND).</summary>
public sealed record LibrarySymbolDragData(string DisplayName, Func<SchematicItem> Factory);