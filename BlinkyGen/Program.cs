using BlinkyMGen;

// ---------------------------------------------------------------------------
// BlinkyMGen — rev 14 control generator.
//
// From the one canonical instruction table it emits, into <outputFolder>:
//   BLINKY_M_UOPA.pld, BLINKY_M_UOPB.pld     Stage 2 decoder GALs
//   BLINKY_M_SEQ_<family>.pld, SEQ_ENTRY.pld Stage 1 sequencer GALs
//   blinky_m_control.html                    three-view control reference
//   OpcodeTable.cs                           BlinkyASM opcode map
//   dictionary.txt                           the micro-op dictionary listing
//
// No microcode EEPROM images: rev 14 control is fuses, not ROM.
//
// Usage:  BlinkyMGen <outputFolder>
// ---------------------------------------------------------------------------

if (args.Length != 1)
{
    Console.WriteLine("Usage: BlinkyMGen <outputFolder>");
    Console.WriteLine("Emits the rev 14 GAL sources, HTML control reference, and assembler table.");
    return;
}

string outDir = args[0];
Directory.CreateDirectory(outDir);

var program = InstructionSet.All;
var dict = new MicroDictionary(program);
var seq = new Sequencer(program, dict);

PldEmitter.EmitAll(outDir, dict, seq);
HtmlMatrix.Emit(Path.Combine(outDir, "blinky_m_control.html"), program, dict, seq);
AsmTableEmitter.Emit(Path.Combine(outDir, "OpcodeTable.cs"), program);
WriteDictionaryListing(Path.Combine(outDir, "dictionary.txt"), program, dict, seq);

int named = program.Count(i => !i.Mnemonic.StartsWith("ALU_"));
Console.WriteLine($"Instructions: {program.Count} ({named} named, {program.Count - named} ALU #n)");
Console.WriteLine($"Micro-op dictionary: {dict.Count} of {MicroDictionary.Slots} slots");
Console.WriteLine($"Sequencer banks: {seq.Families.Count(f => f != 0xF)} families + entry");
Console.WriteLine($"Output -> {Path.GetFullPath(outDir)}");
return;

static void WriteDictionaryListing(string path, IReadOnlyList<Instruction> program,
                                   MicroDictionary dict, Sequencer seq)
{
    using var w = new StreamWriter(path);
    w.WriteLine("Blinky-M rev 14 micro-op dictionary");
    w.WriteLine($"{dict.Count} micro-ops of {MicroDictionary.Slots} slots ({MicroDictionary.IndexBits}-bit index)");
    w.WriteLine();
    for (int i = 0; i < dict.Count; i++)
        w.WriteLine($"  {i,3}: {dict.Ops[i].Describe()}");
    w.WriteLine();
    w.WriteLine("Instruction sequences (index lists):");
    foreach (var ins in program)
    {
        string s = string.Join(" ", ins.Steps.Select(x => dict.Index(x.Op)));
        string tk = ins.TakenSteps is { } t ? "  taken: " + string.Join(" ", t.Select(x => dict.Index(x.Op))) : "";
        w.WriteLine($"  0x{ins.Opcode:X2} {ins.Mnemonic,-7} {s}{tk}");
    }
}
