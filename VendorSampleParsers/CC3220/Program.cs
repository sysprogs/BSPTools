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
using Microsoft.Win32;

namespace CC3220VendorSampleParser
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

        class CC3220SampleRelocator : VendorSampleRelocator
        {
            public CC3220SampleRelocator()
            {
                AutoDetectedFrameworks = new AutoDetectedFramework[0];
                AutoPathMappings = new PathMapping[]
                {
                };
            }

            protected override string BuildVirtualSamplePath(string originalPath)
            {
                string[] components = originalPath.Split('/');
                int trimAtEnd = 0;
                if (components.Last() == "freertos")
                    trimAtEnd++;

                components = components.Skip(4).Take(components.Length - 4 - trimAtEnd).ToArray();

                return string.Join("\\", components);
            }
        }

        class CC3220VendorSampleParser : VendorSampleParser
        {
            readonly string GCC_ARMCOMPILER_DIR;
            readonly string XDCTOOLSCORE;

            public CC3220VendorSampleParser()
                : base(@"..\..\generators\TI\CC3220\output", "CC3220 SDK Samples")
            {
                GCC_ARMCOMPILER_DIR = _SettingsKey.GetValue("GNUARMDirectory") as string ?? throw new Exception("Please specify the GNUARM directory via registry");
                XDCTOOLSCORE = _SettingsKey.GetValue("XDCToolsDirectory") as string ?? throw new Exception("Please specify the XDCTools directory via registry");
            }

            protected override string FilterSDKDir(string dir)
            {
                return dir + ".vendorsamples";
            }

            protected override void AdjustVendorSampleProperties(VendorSample vs)
            {
                base.AdjustVendorSampleProperties(vs);
                vs.BSPReferencesAreCopyable = true;
            }

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                return new CC3220SampleRelocator();
            }

            void LogLine(string strlog)
            {
                using (var file = new StreamWriter(Path.Combine(TestDirectory, "RawLog.txt"), true))
                {
                    file.WriteLine(strlog);
                }
                Console.WriteLine(strlog);
            }
            string ToAbsolutePath(string strDir, string SDKDir, Dictionary<string, string> constDir)
            {
                foreach (var cd in constDir)
                    if (strDir.Contains(cd.Key))
                        strDir = strDir.Replace($"$({cd.Key})", cd.Value);

                return strDir.Replace("$(GCC_ARMCOMPILER)", GCC_ARMCOMPILER).Replace("$(SIMPLELINK_CC32XX_SDK_INSTALL_DIR)", SDKDir).Replace("$(FREERTOS_INSTALL_DIR)", FREERTOS_INSTALL_DIR).
                    Replace("arm-none-eabi", "arm-eabi");
            }

            string GetUpDir(string strDir, string pAbsDir)
            {
                string outAbsDir = pAbsDir;
                if (!strDir.StartsWith(".."))
                    return strDir;

                while (strDir.Contains(".."))
                {
                    if (strDir.StartsWith(".."))
                        outAbsDir = outAbsDir.Remove(outAbsDir.LastIndexOf('\\'));
                    else
                        break;
                    strDir = strDir.Remove(0, 2);

                    if (strDir != "" && strDir.StartsWith("/"))
                        strDir = strDir.Remove(0, 1);
                }

                return outAbsDir;
            }

            FrameworkLocator _FrameworkLocator;

            VendorSample ParseMakFile(string nameFile, string relativePath, string SDKdir)
            {
                VendorSample vs = new VendorSample();
                List<string> lstFileC = new List<string>();
                List<string> lstFileInc = new List<string>();
                List<string> splitArgs = new List<string>();
                List<string> lstDef = new List<string>();
                Dictionary<string, string> lstConstDir = new Dictionary<string, string>();
                HashSet<string> referencedFrameworks = new HashSet<string>();
                string aCurDir = Path.GetDirectoryName(nameFile);
                Boolean flFlags = false;

                foreach (var ln in File.ReadAllLines(nameFile))
                {
                    if (ln.StartsWith("CFLAGS =") || ln.StartsWith("CPPFLAGS ="))
                        flFlags = true;

                    if (flFlags)
                    {
                        if (ln.Contains(" -I") || ln.Contains(" \"-I"))
                        {
                            if (ln.Contains("$(GCC_ARMCOMPILER)"))
                                continue;   //Toolchain-provided header directories should be handled automatically by toolchain itself.

                            lstFileInc.Add(GetUpDir(ToAbsolutePath(ln.Replace("CFLAGS =", "").Replace("\"", "").Replace("-I", "").Trim(' ', '\\'), SDKdir, lstConstDir), aCurDir));
                        }

                        if (ln.Contains(" -D"))
                            lstDef.Add(ln.Replace("-D", "").Replace("\"", "").Trim(' ', '\\'));
                    }


                    if (ln.StartsWith("LFLAGS =") || ln.StartsWith(".PRECIOUS:"))
                        flFlags = false;

                    if (ln.Contains("-std"))
                        if (ln.Contains("c99"))
                            vs.CLanguageStandard = "c99";
                        else
                            vs.CPPLanguageStandard = ln.Replace("-std=", "").Trim('\\', ' ', '\t');

                    if (ln.Contains(".obj:"))
                    {
                        string file = ln.Remove(0, ln.IndexOf(":") + 1).Split('$')[0].Trim(' ');
                        lstFileC.Add(Path.Combine(GetUpDir(file, aCurDir), file.Replace("../", "")));
                    }

                    if (ln.Contains("\"-L"))
                    {
                        //We ignore the library search paths
                    }

                    if (ln.Contains(" -l:"))
                        referencedFrameworks.Add(_FrameworkLocator.LocateFrameworkForLibraryFile(ln.Replace("\"", "").Trim(' ', '\\')));

                    Match m = Regex.Match(ln, @"([\w]+)[ \t]*:=[ \t]*([$(\w)/]*)");
                    if (m.Success)
                        lstConstDir.Add(m.Groups[1].Value, m.Groups[2].Value);
                }


                if (lstFileInc.Where(f => f.Contains("$")).Count() > 0)
                    throw new Exception("Path contains macros " + string.Join(", ", lstFileInc.Where(f => f.Contains("$"))));

                vs.IncludeDirectories = lstFileInc.ToArray();
                vs.PreprocessorMacros = lstDef.ToArray();
                vs.SourceFiles = lstFileC.ToArray();
                vs.Configuration = new VendorSampleConfiguration
                {
                    Frameworks = referencedFrameworks.Where(f => f != null).ToArray(),
                };

                if (vs.Configuration.Frameworks.Contains("com.sysprogs.arm.ti.cc3220.freertos"))
                {
                    AddConfigurationEntries(ref vs.Configuration.Configuration, "com.sysprogs.bspoptions.FreeRTOS_Heap_Implementation=Heap4 - contiguous heap area", "com.sysprogs.bspoptions.FreeRTOS_Port=Software FP");
                    AddConfigurationEntries(ref vs.Configuration.MCUConfiguration, "com.sysprogs.bspoptions.arm.floatmode=-mfloat-abi=soft");
                }

                if (vs.Configuration.Frameworks.Contains("com.sysprogs.arm.ti.cc3220.mqtt"))
                {
                    AddConfigurationEntries(ref vs.Configuration.Configuration, "com.sysprogs.bspoptions.MQTT_Client=1", "com.sysprogs.bspoptions.MQTT_Server=1");
                }

                const string BoardSuffix = "_LAUNCHXL";
                vs.DeviceID = nameFile.Split('\\').Last(component => component.EndsWith(BoardSuffix, StringComparison.InvariantCultureIgnoreCase));
                vs.DeviceID = vs.DeviceID.Substring(0, vs.DeviceID.Length - BoardSuffix.Length);

                if (File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Where(ln => ln.StartsWith("NAME")).Count() == 0)
                    vs.UserFriendlyName = "noname";
                else
                    vs.UserFriendlyName = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("NAME")).Split('=')[1].Trim(' ').ToUpper();


                var relativePathComponents = relativePath.Split('\\');
                vs.UserFriendlyName += "-" + relativePathComponents[0];

                return vs;
            }

            private void AddConfigurationEntries(ref PropertyDictionary2 configuration, params string[] entries)
            {
                PropertyDictionary2.KeyValue[] objs = entries.Select(e =>
                {
                    int idx = e.IndexOf('=');
                    return new PropertyDictionary2.KeyValue { Key = e.Substring(0, idx), Value = e.Substring(idx + 1) };
                }).ToArray();

                if (configuration == null)
                    configuration = new PropertyDictionary2 { Entries = objs };
                else
                    configuration.Entries = configuration.Entries.Concat(objs).ToArray();
            }

            string FREERTOS_INSTALL_DIR;
            string GCC_ARMCOMPILER;

            void UpdateImportMakefile(string SDKdir)
            {
                var impfile = Path.Combine(SDKdir, "imports.mak");

                var filestr = File.ReadAllLines(impfile);

                FREERTOS_INSTALL_DIR = Path.Combine(SDKdir, "FreeRTOSv10.2.1_191129");

                for (int c = 0; c < filestr.Count(); c++)
                {
                    if (filestr[c].StartsWith("FREERTOS_INSTALL_DIR"))
                        filestr[c] = "FREERTOS_INSTALL_DIR   ?= " + FREERTOS_INSTALL_DIR.Replace("\\", "/");
                    if (filestr[c].StartsWith("GCC_ARMCOMPILER"))
                        filestr[c] = "GCC_ARMCOMPILER   ?= " + GCC_ARMCOMPILER_DIR.Replace("\\", "/");
                }

                File.Delete(impfile);
                File.WriteAllLines(impfile, filestr);
            }

            private void BuildFreeRtosKernel(string SDKDirfr)
            {
                string makeExecutableTI = Path.Combine(XDCTOOLSCORE, "gmake");

                LogLine($"Start build FreeRtos Kernel lib " + SDKDirfr);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {makeExecutableTI} clean",
                    UseShellExecute = false,
                    WorkingDirectory = SDKDirfr
                };

                var cleanAction = Process.Start(startInfo);
                cleanAction.WaitForExit();
                if (cleanAction.ExitCode != 0)
                    throw new Exception("Failed to clean" + SDKDirfr);
                startInfo.Arguments = $"/c {makeExecutableTI} -j{Environment.ProcessorCount} VERBOSE=1 > logfr.txt 2>&1";

                var compiler = Process.Start(startInfo);
                compiler.WaitForExit();
                bool buildSucceeded;

                buildSucceeded = compiler.ExitCode == 0;

                Console.ForegroundColor = ConsoleColor.Green;

                if (Directory.GetFiles(SDKDirfr, "*.lib", SearchOption.AllDirectories).Count() == 0)
                    buildSucceeded = false;

                if (!buildSucceeded)
                    Console.ForegroundColor = ConsoleColor.Red;

                LogLine($"FreeRtos kernel lib: " + (buildSucceeded ? "Succeeded" : "Failed "));
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                _FrameworkLocator = new FrameworkLocator(SDKdir, Path.GetFullPath(BSPDirectory + @"\..\rules"));

                string makeExecutable = ToolchainDirectory + "/bin/make";

                string baseExampleDir = Path.Combine(SDKdir, "examples");
                string[] exampleDirs = Directory.GetFiles(baseExampleDir, "Makefile", SearchOption.AllDirectories).
                                                                    Where(s => (s.Contains("gcc") && !s.Contains("tirtos")
                                                                    && !File.ReadAllText(s).Contains("$(NODE_JS)")
                                                                    )).ToArray();

                GCC_ARMCOMPILER = ToolchainDirectory;
                if (GCC_ARMCOMPILER == null)
                    throw new Exception("Cannot locate toolchain path from registry");

                UpdateImportMakefile(SDKdir);

                foreach(var dir in Directory.GetDirectories(Path.Combine(SDKdir, @"kernel\freertos\builds")))
                    BuildFreeRtosKernel(Path.Combine(dir, @"release\gcc"));

                List<VendorSample> allSamples = new List<VendorSample>();

                int samplesDone = 0;

                string outputDir = Path.Combine(TestDirectory, "_MakeBuildLogs");
                List<UnparseableVendorSample> failedSamples = new List<UnparseableVendorSample>();

                foreach (var makefile in exampleDirs)
                {
                    if (!makefile.Contains("gcc"))
                        continue;

                    var sampleDir = Path.GetDirectoryName(makefile);
                    string nameExampl = makefile.Substring(makefile.IndexOf("examples") + 9).Replace("gcc\\Makefile", "");

                    var nameLog = Path.Combine(sampleDir, "log.txt");
                    var markerFile = Path.Combine(sampleDir, "done.txt");

                    Console.WriteLine($"Compiling {nameExampl} ...");

                    var dir = Path.GetDirectoryName(sampleDir);
                    var id = dir.Substring(baseExampleDir.Length + 1).Replace('\\', '-');

                    if (!filter.ShouldParseAnySamplesInsideDirectory(sampleDir))
                        continue;

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {makeExecutable} clean",
                        UseShellExecute = false,
                        WorkingDirectory = sampleDir
                    };

                    bool buildSucceeded;

                    if (!File.Exists(markerFile))
                    {
                        var cleanAction = Process.Start(startInfo);
                        cleanAction.WaitForExit();
                        if (cleanAction.ExitCode != 0)
                            throw new Exception("Failed to clean" + makefile);

                        startInfo.Arguments = $"/c {makeExecutable} -j{Environment.ProcessorCount} VERBOSE=1 > log.txt 2>&1";

                        var compiler = Process.Start(startInfo);

                        compiler.WaitForExit();

                        buildSucceeded = compiler.ExitCode == 0;
                    }
                    else
                        buildSucceeded = true;

                    samplesDone++;

                    CopyDriverFiles(SDKdir, sampleDir);

                    Console.ForegroundColor = ConsoleColor.Green;

                    if (Directory.GetFiles(sampleDir, "*.out").Count() == 0)
                        buildSucceeded = false;

                    if (buildSucceeded)
                        File.WriteAllText(markerFile, "build succeeded");

                    if (!buildSucceeded)
                    {
                        failedSamples.Add(new UnparseableVendorSample { BuildLogFile = nameLog, UniqueID = id });
                        Console.ForegroundColor = ConsoleColor.Red;
                    }
                    LogLine($"{samplesDone}/{exampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + (buildSucceeded ? "Succeeded" : "Failed "));
                    Console.ForegroundColor = ConsoleColor.Gray;

                    if (!File.Exists(nameLog))
                    {
                        LogLine($"No Log file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(makefile));
                        continue;
                    }

                    string relativePath = makefile.Substring(baseExampleDir.Length + 1);

                    var vs = ParseMakFile(makefile, relativePath, SDKdir);
                    vs.Path = Path.GetDirectoryName(makefile);
                    while (Directory.GetFiles(vs.Path, "*.c").Length == 0)
                        vs.Path = Path.GetDirectoryName(vs.Path);

                    vs.InternalUniqueID = id;

                    allSamples.Add(vs);
                    //Clear
                    //                File.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "log.txt"));
                    //                Directory.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "_build"), true);
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                LogLine($"Total samples : {samplesDone}");
                LogLine($"Failed samples : {failedSamples.Count}, {(failedSamples.Count / samplesDone) * 100} % from Total");
                Console.ForegroundColor = ConsoleColor.Gray;

                _FrameworkLocator.ThrowIfUnresolvedLibrariesFound();
                return new ParsedVendorSamples { VendorSamples = allSamples.ToArray(), FailedSamples = failedSamples.ToArray() };
            }

            //This copes the files generated by TI SysConfig (but not other large binary files) back into the original SDK
            private void CopyDriverFiles(string SDKDir, string sampleDir)
            {
                var originalSDKDir = Path.ChangeExtension(SDKDir, "").TrimEnd('.');
                foreach(var fn in Directory.GetFiles(sampleDir))
                {
                    var ext = Path.GetExtension(fn).ToLower();
                    if (ext == ".c" || ext == ".h")
                    {
                        var pathInsideSDKDir = fn.Substring(SDKDir.Length + 1);
                        File.Copy(fn, Path.Combine(originalSDKDir, pathInsideSDKDir), true);
                    }
                }
            }
        }

        static void Main(string[] args) => new CC3220VendorSampleParser().Run(args);

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
