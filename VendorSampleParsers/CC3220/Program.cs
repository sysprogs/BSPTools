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
        }

        class CC3220VendorSampleParser : VendorSampleParser
        {
            public CC3220VendorSampleParser()
                : base(@"..\..\generators\TI\CC3220\output", "CC3220 SDK Samples")
            {
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
            }        string ToAbsolutePath(string strDir,string SDKDir,Dictionary<string,string> constDir)
            {
                foreach (var cd in constDir)
                    if(strDir.Contains(cd.Key))
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
                    strDir  = strDir.Remove(0, 2);

                    if (strDir != "" && strDir.StartsWith("/"))
                        strDir  = strDir.Remove(0, 1);
                }

                return outAbsDir;
            }

            VendorSample ParseMakFile(string nameFile, string SDKdir)
            {
                VendorSample vs = new VendorSample();
                List<string> lstFileC = new List<string>();
                List<string> lstFileLib = new List<string>();
                List<string> lstFileInc = new List<string>();
                List<string> splitArgs = new List<string>();
                List<string> lstDef = new List<string>();
                List<string> lstDirLib = new List<string>();
                Dictionary<string,string> lstConstDir = new Dictionary<string, string>();
                string aCurDir = Path.GetDirectoryName(nameFile);
                Boolean flFlags = false;

                string dirLib = "";
                foreach (var ln in File.ReadAllLines(nameFile))
                {
                    if (ln.StartsWith("CFLAGS =") || ln.StartsWith("CPPFLAGS ="))
                        flFlags = true;

                    if (flFlags)
                    {
                        if (ln.Contains(" -I") || ln.Contains(" \"-I"))
                            lstFileInc.Add(GetUpDir(ToAbsolutePath(ln.Replace("CFLAGS =", "").Replace("\"", "").Replace("-I", "").Trim(' ','\\'),SDKdir, lstConstDir),aCurDir));

                        if (ln.Contains(" -D"))
                            lstDef.Add(ln.Replace("-D", "").Replace("\"", "").Trim(' ','\\'));
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
                        lstFileC.Add(Path.Combine(GetUpDir(file, aCurDir), file.Replace("../","")));
                    }

                    if (ln.Contains("\"-L"))
                    {
                        dirLib = ToAbsolutePath(ln.Replace("-L", "").Replace("\"", "").Replace("LFLAGS =", "").Trim(' ', '\\').Replace("$(SIMPLELINK_CC32XX_SDK_INSTALL_DIR)", "$$SYS:BSP_ROOT$$"), SDKdir,lstConstDir);
                        lstDirLib.Add("-L"+dirLib);
                    }

                    if (ln.Contains(" -l:"))
                        lstFileLib.Add(ln.Replace("\"", "").Trim(' ', '\\'));

                    Match m = Regex.Match(ln, @"([\w]+)[ \t]*:=[ \t]*([$(\w)/]*)");
                    if (m.Success)
                        lstConstDir.Add(m.Groups[1].Value, m.Groups[2].Value);
                }


                if (lstFileInc.Where(f => f.Contains("$")).Count() > 0)
                    throw new Exception("Path contains macros " + string.Join(", ", lstFileInc.Where(f => f.Contains("$"))));

               vs.IncludeDirectories = lstFileInc.ToArray();
                vs.PreprocessorMacros = lstDef.ToArray();
                vs.SourceFiles = lstFileC.ToArray();
                vs.DeviceID = "CC3220";
                vs.LDFLAGS = string.Join(" ", lstDirLib.Distinct().ToArray()) + " " + string.Join(" ", lstFileLib.Distinct().ToArray());
                if (File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Where(ln => ln.StartsWith("NAME")).Count() == 0)
                    vs.UserFriendlyName = "noname";
                else
                    vs.UserFriendlyName = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("NAME")).Split('=')[1].Trim(' ').ToUpper();
                return vs;
            }

            string FREERTOS_INSTALL_DIR;
            string GCC_ARMCOMPILER;
            string GCC_ARMCOMPILER_DIR = @"c:\toolchain1";
            const string XDCTOOLSCORE = @"c:\ti\xdctools_3_50_08_24_core";//c:\TI\xdctools_3_50_07_20_core";

            void UpdateImportMakefile(string SDKdir)
            {
                var impfile = Path.Combine(SDKdir, "imports.mak");

                var filestr = File.ReadAllLines(impfile);

                FREERTOS_INSTALL_DIR = Path.Combine(SDKdir, "FreeRTOSv10.1.1");

                for ( int c = 0; c< filestr.Count();c++)
                {
                    if (filestr[c].StartsWith("FREERTOS_INSTALL_DIR"))
                        filestr[c] = "FREERTOS_INSTALL_DIR   ?= "+ FREERTOS_INSTALL_DIR.Replace("\\","/");
                    if (filestr[c].StartsWith("GCC_ARMCOMPILER"))
                        filestr[c] = "GCC_ARMCOMPILER   ?= " + GCC_ARMCOMPILER_DIR.Replace("\\", "/");
                 }

                File.Delete(impfile);
                File.WriteAllLines(impfile,filestr);
            }
            private void  BuildFreeRtosKernel(string SDKDirfr)
            {
                string makeExecutableTI = Path.Combine(XDCTOOLSCORE,"gmake");

                LogLine($"Start build FreeRtos Kernel lib " + SDKDirfr);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {makeExecutableTI} -j{Environment.ProcessorCount} VERBOSE=1 > logfr.txt 2>&1",
                    UseShellExecute = false,
                    WorkingDirectory = SDKDirfr
                };

             
                var compiler = Process.Start(startInfo);
                compiler.WaitForExit();
                bool buildSucceeded;

                buildSucceeded = compiler.ExitCode == 0;

                Console.ForegroundColor = ConsoleColor.Green;

                if (Directory.GetFiles(SDKDirfr, "*.lib",SearchOption.AllDirectories).Count() == 0)
                    buildSucceeded = false;

                if (!buildSucceeded)
                  Console.ForegroundColor = ConsoleColor.Red;
                
                LogLine($"FreeRtos kernel lib: " + (buildSucceeded ? "Succeeded" : "Failed "));
                Console.ForegroundColor = ConsoleColor.Gray;
            }
            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
              
                string makeExecutable = ToolchainDirectory + "/bin/make";

                string[] ExampleDirs = Directory.GetFiles(Path.Combine(SDKdir, "examples"), "Makefile", SearchOption.AllDirectories).
                                                                    Where(s => (s.Contains("gcc") && !s.Contains("tirtos") 
                                                                    && !File.ReadAllText(s).Contains("$(NODE_JS)")
                                                                    )). ToArray();
               
                GCC_ARMCOMPILER = ((string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue("SysGCC-arm-eabi-7.2.0")).Replace("\\arm-eabi", "");
                if (GCC_ARMCOMPILER == null)
                    throw new Exception("Cannot locate toolchain path from registry");

                BuildFreeRtosKernel(Path.Combine(SDKdir, @"kernel\freertos\builds\CC3220S_LAUNCHXL\release\gcc"));

                BuildFreeRtosKernel(Path.Combine(SDKdir, @"kernel\freertos\builds\CC3220SF_LAUNCHXL\release\gcc"));

                UpdateImportMakefile(SDKdir);

                List<VendorSample> allSamples = new List<VendorSample>();

                int samplesDone = 0;

                string outputDir = Path.Combine(TestDirectory, "_MakeBuildLogs");
                List<UnparseableVendorSample> failedSamples = new List<UnparseableVendorSample>();
                int count = 0;

                foreach (var makefile in ExampleDirs)
                {
                    if (!makefile.Contains("gcc"))
                        continue;

                    //if (!makefile.Contains("capturepwmdisplay"))
                      //  continue;

                     //  if (count++ ==3)
                       //   break;

                    string nameExampl = makefile.Substring(makefile.IndexOf("examples") + 9).Replace("gcc\\Makefile", "");
                   
                    var nameLog = Path.Combine(Path.GetDirectoryName(makefile), "log.txt");
                    
                    Console.WriteLine($"Compiling {nameExampl} ...");

                    string namefl = "noname";
                    if (File.ReadAllLines(makefile).Where(ln => ln.StartsWith("NAME")).Count() == 0)
                        Console.WriteLine("NO NAME IN " + makefile);
                    else
                        namefl = File.ReadAllLines(makefile).Single(ln => ln.StartsWith("NAME")).Split('=')[1].Trim(' ').ToUpper();
                       

                    var sampleID = new VendorSampleID
                    {
                        SampleName = namefl
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

                    var compiler = Process.Start(startInfo);
                    samplesDone++;

                    compiler.WaitForExit();

                    bool buildSucceeded;

                    buildSucceeded = compiler.ExitCode == 0;

                    Console.ForegroundColor = ConsoleColor.Green;

                    if (Directory.GetFiles(Path.GetDirectoryName(makefile), "*.out").Count() == 0)
                        buildSucceeded = false;

                    if (!buildSucceeded)
                    {
                        failedSamples.Add(new UnparseableVendorSample { BuildLogFile = nameLog, ID = sampleID });
                        Console.ForegroundColor = ConsoleColor.Red;
                    }
                    LogLine($"{samplesDone}/{ExampleDirs.Length}: {nameExampl.TrimEnd('\\')}: " + (buildSucceeded ? "Succeeded" : "Failed "));
                    Console.ForegroundColor = ConsoleColor.Gray;

                    if (!File.Exists(nameLog))
                    {
                        LogLine($"No Log file  " + Path.GetDirectoryName(makefile));
                        Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(makefile));
                        continue;
                    }

                    var vs = ParseMakFile (makefile,SDKdir);
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
                LogLine($"Failed samples : {failedSamples.Count}, {(failedSamples.Count / samplesDone) * 100} % from Total");
                Console.ForegroundColor = ConsoleColor.Gray;

                return new ParsedVendorSamples { VendorSamples = allSamples.ToArray(), FailedSamples = failedSamples.ToArray() };
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
