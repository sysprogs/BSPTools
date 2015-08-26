using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace msp432_bsp_generator
{
    internal class StartupFilesParser
    {
        private static readonly Regex VECTOR_TABLE_START = new Regex(@"[ \t]*__Vectors[ \t]+DCD[ \t]+([\w]+)[ \t]+;([ \w]+)*", RegexOptions.Singleline);
        private static readonly Regex VECTOR_TABLE_ENTRY = new Regex(@"[ \t]*DCD[ \t]+([\w]+)[ \t]+;([ \w]+)*", RegexOptions.Singleline);
        private static readonly Regex VECTOR_TABLE_END = new Regex(@"[ \t]*__Vectors_End", RegexOptions.Singleline);
        private static readonly Regex IGNORE_LINE = new Regex(@"(\n\r)|([ \t]*)", RegexOptions.Singleline);
        private static readonly Regex EXTERNAL_INTERRUPTS = new Regex(@"[ \t]*;[ \t]*([ \w]+)", RegexOptions.Singleline);
        private const string UNINTIALIZED_VECTOR_ENTRY = "0";

        public static StartupFileGenerator.InterruptVectorTable Parse(string mcuFamilyName, string searchDir, string startupFileName)
        {
            var startupFiles = new DirectoryInfo(searchDir).GetFiles("startup_" + mcuFamilyName + ".s", SearchOption.AllDirectories);
            var allVectorTables = new List<StartupFileGenerator.InterruptVector[]>();

            foreach (var startupFile in startupFiles)
            {
                if (!startupFile.FullName.ToUpperInvariant().Contains(mcuFamilyName.ToUpperInvariant()))
                {
                    continue;
                }

                allVectorTables.Add(ParseInterruptVectors(startupFile.FullName));
            }

            var firstVectorTable = new List<StartupFileGenerator.InterruptVector>(allVectorTables[0]);
            allVectorTables.RemoveAt(0);

            foreach (var vectorTable in allVectorTables)
            {
                if (vectorTable.Length != firstVectorTable.Count)
                {
                    throw new Exception("Interrupt vector counts different!");
                }

                for (int i = 0; i < firstVectorTable.Count; i++)
                {
                    if (vectorTable[i].OptionalComment != firstVectorTable[i].OptionalComment)
                    {
                        throw new Exception(string.Format("Comments are different for interrupt {0}", firstVectorTable[i]));
                    }
                }
            }
            
            //Fix the vector names from comments
            for (int i = 0; i < firstVectorTable.Count; i++)
            {
                if (i == 0)
                {                    
                    firstVectorTable[i].Name = "_estack";
                    continue;
                }                
            }

            return new StartupFileGenerator.InterruptVectorTable
            {
                FileName = Path.GetFileName(startupFileName),
                MatchPredicate = m => true,
                Vectors = firstVectorTable.ToArray()
            };
        }

        private static StartupFileGenerator.InterruptVector[] ParseInterruptVectors(string fullName)
        {
            var numOfReservedInterrupts = 0;
            var vectors = new List<StartupFileGenerator.InterruptVector>();
            var insideTable = false;
            Match match = null;
            var externalInterrupts = false;

            foreach (var line in File.ReadLines(fullName))
            {
                if (!insideTable)
                {
                    match = VECTOR_TABLE_START.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    vectors.Add(new StartupFileGenerator.InterruptVector
                    {
                        Name = match.Groups[1].Value,
                        OptionalComment = match.Groups[2].Value
                    });
                    insideTable = true;
                }  
                else
                {
                    match = VECTOR_TABLE_ENTRY.Match(line);
                    if (match.Success)
                    {
                        var vectorName = match.Groups[1].Value;
                        var comment = match.Groups[1].Value;

                        if (vectorName == UNINTIALIZED_VECTOR_ENTRY)
                        {
                            vectorName = string.Format(
                                "Reserved_{0}_{1}", 
                                numOfReservedInterrupts++, 
                                externalInterrupts ? "_IRQHandler" : "_Handler");
                        }

                        var vector = new StartupFileGenerator.InterruptVector
                        {
                            Name = vectorName,
                            OptionalComment = comment
                        };

                        vectors.Add(vector);
                        continue;
                    }

                    if (EXTERNAL_INTERRUPTS.Match(line).Success)
                    {
                        externalInterrupts = true;
                        continue;
                    }

                    if (VECTOR_TABLE_END.Match(line).Success)
                    {
                        break;
                    }

                    if (IGNORE_LINE.Match(line).Success)
                    {
                        continue;
                    }                    

                    throw new Exception("Failed to parse vector table");
                }              
            }

            return vectors.ToArray();
        }
    }
}
