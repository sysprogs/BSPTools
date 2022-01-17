using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace stm8_bsp_generator
{
    public struct CXSTM8MCU
    {
        public string MCUName, Family;
        public string[] LinkerScript;
        public string[] VectorsFile;
        public string[] Options;

        public override string ToString() => MCUName;

        public void GenerateScriptsAndVectors(string outputDir)
        {
            var dir = Path.Combine(outputDir, Family);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, MCUName + ".lkf"), LinkerScript);
            File.WriteAllLines(Path.Combine(dir, MCUName + "_vectors.c"), VectorsFile);
        }
    }

    static class CXSTM8DefinitionParser
    {
        static string[] SpliceArray(string[] arr, int first, int count)
        {
            string[] result = new string[count];
            Array.Copy(arr, first, result, 0, count);
            return result;
        }

        public static CXSTM8MCU[] ParseMCUs(string cxstm8Dir, Action<string> warningHandler = null)
        {
            var devDir = Path.Combine(cxstm8Dir, "Devices_sm8");
            if (!Directory.Exists(devDir))
                throw new DirectoryNotFoundException("Missing " + devDir);

            List<CXSTM8MCU> mcus = new List<CXSTM8MCU>();

            foreach (var famDir in Directory.GetDirectories(devDir))
            {
                foreach (var fn in Directory.GetFiles(famDir, "*.tgt", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(fn);
                    string section = null;
                    Dictionary<string, string[]> sectionContents = new Dictionary<string, string[]>(StringComparer.InvariantCultureIgnoreCase);
                    int bodyStart = -1;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.StartsWith("["))
                        {
                            if (section != null && bodyStart < i)
                                sectionContents[section] = SpliceArray(lines, bodyStart, i - bodyStart);

                            section = line.Trim(' ', '[', ']');
                            bodyStart = i + 1;
                        }
                    }

                    if (section != null && bodyStart < lines.Length)
                        sectionContents[section] = SpliceArray(lines, bodyStart, lines.Length - bodyStart);

                    var mcu = ParseMCUs(Path.GetFileName(famDir), fn, sectionContents, warningHandler);
                    if (mcu.MCUName != null)
                        mcus.Add(mcu);
                }
            }

            return mcus.ToArray();
        }

        private static CXSTM8MCU ParseMCUs(string family, string fn,  Dictionary<string, string[]> sectionContents, Action<string> warningHandler)
        {
            CXSTM8MCU mcu = new CXSTM8MCU { Family = family.ToUpper() };

            if (!sectionContents.TryGetValue("Link", out mcu.LinkerScript) || !sectionContents.TryGetValue("Vector", out mcu.VectorsFile))
                return default;

            if (!sectionContents.TryGetValue("Target", out var lines))
                return default;

            foreach (var line in lines)
            {
                int idx = line.IndexOf('=');
                if (idx == -1)
                    continue;

                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();

                if (StringComparer.InvariantCultureIgnoreCase.Compare(key, "Name") == 0)
                    mcu.MCUName = value.ToUpper();
                else
                    warningHandler?.Invoke($"{fn}: Unexpected target parameter: {key}");
            }

            if (sectionContents.TryGetValue("Option", out lines))
                mcu.Options = lines.SelectMany(l => l.Split(' ')).Select(s => s.Trim()).Where(o => o != "").ToArray();

            if (mcu.Options?.Length != 1)
                warningHandler?.Invoke($"{fn}: Unexpected options count: {mcu.Options?.Length}");

            return mcu;
        }
    }
}
