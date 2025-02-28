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
        public struct MemoryExtents
        {
            public string Name;
            public uint Start, Length;
            public uint End => Start + Length;

            public override string ToString() => $"{Name}: {Start:x8}..{End:x8}";
        }

        public static Dictionary<string, string> GetXtensaMemoryMappings()
        {
            return new Dictionary<string, string>
            {
                { "drom0_0_seg", "DATA_FLASH" },
                { "iram0_2_seg", "INSTR_FLASH" },
                { "iram0_0_seg", "INSTR_RAM" },
                { "dram0_0_seg", "DATA_RAM" },
                { "irom_seg", "FLASH" },
                { "lp_reserved_seg", "LPRAM" },
            };
        }

        public static readonly string[] CanonicalMemoryOrder = new[] { "DATA_FLASH", "INSTR_FLASH", "DATA_RAM", "INSTR_RAM", "FLASH", "LPRAM", "RAM" };

        public static List<MemoryExtents> LocateMemories(string mapFile)
        {
            List<MemoryExtents> memories = null;
            var rgMem = new Regex("([^ \t]+)[ \t]+0x([0-9a-f]+)[ \t]+0x([0-9a-f]+)(.*)$");
            foreach (var line in File.ReadAllLines(mapFile))
            {
                if (line == "Memory Configuration")
                    memories = new List<MemoryExtents>();
                else if (line == "Linker script and memory map")
                    break;
                else if (memories != null)
                {
                    var m = rgMem.Match(line);
                    if (m.Success)
                    {
                        var extents = new MemoryExtents { Name = m.Groups[1].Value, Start = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber), Length = uint.Parse(m.Groups[3].Value, NumberStyles.HexNumber) };
                        uint padding = extents.Start & 0xFFFFU;

                        extents.Start -= padding;
                        extents.Length += padding;

                        if (extents.Name.Contains("*"))
                            continue;
                        memories.Add(extents);
                    }
                }
            }


            return memories;
        }
    }
}
