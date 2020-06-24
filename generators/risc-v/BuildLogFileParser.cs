using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace risc_v
{
    class BuildLogFileParser
    {
        public class ParsedBuildLog
        {
            public HashSet<string> allIncludes = new HashSet<string>();
            public HashSet<string> allFlags = new HashSet<string>();
            public Dictionary<string, string> allDefines = new Dictionary<string, string>();
            public HashSet<string> AllSources = new HashSet<string>();
            public HashSet<string> AllLDFlags = new HashSet<string>();
            public HashSet<string> AllLibs = new HashSet<string>();
            public string LinkerScript;
        }

        public static ParsedBuildLog ParseRISCVBuildLog(string buildLogFile)
        {
            var rgBuildLine = new Regex("^riscv64-unknown-elf-gcc .*");
            ParsedBuildLog result = new ParsedBuildLog();

            foreach (var line in File.ReadAllLines(buildLogFile))
            {
                if (rgBuildLine.IsMatch(line))
                {
                    var args = CommandLineTools.CommandLineToArgs(line);
                    Dictionary<string, string> defines = new Dictionary<string, string>();
                    List<string> includes = new List<string>();
                    List<string> otherFlags = new List<string>();
                    string source = null;

                    int skipNow = 0;

                    bool isLinker = args.FirstOrDefault(a => a.StartsWith("-Wl,")) != null;

                    foreach (var arg in args.Skip(1))
                    {
                        if (skipNow-- > 0)
                            continue;

                        if (arg.StartsWith("-D"))
                        {
                            string key = arg.Substring(2), value = null;
                            int idx = key.IndexOf('=');
                            if (idx != -1)
                            {
                                value = key.Substring(idx + 1);
                                key = key.Substring(0, idx);
                            }

                            defines[key] = value;
                        }
                        else if (arg.StartsWith("-I"))
                            includes.Add(arg.Substring(2));
                        else if (arg.StartsWith("-m") || arg.StartsWith("--specs"))
                        {
                            otherFlags.Add(arg);
                        }
                        else if (arg.StartsWith("-f") && arg.EndsWith("-sections"))
                            continue;
                        else if (arg.StartsWith("-O") || arg.StartsWith("-g") || arg == "-c")
                            continue;
                        else if (arg == "-MT" || arg == "-MF" || arg == "-o")
                        {
                            skipNow = 1;
                            continue;
                        }
                        else if (arg.StartsWith("-M"))
                            continue;
                        else if (arg.StartsWith("&&"))
                            break;
                        else if (arg.EndsWith(".c") || arg.EndsWith(".S"))
                        {
                            source = arg;
                        }
                        else if (isLinker)
                        {
                            if (arg == "-Wl,--gc-sections" || arg == "-Wl,--start-group" || arg == "-Wl,--end-group" || arg.StartsWith("-Wl,-Map") || arg.StartsWith("-L"))
                                continue;
                            else if (arg.StartsWith("-T"))
                                result.LinkerScript = arg.Substring(2);
                            else if (arg.StartsWith("-no"))
                                result.AllLDFlags.Add(arg);
                            else if (arg.StartsWith("-l"))
                                result.AllLibs.Add(arg.Substring(2));
                            else
                                throw new Exception("Unknown linker argument: " + arg);
                        }
                        else
                            throw new Exception("Unknown argument: " + arg);
                    }

                    if (source == null)
                        throw new Exception("Unknown source file for command line");

                    foreach (var kv in defines)
                        result.allDefines[kv.Key] = kv.Value;
                    foreach (var inc in includes)
                        result.allIncludes.Add(inc);
                    foreach (var flag in otherFlags)
                        result.allFlags.Add(flag);
                    result.AllSources.Add(source);
                }
            }

            return result;
        }
    }
}
