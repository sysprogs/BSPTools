using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace nrf5x
{
    class NordicLinkerScriptGenerator
    {
        enum LinkerScriptGenerationPass
        {
            BeforeFirst,

            Regular,
            Reserve,
            Nosoftdev,

            AfterLast
        }

        public static void CopyCommonScripts(string originalSDKDir, string targetDir)
        {
            //1. Copy the nrf_common.lds file from the original SDK.
            var commonLines = File.ReadAllLines(Path.Combine(originalSDKDir, @"modules\nrfx\mdk\nrf_common.ld")).ToList();
            int idx = commonLines.IndexOf("    .text :");
            if (idx == -1)
                throw new Exception("Could not find the beginning of section .text");
            commonLines.Insert(idx, "    _stext = .;");

            File.WriteAllLines(Path.Combine(targetDir, "nrf_common.ld"), commonLines.ToArray());

            string[] providedSymbols =
            {
                    "PROVIDE(_sbss = __bss_start__);",
                    "PROVIDE(_ebss = __bss_end__);",
                    "PROVIDE(_sdata = __data_start__);",
                    "PROVIDE(_sidata = __etext);",
                    "PROVIDE(_estack = __StackTop);",
                    "PROVIDE(_edata = __data_end__);",
                    "PROVIDE(__isr_vector = __StackTop);",
                    "PROVIDE(_etext = __etext);"
            };

            //2. Copy the nrf_full.ld file that was manually created from sample linker scripts.
            var fullLines = File.ReadAllLines(Path.Combine(originalSDKDir, @"modules\nrfx\mdk\nrf_full.ld")).ToList();
            fullLines.Add("");
            fullLines.AddRange(providedSymbols);

            var rgSection = new Regex("[ \t]+(\\.[a-zA-Z0-9_]+)[ \t]*:");
            var sections = fullLines.Select(l => rgSection.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            foreach (var sec in new[] { ".pwr_mgmt_data", ".sdh_soc_observers", ".sdh_stack_observers" })
                if (!sections.Contains(sec))
                    throw new Exception($"Missing {sec} in nrf_full.ld");

            File.WriteAllLines(Path.Combine(targetDir, "nrf_full.ld"), fullLines.ToArray());
        }

        public static void BuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, NordicMCUBuilder mcu, SoftdeviceDefinition sd)
        {
            for (LinkerScriptGenerationPass pass = LinkerScriptGenerationPass.BeforeFirst + 1; pass < LinkerScriptGenerationPass.AfterLast; pass++)
                DoBuildLinkerScriptBasedOnOriginalNordicScripts(ldsDirectory, generalizedName, mcu, sd, pass);
        }

        static void DoBuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, NordicMCUBuilder mcu, SoftdeviceDefinition sd, LinkerScriptGenerationPass pass)
        {
            string suffix;
            if (pass == LinkerScriptGenerationPass.Nosoftdev)
                suffix = "nosoftdev";
            else if (pass == LinkerScriptGenerationPass.Reserve)
                suffix = $"{sd.Name.ToLower()}_reserve";
            else
                suffix = sd.Name.ToLower();

            int idx = generalizedName.IndexOf('_');

            using(var sw = File.CreateText(Path.Combine(ldsDirectory, $"{generalizedName}_{suffix}.lds")))
            {
                sw.WriteLine($"/* Linker script for {mcu.Name} */");
                sw.WriteLine();
                if (pass == LinkerScriptGenerationPass.Regular)
                {
                    sw.WriteLine($"GROUP({sd.Name}_softdevice.o)");
                }

                sw.WriteLine("MEMORY");
                sw.WriteLine("{");
                foreach(var mem in mcu.Summary.MemoryLines)
                {
                    var line = mem.Line;
                    if (pass != LinkerScriptGenerationPass.Nosoftdev)
                    {
                        ulong reservedSize = mem.Name switch
                        {
                            "FLASH" => sd.ReservedFLASH,
                            "RAM" => sd.ReservedRAM,
                            _ => 0
                        };

                        if (reservedSize != 0)
                        {
                            if (pass == LinkerScriptGenerationPass.Regular)
                                sw.WriteLine(mem.Reformat(mem.Name + "_SOFTDEVICE", mem.Origin, reservedSize));

                            line = mem.Reformat(null, mem.Origin + reservedSize, mem.Size - reservedSize);
                        }
                    }

                    sw.WriteLine(line);
                }
                sw.WriteLine("}");

                if (pass == LinkerScriptGenerationPass.Regular)
                {
                    var summary = mcu.Summary;
                    var lines =  new[] {
                        "  .softdevice :",
                        "  {",
                        "    KEEP(*(.softdevice))",
                        "    FILL(0xFFFFFFFF);",
                        $"    . = 0x{sd.ReservedFLASH:x8};",
                        "  } > FLASH_SOFTDEVICE",
                        "",
                        "  .softdevice_sram :",
                        "  {",
                        "    FILL(0xFFFFFFFF);",
                        $"    . = 0x{sd.ReservedRAM:x8};",
                        "  } > RAM_SOFTDEVICE"
                        };

                    sw.WriteLine("");
                    sw.WriteLine("SECTIONS");
                    sw.WriteLine("{");
                    foreach (var line in lines)
                        sw.WriteLine(line);
                    sw.WriteLine("}");
                }

                sw.WriteLine("");
                sw.WriteLine("INCLUDE \"nrf_full.ld\"");
            }
        }
    }
}
