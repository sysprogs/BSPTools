using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPGenerationTools
{
    public class LinkerScriptTools
    {
        public static MCUMemory[] ScanLinkerScriptForMemories(string linkerScript)
        {
            if (!File.Exists(linkerScript))
                throw new Exception($"Missing {linkerScript}");

            bool insideMemoryBlock = false;

            Regex rgMemory = new Regex("([a-zA-Z0-9_]+)[ \t]+\\([^()]+\\)[ \t:]+ORIGIN[ \t]*=[ \t]*0x([0-9A-Fa-f]+),[ \t]*LENGTH[ \t]*=[ \t]*0x([0-9A-Fa-f]+)");
            List<MCUMemory> memories = new List<MCUMemory>();

            foreach (var line in File.ReadAllLines(linkerScript))
            {
                if (line.Trim() == "MEMORY")
                    insideMemoryBlock = true;
                else if (line.Trim() == "}")
                    insideMemoryBlock = false;
                else if (insideMemoryBlock)
                {
                    var m = rgMemory.Match(line);
                    if (m.Success)
                    {
                        memories.Add(new MCUMemory { Name = m.Groups[1].Value, Address = ulong.Parse(m.Groups[2].Value, NumberStyles.HexNumber), Size = ulong.Parse(m.Groups[3].Value, NumberStyles.HexNumber) });
                    }
                }
            }

            return memories.ToArray();
        }
    }
}
