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
            public NordicSampleRelocator()
            {
                AutoDetectedFrameworks = new AutoDetectedFramework[0];
                AutoPathMappings = new PathMapping[]
                {
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/(components|config|external|integration|modules)/(.*)", "$$SYS:BSP_ROOT$$/nRF5x/{1}/{2}"),
                };
            }
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
                return new NordicSampleRelocator();
            }

            void LogLine(string strlog)
            {
                using (var file = new StreamWriter(Path.Combine(TestDirectory, "RawLog.txt"), true))
                {
                    file.WriteLine(strlog);
                }
                Console.WriteLine(strlog);
            }

            VendorSample ParseNativeBuildLog(string namelog, string SDKdir)
            {
                VendorSample vs = new VendorSample();
                List<string> lstFileC = new List<string>();
                List<string> lstFileInc = new List<string>();
                List<string> splitArgs = new List<string>();
                List<string> lstDef = new List<string>();
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
                lstDef.AddRange(splitArgs.Where(ar => ar.StartsWith("-D")).Select(a => a.Substring(2).Trim()));

                lstFileC.AddRange(splitArgs.Where(ar => (ar.EndsWith(".c") || ar.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase)) && !ar.Contains(@"components/toolchain/") &&
                !ar.Contains(@"gcc_startup")));

           //    if (ln.Contains("-std"))
                //    if (ln.Contains("c99"))
                        vs.CLanguageStandard = "c99";
                 //   else
               //         vs.CPPLanguageStandard = ln.Replace("-std=", "").Trim('\\', ' ', '\t');

             
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

                var fl = File.ReadAllText(Path.Combine(aCurDir, "Makefile"));
                if (!fl.Contains("SOFTDEVICE_PRESENT"))
                {
                    vs.Configuration.MCUConfiguration = new PropertyDictionary2
                    {
                        Entries = new PropertyDictionary2.KeyValue[] {
                                    new PropertyDictionary2.KeyValue {
                                        Key = "com.sysprogs.bspoptions.nrf5x.softdevice", Value = "nosoftdev" }
                                                                    }.ToArray()
                    };
                }
                else
                {
                    var n = lstFileC.FindIndex(fi => fi.Contains("/softdevice_handler.c"));
                    if (n >= 0)
                        lstFileC.RemoveAt(n);

                }

                if (Directory.GetFiles(aCurDir, "*.ld").Count() > 0)
                    vs.LinkerScript = Directory.GetFiles(aCurDir, "*.ld")[0].Replace(SDKdir, "$$SYS:BSP_ROOT$$/nRF5x");

                vs.IncludeDirectories = lstFileInc.ToArray();
                vs.PreprocessorMacros = lstDef.ToArray();
                vs.SourceFiles = lstFileC.ToArray();
                vs.DeviceID = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("TARGETS")).Split('=')[1].Trim(' ').ToUpper();
                vs.UserFriendlyName = aProjectName;
                return vs;
            }

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                ApplyKnownPatches(SDKdir);
                string makeExecutable = ToolchainDirectory + "/bin/make";

                string[] ExampleDirs = Directory.GetFiles(Path.Combine(SDKdir, "examples"), "Makefile", SearchOption.AllDirectories).ToArray();

                using (var sw = File.CreateText(Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windows")))
                {
                    sw.WriteLine($"GNU_INSTALL_ROOT := {ToolchainDirectory.Replace('\\', '/')}/bin/");
                    sw.WriteLine($"GNU_VERSION := 7.2.0");
                    sw.WriteLine($"GNU_PREFIX := arm-eabi");
                }

                List<VendorSample> allSamples = new List<VendorSample>();

                int samplesDone = 0;

                string outputDir = Path.Combine(TestDirectory, "_MakeBuildLogs");
                List<UnparseableVendorSample> failedSamples = new List<UnparseableVendorSample>();
                int cnt = 0;
                int nobin = 0;
                int nolog = 0;

                foreach (var makefile in ExampleDirs)
                {
                    string nameExampl = makefile.Substring(makefile.IndexOf("examples") + 9).Replace("armgcc\\Makefile", "");
                    if (makefile.Contains(@"\ant\"))
                    {
                        LogLine($"{samplesDone}/{ExampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + (" Skipped"));
                        continue;
                    }
                    cnt++;

                    /* for debbug
                     * if (cnt < 80)
                       {
                           LogLine($"{cnt}/{ExampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + (" Skipped cnt"));
                           continue;
                       }
                       */
                    //if (samplesDone > 2)
                    //    break;
                    //if(!makefile.Contains(@"debug"))
                    //   continue;
                    //-----------------------------------------

                    //  if (Directory.Exists(Path.Combine(Path.GetDirectoryName(makefile), "_build")))
                    //      Directory.Delete(Path.Combine(Path.GetDirectoryName(makefile), "_build"), true);
                    var nameLog = Path.Combine(Path.GetDirectoryName(makefile), "log.txt");
                    // if (File.Exists(nameLog))
                    //   File.Delete(nameLog);

                    Console.WriteLine($"Compiling {nameExampl} ...");

                    var sampleID = new VendorSampleID
                    {
                        SampleName = File.ReadAllLines(makefile).Single(ln => ln.StartsWith("PROJECT_NAME")).Split('=')[1].Trim(' ').ToUpper(),
                        BoardNameOrDeviceID = File.ReadAllLines(makefile).Single(ln => ln.StartsWith("TARGETS")).Split('=')[1].Trim(' ').ToUpper()
                    };

                    if (!filter.ShouldParseSampleForSpecificDevice(sampleID))
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
                    if (Directory.Exists(buildDir) &&  Directory.GetFiles(buildDir,"*.bin",SearchOption.AllDirectories).Count() > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        LogLine($"{samplesDone}/{ExampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + ("Builded"));
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
                            failedSamples.Add(new UnparseableVendorSample { BuildLogFile = nameLog, ID = sampleID });
                            Console.ForegroundColor = ConsoleColor.Red;
                        }
                        LogLine($"{samplesDone}/{ExampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + (buildSucceeded ? "Succeeded" : "Failed "));
                        Console.ForegroundColor = ConsoleColor.Gray;

                        if (!buildSucceeded)
                            continue;
                    }


                    if (!File.Exists(nameLog))
                    {
                        LogLine($"No Log file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(makefile));
                        nolog++;
                        continue;
                    }
                    if (Directory.GetFiles(buildDir, "*.bin", SearchOption.AllDirectories).Count() == 0)
                    {
                        LogLine($"No bin file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No bin file {1}", Path.GetDirectoryName(makefile));
                        nobin++;
                        continue;
                    }

                    var vs = ParseNativeBuildLog(nameLog, SDKdir);
                    vs.Path = Path.GetDirectoryName(makefile);
                    while (Directory.GetFiles(vs.Path, "*.c").Length == 0)
                        vs.Path = Path.GetDirectoryName(vs.Path);

                    allSamples.Add(vs);
                    //Clear
                    //                File.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "log.txt"));
                    //                Directory.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "_build"), true);
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                LogLine($"Total samples : {samplesDone}");
                LogLine($"No Builded samples : {nobin}  No Log samples : {nolog}");
                LogLine($"Failed samples : {failedSamples.Count}, {(failedSamples.Count / samplesDone) * 100} % from Total");
                Console.ForegroundColor = ConsoleColor.Gray;

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
