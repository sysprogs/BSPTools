using System;
using BSPEngine;
//using BSPGenerationTools;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BSPGenerationTools;
using VendorSampleParserEngine;
using System.Text.RegularExpressions;

namespace NordicVendorSampleParser
{
    class Program
    {
        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        //-----------------------------------------------
        static int CountDirUp(ref string pRelativeDir)
        {
            int aCountUp = 0;
            while (pRelativeDir.StartsWith(".."))
            {
                pRelativeDir = pRelativeDir.Remove(0, 2);
                aCountUp++;
                if (pRelativeDir.StartsWith("/"))
                    pRelativeDir = pRelativeDir.Remove(0, 1);
            }
            return aCountUp;
        }
        //-----------------------------------------------
        static string BuildAbsolutePath(string pDir, string s)
        {
            var RelativePath = s;
            if (Path.IsPathRooted(RelativePath))
                return RelativePath;

            var aCountUp = CountDirUp(ref RelativePath);
            return (string.Join("/", pDir.Split('\\').Reverse().Skip(aCountUp).Reverse()) + "/" + RelativePath);
        }
        //-----------------------------------------------
        static void BuildAbsolutePath(string pStartDir, ref List<string> pLstDir)
        {
            for (int c = 0; c < pLstDir.Count(); c++)
            {
                pLstDir[c] = BuildAbsolutePath(pStartDir, pLstDir[c]);
            }
        }
        //-----------------------------------------------
        static void ApplyKnownPatches(string SDKdir)
        {
            string fn = Path.Combine(SDKdir, @"examples\peripheral\twi_master_with_twis_slave\config.h");

            if (!File.Exists(fn))
                throw new Exception($"No exists file {fn}");

            InsertLinesIntoFile(fn,
                new string[] { "#undef BIG_ENDIAN", "#undef LITTLE_ENDIAN" },
                "#define EEPROM_SIM_ADDRESS_LEN_BYTES    2");


            fn = Path.Combine(SDKdir, @"examples\peripheral\ram_retention\main.c");
            if (!File.Exists(fn))
                throw new Exception($"No exists file {fn}");

            InsertLinesIntoFile(fn,
                new string[] { "#undef NRF52832_XXAA" },
                "#include \"nrf_gpio.h\"");
        }

        class NordicSampleRelocator : VendorSampleRelocator
        {
            public NordicSampleRelocator(ReverseConditionTable optionalConditionTableForFrameworkMapping)
                : base(optionalConditionTableForFrameworkMapping)
            {
                AutoDetectedFrameworks = new AutoDetectedFramework[0];
                AutoPathMappings = new PathMapping[]
                {
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/(components|config|external|integration|modules)/(.*)", "$$SYS:BSP_ROOT$$/nRF5x/{1}/{2}"),
                };
            }

            class PathMapperImpl : PathMapper
            {
                public PathMapperImpl(ConstructedVendorSampleDirectory dir)
                    : base(dir)
                {
                }

                public override string MapPath(string path)
                {
                    var result = base.MapPath(path);
                    if (result?.EndsWith(".ld") == true)
                    {
                        //Some linker script paths are too long and will likely trigger the MAX_PATH limit. Try to shorten them in a meaningful way.
                        string[] components = result.Split('/');
                        string board = components[components.Length - 4];
                        string softdev = components[components.Length - 3];
                        Array.Resize(ref components, components.Length - 4);
                        result = string.Join("/", components) + $"/{board}_{softdev}.ld";
                    }

                    return result;
                }
            }

            protected override PathMapper CreatePathMapper(ConstructedVendorSampleDirectory dir) => new PathMapperImpl(dir);
        }

        class NordicVendorSampleParser : VendorSampleParser
        {
            public NordicVendorSampleParser()
                : base(@"..\..\generators\nrf5x\output", "Nordic SDK Samples")
            {
            }

            Regex rgSystemFile = new Regex(".*/system_nrf5.*\\.c$");

            protected override void AdjustVendorSampleProperties(VendorSample vs)
            {
                base.AdjustVendorSampleProperties(vs);
                vs.BSPReferencesAreCopyable = true;
                vs.SourceFiles = vs.SourceFiles.Where(s => !rgSystemFile.IsMatch(s)).ToArray();
            }

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                ReverseConditionTable table = null;

                if (false)
                {
                    //As of SDK 16.0, most vendor samples include very specific subsets of various frameworks, so
                    //converting them to properly reference embedded frameworks pulls in too many extra files.
                    //Also the LwIP framework defines excessive amount of include directories, exceeding the 32KB
                    //limit for .rsp files.
                    var conditionTableFile = Path.Combine(BSPDirectory, ReverseFileConditionBuilder.ReverseConditionListFileName + ".gz");
                    if (File.Exists(conditionTableFile))
                        table = XmlTools.LoadObject<ReverseConditionTable>(conditionTableFile);
                }

                return new NordicSampleRelocator(table);
            }

            void LogLine(string strlog)
            {
                using (var file = new StreamWriter(Path.Combine(TestDirectory, "RawLog.txt"), true))
                {
                    file.WriteLine(strlog);
                }
                Console.WriteLine(strlog);
            }

            VendorSample ParseNativeBuildLog(string namelog, string SDKdir, string sampleID)
            {
                VendorSample vs = new VendorSample { InternalUniqueID = sampleID };
                List<string> lstFileC = new List<string>();
                List<string> lstFileInc = new List<string>();
                List<string> splitArgs = new List<string>();
                List<string> preprocessorMacros = new List<string>();
                string aCurDir = Path.GetDirectoryName(namelog);
                foreach (var ln in File.ReadAllLines(namelog))
                {
                    if (ln.Replace('\\', '/').IndexOf(ToolchainDirectory.Replace('\\', '/'), StringComparison.InvariantCultureIgnoreCase) == -1)
                        continue;
                    // Get Arguments 
                    int munArg;
                    IntPtr ptrToSplitArgs = CommandLineToArgvW(ln, out munArg);
                    if (ptrToSplitArgs == IntPtr.Zero)
                        throw new Exception("no arg");

                    for (int i = 0; i < munArg; i++)
                    {
                        string arg = Marshal.PtrToStringUni(
                         Marshal.ReadIntPtr(ptrToSplitArgs, i * IntPtr.Size));
                        if (!splitArgs.Contains(arg))
                            splitArgs.Add(arg);
                    }
                }

                // Processing arguments
                lstFileInc.AddRange(splitArgs.Where(ar => ar.StartsWith("-I")).Select(a => a.Substring(2).Trim()));
                preprocessorMacros.AddRange(splitArgs.Where(ar => ar.StartsWith("-D")).Select(a => a.Substring(2).Trim()));

                lstFileC.AddRange(splitArgs.Where(ar => (ar.EndsWith(".c") || ar.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase)) && !ar.Contains(@"components/toolchain/") &&
                !ar.Contains(@"gcc_startup")));
                vs.CLanguageStandard = "c99";

                //arguments from file
                var fileArg = splitArgs.SingleOrDefault(ar => ar.StartsWith("@"));
                if (fileArg != null)
                {
                    var Libs = from t in File.ReadAllText(Path.Combine(aCurDir, fileArg.Substring(1))).Split(' ') where t.EndsWith(".a") orderby t select t;
                    lstFileC.AddRange(Libs);
                }

                BuildAbsolutePath(aCurDir, ref lstFileInc);
                BuildAbsolutePath(aCurDir, ref lstFileC);

                var aProjectName = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("PROJECT_NAME")).Split('=')[1].Trim(' ').ToUpper();

                vs.DeviceID = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("TARGETS")).Split('=')[1].Trim(' ').ToUpper();

                var softdeviceProperty = BSP.BSP.SupportedMCUs.First(m => vs.DeviceID.StartsWith(m.ID, StringComparison.InvariantCultureIgnoreCase)).ConfigurableProperties.PropertyGroups.SelectMany(g => g.Properties).FirstOrDefault(p => p.UniqueID == "com.sysprogs.bspoptions.nrf5x.softdevice") as PropertyEntry.Enumerated;
                if (softdeviceProperty == null)
                    throw new Exception("Failed to locate softdevice for property" + vs.DeviceID);

                string softdevice = softdeviceProperty.SuggestionList.FirstOrDefault(e => preprocessorMacros.Contains(e.InternalValue))?.InternalValue;
                if (!preprocessorMacros.Contains("SOFTDEVICE_PRESENT"))
                {
                    //This is a special 'serialization mode' sample that defines -DSxxx, but not -DSOFTDEVICE_PRESENT, that is not supported by our BSP yet.
                    softdevice = null;
                }
                else if (softdevice == null)
                    throw new Exception("Could not find a matching softdevice for " + sampleID);

                List<PropertyDictionary2.KeyValue> entries = new List<PropertyDictionary2.KeyValue>
                {
                    new PropertyDictionary2.KeyValue
                    {
                        Key = "com.sysprogs.bspoptions.nrf5x.softdevice",
                        Value = softdevice ?? "nosoftdev"
                    }
                };

                for (int i = 0; i < preprocessorMacros.Count; i++)
                {
                    int idx = preprocessorMacros[i].IndexOf("=");
                    if (idx == -1)
                        continue;

                    string name = preprocessorMacros[i].Substring(0, idx);
                    if (name == "__HEAP_SIZE")
                    {
                        entries.Add(new PropertyDictionary2.KeyValue { Key = "com.sysprogs.bspoptions.stackheap.heapsize", Value = preprocessorMacros[i].Substring(idx + 1) });
                        preprocessorMacros.RemoveAt(i--);
                    }
                    else if (name == "__STACK_SIZE")
                    {
                        entries.Add(new PropertyDictionary2.KeyValue { Key = "com.sysprogs.bspoptions.stackheap.stacksize", Value = preprocessorMacros[i].Substring(idx + 1) });
                        preprocessorMacros.RemoveAt(i--);
                    }
                }


                vs.Configuration.MCUConfiguration = new PropertyDictionary2
                {
                    Entries = entries.ToArray()
                };

                if (softdevice != null)
                {
                    var n = lstFileC.FindIndex(fi => fi.Contains("/softdevice_handler.c"));
                    if (n >= 0)
                        lstFileC.RemoveAt(n);
                }

                if (Directory.GetFiles(aCurDir, "*.ld").Count() > 0)
                    vs.LinkerScript = Directory.GetFiles(aCurDir, "*.ld")[0];

                vs.IncludeDirectories = lstFileInc.ToArray();
                vs.PreprocessorMacros = preprocessorMacros.ToArray();
                vs.SourceFiles = lstFileC.ToArray();
                vs.UserFriendlyName = aProjectName;
                vs.NoImplicitCopy = true;

                return vs;
            }

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                ApplyKnownPatches(SDKdir);
                string makeExecutable = ToolchainDirectory + "/bin/make";

                string[] allMakefiles = Directory.GetFiles(Path.Combine(SDKdir, "examples"), "Makefile", SearchOption.AllDirectories).ToArray();

                using (var sw = File.CreateText(Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windows")))
                {
                    sw.WriteLine($"GNU_INSTALL_ROOT := {ToolchainDirectory.Replace('\\', '/')}/bin/");
                    sw.WriteLine($"GNU_VERSION := 9.2.1");
                    sw.WriteLine($"GNU_PREFIX := arm-none-eabi");
                }

                List<VendorSample> allSamples = new List<VendorSample>();

                string outputDir = Path.Combine(TestDirectory, "_MakeBuildLogs");
                List<UnparseableVendorSample> failedSamples = new List<UnparseableVendorSample>();
                int samplesDone = 0, samplesWithoutBinaryFile = 0, samplesWithoutLogFile = 0;

                foreach (var makefile in allMakefiles)
                {
                    string nameExampl = makefile.Substring(makefile.IndexOf("examples") + 9).Replace("armgcc\\Makefile", "");
                    if (makefile.Contains(@"\ant\"))
                    {
                        LogLine($"{samplesDone}/{allMakefiles.Length}: {nameExampl.TrimEnd('\\')}: " + (" Skipped"));
                        continue;
                    }

                    var nameLog = Path.Combine(Path.GetDirectoryName(makefile), "log.txt");
                    Console.WriteLine($"Compiling {nameExampl} ...");

                    string sampleName = File.ReadAllLines(makefile).Single(ln => ln.StartsWith("PROJECT_NAME")).Split('=')[1].Trim(' ').ToUpper();
                    string boardNameOrDeviceID = File.ReadAllLines(makefile).Single(ln => ln.StartsWith("TARGETS")).Split('=')[1].Trim(' ').ToUpper();

                    string sampleID = $"{sampleName}-{boardNameOrDeviceID}";

                    string vendorSamplePath = Path.GetDirectoryName(makefile);
                    while (Directory.GetFiles(vendorSamplePath, "*.c").Length == 0)
                        vendorSamplePath = Path.GetDirectoryName(vendorSamplePath);

                    if (!filter.ShouldParseAnySamplesInsideDirectory(vendorSamplePath))
                        continue;

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {makeExecutable} -j{Environment.ProcessorCount} VERBOSE=1 > log.txt 2>&1",
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(makefile)
                    };

                    samplesDone++;

                    var buildDir = Path.Combine(startInfo.WorkingDirectory, "_build");
                    if (Directory.Exists(buildDir) && Directory.GetFiles(buildDir, "*.bin", SearchOption.AllDirectories).Count() > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        LogLine($"{samplesDone}/{allMakefiles.Length}: {nameExampl.TrimEnd('\\')}: " + ("Built"));
                        Console.ForegroundColor = ConsoleColor.Gray;
                    }
                    else
                    {
                        try
                        {
                            if (Directory.Exists(buildDir))
                                Directory.Delete(buildDir, true);
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine("Error delete: " + exc.Message);
                        }

                        var compiler = Process.Start(startInfo);

                        compiler.WaitForExit();

                        bool buildSucceeded;

                        buildSucceeded = compiler.ExitCode == 0;

                        Console.ForegroundColor = ConsoleColor.Green;
                        if (!buildSucceeded)
                        {
                            failedSamples.Add(new UnparseableVendorSample { BuildLogFile = nameLog, UniqueID = sampleID });
                            Console.ForegroundColor = ConsoleColor.Red;
                        }
                        LogLine($"{samplesDone}/{allMakefiles.Length}: {nameExampl.TrimEnd('\\')}: " + (buildSucceeded ? "Succeeded" : "Failed "));
                        Console.ForegroundColor = ConsoleColor.Gray;

                        if (!buildSucceeded)
                            continue;
                    }


                    if (!File.Exists(nameLog))
                    {
                        LogLine($"No Log file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(makefile));
                        samplesWithoutLogFile++;
                        continue;
                    }
                    if (Directory.GetFiles(buildDir, "*.bin", SearchOption.AllDirectories).Count() == 0)
                    {
                        LogLine($"No bin file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No bin file {1}", Path.GetDirectoryName(makefile));
                        samplesWithoutBinaryFile++;
                        continue;
                    }

                    var vs = ParseNativeBuildLog(nameLog, SDKdir, sampleID);
                    vs.Path = vendorSamplePath;


                    allSamples.Add(vs);
                }

                if (samplesDone > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    LogLine($"Total samples : {samplesDone}");
                    LogLine($"Samples without final binary file: {samplesWithoutBinaryFile}  Samples producing no log: {samplesWithoutLogFile}");
                    LogLine($"Failed samples : {failedSamples.Count}, {(failedSamples.Count / samplesDone) * 100} % from Total");
                    Console.ForegroundColor = ConsoleColor.Gray;
                }

                return new ParsedVendorSamples { VendorSamples = allSamples.ToArray(), FailedSamples = failedSamples.ToArray() };
            }
        }

        static void Main(string[] args) => new NordicVendorSampleParser().Run(args);

        //-----------------------------------------------
        static void InsertLinesIntoFile(string filename, string[] insertLines, string AfterLines)
        {
            Patch.InsertLines p = new Patch.InsertLines
            {
                InsertedLines = insertLines,
                AfterLine = AfterLines,
                FilePath = filename
            };

            List<string> allLines = File.ReadAllLines(filename).ToList();
            if (allLines.IndexOf(insertLines[0]) >= 0)
                return;

            p.Apply(allLines);
            File.WriteAllLines(filename, allLines);
        }
        //-----------------------------------------------

    }
}
