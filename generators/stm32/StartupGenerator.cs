/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;

namespace stm32_bsp_generator
{
    static class StartupGenerator
    {
        public static void GenerateStartupFile(string assemblyInput, string templateFile)
        {
            Regex rgBootRam = new Regex(".equ[ \t]+BootRAM,[ \t]+(0x[0-9a-fA-F]+)$");
            Regex rgVectorEntry = new Regex("[ \t]+.word[ \t]+([^ \t]+)($|[ \t])");

            List<string> vectors = new List<string>();
            string bootRam = null;
            bool insideVectors = false;

            foreach (var line in File.ReadAllLines(assemblyInput))
            {
                string trimmed = line.Trim();
                if (trimmed == "" || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                    continue;
                var m = rgBootRam.Match(line);
                if (m.Success)
                    bootRam = m.Groups[1].ToString();
                else if (line == "g_pfnVectors:")
                    insideVectors = true;
                else if (insideVectors)
                {
                    if ((m = rgVectorEntry.Match(line)).Success)
                    {
                        vectors.Add(m.Groups[1].ToString());
                        if (vectors[vectors.Count - 1] == "BootRAM")
                            break;
                    }
                    else if (bootRam == null && line.Contains(".weak"))
                        break;
                    else
                        throw new Exception("Unrecognized line inside vectors!");
                }
            }

            using (var sw = new StreamWriter(Path.ChangeExtension(assemblyInput, ".c")))
            {
                string[] allLines = File.ReadAllLines(templateFile);
                int line = 0;
                while (line < allLines.Length)
                {
                    if (allLines[line] == "$$GENERATED_STARTUP_VECTOR$$")
                    {
                        line++;
                        break;
                    }
                    sw.WriteLine(allLines[line++]);
                }

                if (bootRam != null)
                    sw.WriteLine("#define BootRAM ((void *){0})", bootRam);

                sw.WriteLine("");
                int maxLen = (from v in vectors select v.Length).Max();
                foreach (var vec in vectors)
                {
                    if (vec != "0" && vec != "_estack" && vec != "Reset_Handler" && vec != "BootRAM")
                        sw.WriteLine("void {0}() {1}__attribute__ ((weak, alias (\"Default_Handler\")));", vec, new string(' ', maxLen - vec.Length));
                }
                sw.WriteLine("");

                sw.WriteLine("void * g_pfnVectors[0x{0:x}] __attribute__ ((section (\".isr_vector\"))) = ", vectors.Count);
                sw.WriteLine("{");
                foreach (var vec in vectors)
                    if (vec == "BootRAM")
                        sw.WriteLine("\t{0}", vec);
                    else if (vec == "0")
                        sw.WriteLine("\tNULL,");
                    else
                        sw.WriteLine("\t&{0},", vec);

                sw.WriteLine("};");

                while (line < allLines.Length)
                    sw.WriteLine(allLines[line++]);
            }
        }
    }
}
