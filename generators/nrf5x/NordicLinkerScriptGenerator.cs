using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                    "PROVIDE(_edata =__data_end__);",
                    "PROVIDE(__isr_vector = __StackTop);",
                    "PROVIDE(_etext = __etext);"
            };

            //2. Copy the nrf_full.ld file that was manually created from sample linker scripts.
            var fullLines = File.ReadAllLines(Path.Combine(originalSDKDir, @"modules\nrfx\mdk\nrf_full.ld")).ToList();
            fullLines.Add("");
            fullLines.AddRange(providedSymbols);

            File.WriteAllLines(Path.Combine(targetDir, "nrf_full.ld"), commonLines.ToArray());
        }

        public static void DoBuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftdeviceDefinition sd)
        {
            for (LinkerScriptGenerationPass pass = LinkerScriptGenerationPass.BeforeFirst + 1; pass < LinkerScriptGenerationPass.AfterLast; pass++)
                DoBuildLinkerScriptBasedOnOriginalNordicScripts(ldsDirectory, generalizedName, sd, pass);
        }

        static void DoBuildLinkerScriptBasedOnOriginalNordicScripts(string ldsDirectory, string generalizedName, SoftdeviceDefinition sd, LinkerScriptGenerationPass pass)
        {
#if UNUSED
                string[] providedSymbols =
                {
                    "PROVIDE(_sbss = __bss_start__);",
                    "PROVIDE(_ebss = __bss_end__);",
                    "PROVIDE(_sdata = __data_start__);",
                    "PROVIDE(_sidata = __etext);",
                    "PROVIDE(_estack = __StackTop);",
                    "PROVIDE(_edata =__data_end__);",
                    "PROVIDE(__isr_vector = __StackTop);",
                    "PROVIDE(_etext = __etext);"
                };


                string suffix;
                if (pass == LinkerScriptGenerationPass.Nosoftdev)
                    suffix = "nosoftdev";
                else if (pass == LinkerScriptGenerationPass.Reserve)
                    suffix = $"{sd.Name.ToLower()}_reserve";
                else
                    suffix = sd.Name.ToLower();

                int idx = generalizedName.IndexOf('_');
                if (!sd.LinkerScriptWithMaximumReservedRAM.TryGetValue(generalizedName.Substring(0, idx), out var mems))
                {
                    if (pass == LinkerScriptGenerationPass.Nosoftdev)
                        return;
                    File.WriteAllText(Path.Combine(ldsDirectory, $"{generalizedName}_{suffix}.lds"), $"/* The Nordic SDK did not include a linker script for this device/softdevice combination.\r\nIf you would like to use it nonetheless, consider porting another linker script based on the device/softdevice specifications. */\r\nINPUT(UNSUPPORTED_DEVICE_SOFTDEVICE_COMBINATION)\r\n");

                    _MissingSoftdeviceScripts.Add(new MissingSoftdeviceScriptInfo { MCU = generalizedName, Softdevice = sd.Name });
                    return;
                }

                List<string> lines = File.ReadAllLines(mems.FullPath).ToList();
                lines.Insert(0, $"/* Based on {mems.FullPath} */");

                InsertPowerMgmtData(lines);


                if (pass == LinkerScriptGenerationPass.Nosoftdev)
                {
                    idx = lines.FindOrThrow(s => s.Contains("FLASH"));
                    lines[idx] = $"  FLASH (RX) :  ORIGIN = 0x{FLASHBase:x}, LENGTH = 0x{mems.FLASH.Origin + mems.FLASH.Length - FLASHBase:x}";
                    idx = lines.FindOrThrow(s => s.Contains("RAM ("));
                    lines[idx] = $"  RAM (RWX) :  ORIGIN = 0x{SRAMBase:x}, LENGTH = 0x{mems.RAM.Origin + mems.RAM.Length - SRAMBase:x}";
                }
                else
                {
                    lines.Insert(lines.FindOrThrow(s => s.Contains("FLASH")), $"  FLASH_SOFTDEVICE (RX) : ORIGIN = 0x{FLASHBase:x8}, LENGTH = 0x{mems.FLASH.Origin - FLASHBase:x8}");
                    lines.Insert(lines.FindOrThrow(s => s.Contains("RAM")), $"  SRAM_SOFTDEVICE (RWX) : ORIGIN = 0x{SRAMBase:x8}, LENGTH = 0x{mems.RAM.Origin - SRAMBase:x8}");
                    var idxSectionList = lines.FindOrThrow(s => s == "SECTIONS") + 1;
                    while (lines[idxSectionList].Trim() == "{")
                        idxSectionList++;

                    if (lines[idxSectionList].Contains(". = ALIGN"))
                        idxSectionList++;

                    if (pass == LinkerScriptGenerationPass.Regular)
                    {
                        lines.InsertRange(idxSectionList, new[] {
                            "  .softdevice :",
                            "  {",
                            "    KEEP(*(.softdevice))",
                            "    FILL(0xFFFFFFFF);",
                            $"    . = 0x{mems.FLASH.Origin - FLASHBase:x8};",
                            "  } > FLASH_SOFTDEVICE",
                            "",
                            "  .softdevice_sram :",
                            "  {",
                            "    FILL(0xFFFFFFFF);",
                            $"    . = 0x{mems.RAM.Origin - SRAMBase:x8};",
                            "  } > SRAM_SOFTDEVICE"
                            });

                        lines.Insert(lines.FindOrThrow(s => s.Contains("MEMORY")), $"GROUP({sd.Name}_softdevice.o)");
                    }

                }

                lines.AddRange(providedSymbols);

                File.WriteAllLines(Path.Combine(ldsDirectory, $"{generalizedName}_{suffix}.lds"), lines);
#endif
        }


    }
}
