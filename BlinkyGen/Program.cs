using System.Diagnostics;
using BlinkyMGen;

// ---------------------------------------------------------------------------
// BlinkyMGen — rev 14 control generator.
//
// From the one canonical instruction table it emits, into <outputFolder>:
//   BLINKY_M_UOPA.pld, BLINKY_M_UOPB.pld     Stage 2 decoder GALs
//   BLINKY_M_SEQ_<bank>.pld, SEQ_ENTRY.pld   Stage 1 sequencer GALs
//   BlinkyMGalTest/BlinkyMGalTest.ino        Arduino Mega GAL burn tester
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

var stopwatch = Stopwatch.StartNew();

var program = InstructionSet.All;
var dict = new MicroDictionary(program);
var seq = new Sequencer(program, dict);

// One dot per generated artifact; the GAL minimization is the slow part, so
// EmitAll reports a dot as each .pld finishes.
Console.Write("Generating");
void Tick() { Console.Write('.'); Console.Out.Flush(); }

PldEmitter.EmitAll(outDir, dict, seq, Tick);

// The GAL burn tester is derived from the .pld files just emitted, so it runs
// after EmitAll. The Arduino IDE requires a sketch in a folder of its own
// name; Write creates that subfolder.
BlinkyMGalTester.Write(outDir, Path.Combine(outDir, "BlinkyMGalTest", "BlinkyMGalTest.ino")); Tick();

HtmlMatrix.Emit(Path.Combine(outDir, "blinky_m_control.html"), program, dict, seq); Tick();
AsmTableEmitter.Emit(Path.Combine(outDir, "OpcodeTable.cs"), program); Tick();
WriteDictionaryListing(Path.Combine(outDir, "dictionary.txt"), program, dict, seq); Tick();


stopwatch.Stop();
Console.WriteLine(" done");
Console.WriteLine();

int named = program.Count(i => !i.Mnemonic.StartsWith("ALU_"));
Console.WriteLine($"Instructions: {program.Count} ({named} named, {program.Count - named} ALU #n)");
Console.WriteLine($"Micro-op dictionary: {dict.Count} of {MicroDictionary.Slots} slots");
Console.WriteLine($"Sequencer banks: {seq.Banks.Count} + entry");
Console.WriteLine($"Elapsed: {stopwatch.ElapsedMilliseconds} ms");
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