using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ESP32ToolchainUpdater
{
    internal class MemoryMapUpdater
    {
        struct MemoryExtents
        {
            public uint Start, Length;
            public uint End => Start + Length;

            public override string ToString() => $"{Start:x8}..{End:x8}";
        }

        static string MapMemoryName(string nameFromBSP) => nameFromBSP switch
        {
            "DATA_FLASH" => "drom0_0_seg",
            "INSTR_FLASH" => "iram0_2_seg",
            "INSTR_RAM" => "iram0_0_seg",
            "DATA_RAM" => "dram0_0_seg",

            "FLASH" => "irom_seg",
            "RAM" => "iram0_0_seg",
            "LPRAM" => "lp_reserved_seg",
        };

        public static void UpdateMemoryMap(string bspXML, string deviceName, string mapFile)
        {
            Dictionary<string, MemoryExtents> memories = null;
            var rgMem = new Regex("([id]r[ao]m[^ \t]+|lp_reserved_seg)[ \t]+0x([0-9a-f]+)[ \t]+0x([0-9a-f]+)(.*)$");
            foreach(var line in File.ReadAllLines(mapFile))
            {
                if (line == "Memory Configuration")
                    memories = new Dictionary<string, MemoryExtents>();
                else if (line == "Linker script and memory map")
                    break;
                else if (memories != null)
                {
                    var m = rgMem.Match(line);
                    if (m.Success)
                    {
                        var extents = new MemoryExtents { Start = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber), Length = uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber) };
                        uint padding = extents.Start & 0xFFFFU;

                        extents.Start -= padding;
                        extents.Length += padding;

                        memories[m.Groups[1].Value] = extents;
                    }
                }
            }

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspXML);
            var dev = bsp.SupportedMCUs.FirstOrDefault(m => m.ID == deviceName) ?? throw new Exception("Unknown MCU:" + deviceName);
            foreach(var mem in dev.MemoryMap.Memories)
            {
                var extents = memories[MapMemoryName(mem.Name)];
                mem.Address = extents.Start;
                mem.Size = extents.Length;
            }

            XmlTools.SaveObject(bsp, bspXML);
        }
    }
}
