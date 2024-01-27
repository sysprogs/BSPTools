/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPGenerationTools
{
    public class StartupFileGenerator
    {
        public class InterruptVector
        {
            public string Name;
            public string OptionalComment;

            public string SpecialVectorValue;

            public override string ToString()
            {
                if (SpecialVectorValue != null)
                    return string.Format("{0} ({1})", Name, SpecialVectorValue);
                else
                    return Name;
            }
        }

        public delegate InterruptVector VectorLineHook(string[] lines, ref int lineIndex);

        public static InterruptVector[] ParseInterruptVectors(string file,
                                                              string tableStart,
                                                              string tableEnd,
                                                              string vectorLineA,
                                                              string vectorLineB,
                                                              string ignoredLine,
                                                              string macroDef,
                                                              int nameGroup,
                                                              int commentGroup,
                                                              VectorLineHook hook = null,
                                                              bool useLastStartSymbol = false)
        {
            var rgTableStart = new Regex(tableStart);
            var rgTableEnd = new Regex(tableEnd);
            var rgVectorLineA = new Regex(vectorLineA);
            var rgVectorLineB = vectorLineB == null ? null : new Regex(vectorLineB);
            var rgIgnoredLine = new Regex(ignoredLine);
            var rgMacroDef = (macroDef == null) ? null : new Regex(macroDef);

            Dictionary<string, string> macroValues = new Dictionary<string, string>();

            bool insideTable = false;
            List<InterruptVector> result = new List<InterruptVector>();
            var lines = File.ReadAllLines(file);
            for (int i = 0; i <lines.Length; i++)
            {
                string line = lines[i];

                if (!insideTable)
                {
                    if (rgMacroDef != null)
                    {
                        var m = rgMacroDef.Match(line);
                        if (m.Success)
                            macroValues[m.Groups[1].Value] = m.Groups[2].Value;
                    }

                    if (rgTableStart.IsMatch(line))
                    {
                        insideTable = true;
                        continue;
                    }
                }
                else
                {
                    if (rgTableStart.IsMatch(line))
                    {
                        result.Clear();
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line) || rgIgnoredLine.IsMatch(line))
                        continue;
                    else if (rgTableEnd.IsMatch(line))
                        break;

                    if (hook != null)
                    {
                        var overrideVec = hook(lines, ref i);
                        if (overrideVec != null)
                        {
                            result.Add(overrideVec);
                            continue;
                        }
                    }

                    var m = rgVectorLineA.Match(line);
                    if (!m.Success && vectorLineB != null)
                        m = rgVectorLineB.Match(line);

                    if (!m.Success)
                        throw new Exception("Cannot parse interrupt vector table definition line: " + line);

                    InterruptVector vec = new InterruptVector { Name = m.Groups[nameGroup].Value };
                    if (m.Groups.Count > commentGroup)
                        vec.OptionalComment = m.Groups[commentGroup].Value.Trim();

                    string val;
                    if (vec.Name == "0")
                        vec = null;
                    else if (macroValues.TryGetValue(vec.Name, out val))
                        vec.SpecialVectorValue = val;

                    result.Add(vec);
                }
            }

            return result.ToArray();
        }

        public class InterruptVectorTable
        {
            public Predicate<MCUBuilder> MatchPredicate;
            public bool IsFallbackFile;
            public string FileName;
            public InterruptVector[] Vectors;

            public string[] AdditionalResetHandlerLines;

            public override string ToString()
            {
                return FileName + ": " + Vectors.Length + " vectors";
            }

            static List<string> AlignSpaceOffset(List<string> input)
            {
                int maxOff = input.Max(i => i.IndexOf("$$ALIGN_SPACE_OFFSET$$"));
                return input.Select(i => i.Replace("$$ALIGN_SPACE_OFFSET$$", new string(' ', maxOff - i.IndexOf("$$ALIGN_SPACE_OFFSET$$")))).ToList();
            }

            internal void Save(string fn,string pFileNameTemplate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fn));
                using (var sw = File.CreateText(fn))
                {
                    if (Vectors[0].Name != "_estack" || Vectors[1].Name != "Reset_Handler")
                        throw new Exception("Unexpected vector table layout");

                    var templateLines = File.ReadAllLines(pFileNameTemplate);
                    for (int l = 0; l < templateLines.Length; l++ )
                    {
                        var line = templateLines[l];
                        if (line.Contains("$$EXTRA_RESET_HANDLER_CODE$$"))
                        {
                            if (AdditionalResetHandlerLines != null)
                                foreach (var l2 in AdditionalResetHandlerLines)
                                    sw.WriteLine(line.Replace("$$EXTRA_RESET_HANDLER_CODE$$", l2));
                        }
                        else if (line.Contains("$$VECTOR$$"))
                        {
                            List<string> lines = new List<string>();
                            int extraLines = 0;
                            int idx = line.IndexOf("$@+");
                            if (idx != -1)
                            {
                                extraLines = int.Parse(line.Substring(idx + 3));
                                line = line.Substring(0, idx);
                            }

                            for (int i = 2; i < Vectors.Length; i++)
                            {
                                if (Vectors[i] != null && Vectors[i].SpecialVectorValue == null)
                                {
                                    lines.Add(line.Replace("$$VECTOR$$", Vectors[i].Name));
                                    for (int j = 1; j <= extraLines; j++)
                                        lines.Add(templateLines[l + j].Replace("$$VECTOR$$", Vectors[i].Name));
                                }
                            }

                            if (line.Contains("$$ALIGN_SPACE_OFFSET$$"))
                                lines = AlignSpaceOffset(lines);

                            foreach (var l2 in lines)
                                sw.WriteLine(l2);

                            l += extraLines;
                        }
                        else if (line.Contains("$$VECTOR_TABLE_SIZE$$"))
                        {
                            sw.WriteLine(line.Replace("$$VECTOR_TABLE_SIZE$$", string.Format("0x{0:x}", Vectors.Length)));
                        }
                        else if (line.Contains("$$VECTOR_POINTER$$"))
                        {
                            for (int i = 2; i < Vectors.Length; i++)
                            {
                                if (Vectors[i] == null)
                                    sw.WriteLine(line.Replace("$$VECTOR_POINTER$$", "NULL"));
                                else if (Vectors[i].SpecialVectorValue == null)
                                    sw.WriteLine(line.Replace("$$VECTOR_POINTER$$", "&" + Vectors[i].Name));
                                else
                                    sw.WriteLine(line.Replace("$$VECTOR_POINTER$$", "(void *)" + Vectors[i].SpecialVectorValue + " /* " + Vectors[i].Name + " */"));
                            }
                        }
                        else
                            sw.WriteLine(line);
                    }
                }
            }
        }
    }
}
