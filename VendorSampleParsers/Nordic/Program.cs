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

namespace NordicVendorSampleParser
{
    class Program
    {
        static string SDKdir;
        static int CntSamles = 0;
        static int FaildSamles = 0;
        static string outputDir = @"..\..\Output";
        static string toolchainDir;

        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,out int pNumArgs);

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
        static void ApplyKnownPatches()
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
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/(components|config|external)/(.*)", "$$SYS:BSP_ROOT$$/nRF5x/{1}/{2}"),
                };
            }
        }

        //-----------------------------------------------
        static void Main(string[] args)
        {
            const string bspDir = @"..\..\..\..\generators\nrf5x\output";
            string tempDir;

            if (args.Length < 2)
                throw new Exception("Usage: NordicVendorSampleParser <SDKDir> <TestDir>");

            SDKdir = args[0];
            tempDir = args[1];

            toolchainDir = File.ReadAllLines(SDKdir + @"\components\toolchain\gcc\Makefile.windows")[0].Split('=')[1].Trim(' ');

            ApplyKnownPatches();
            string sampleListFile = Path.Combine(outputDir, "samples.xml");
            var sampleDir = BuildOrLoadSampleDirectory(SDKdir, outputDir, sampleListFile);
            if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
            {
                //Perform Pass 1 testing - test the raw VendorSamples in-place
                StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, bspDir, tempDir);
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }

            foreach (var s in sampleDir.Samples)
                s.BSPReferencesAreCopyable = true;

            //Insert the samples into the generated BSP
            var relocator = new NordicSampleRelocator();
            relocator.InsertVendorSamplesIntoBSP(sampleDir, bspDir);

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(bspDir, "bsp.xml"));
            bsp.VendorSampleDirectoryPath = "VendorSamples";
            bsp.VendorSampleCatalogName = "Nordic SDK Samples";
            XmlTools.SaveObject(bsp, Path.Combine(bspDir, "bsp.xml"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            string statFile = Path.ChangeExtension(archiveName, ".xml");
            TarPacker.PackDirectoryToTGZ(bspDir, Path.Combine(bspDir, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp" && Path.GetFileName(fn) != statFile);

            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, "VendorSamples", "VendorSamples.xml"));
            expandedSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
            var result = StandaloneBSPValidator.Program.TestVendorSamples(expandedSamples, bspDir, tempDir);
            if (result.Failed > 0)
                throw new Exception("Some of the vendor samples failed to build. Check the build log.");
        }
        //-----------------------------------------------
        private static ConstructedVendorSampleDirectory BuildOrLoadSampleDirectory(string SDKdir, string outputDir, string sampleListFile)
        {
            ConstructedVendorSampleDirectory sampleDir;
            if (File.Exists(sampleListFile) || File.Exists(sampleListFile + ".gz"))
            {
                sampleDir = XmlTools.LoadObject<ConstructedVendorSampleDirectory>(sampleListFile);
                if (sampleDir.SourceDirectory == SDKdir)
                    return sampleDir;
            }

            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            var samples = ParseVendorSamples(SDKdir);

            Console.ForegroundColor = ConsoleColor.Yellow;
            ToLog($"Total exemples : {CntSamles}");
            ToLog($"Failed exemples : {FaildSamles}, {CntSamles / 100 * FaildSamles} % from Total");
            Console.ForegroundColor = ConsoleColor.Gray;

            sampleDir = new ConstructedVendorSampleDirectory
            {
                SourceDirectory = SDKdir,
                Samples = samples.ToArray(),
            };

            XmlTools.SaveObject(sampleDir, sampleListFile);
            return sampleDir;
        }
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
        static VendorSample ParseNativeBuildLog(string namelog)
        {
            VendorSample vs = new VendorSample();
            List<string> lstFileC = new List<string>();
            List<string> lstFileInc = new List<string>();
            List<string> splitArgs = new List<string>();
            List<string> lstDef = new List<string>();
            string aCurDir = Path.GetDirectoryName(namelog);
            foreach (var ln in File.ReadAllLines(namelog))
            {
                if (!ln.Contains(toolchainDir))
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
            lstFileInc.AddRange(string.Join(" ", splitArgs.Where(ar => ar.StartsWith("-I"))).Split(new string[] { "-I" }, StringSplitOptions.RemoveEmptyEntries));
            lstDef.AddRange(string.Join(" ", splitArgs.Where(ar => ar.StartsWith("-D"))).Split(new string[] { "-D" }, StringSplitOptions.RemoveEmptyEntries));

            lstFileC.AddRange(splitArgs.Where(ar => ar.EndsWith(".c") && !ar.Contains(@"components/toolchain/")));


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
            vs.IncludeDirectories = lstFileInc.ToArray();
            vs.PreprocessorMacros = lstDef.ToArray();
            vs.SourceFiles = lstFileC.ToArray();
            vs.DeviceID = File.ReadAllLines(Path.Combine(aCurDir, "Makefile")).Single(ln => ln.StartsWith("TARGETS")).Split('=')[1].Trim(' ').ToUpper();
            vs.UserFriendlyName = aProjectName;
            return vs;
        }
        //-----------------------------------------------
        static void ToLog(string strlog)
        {
            using (System.IO.StreamWriter file =
               new System.IO.StreamWriter(Path.Combine(outputDir, "log.txt"), true))
            {
                file.WriteLine(strlog);
            }
            Console.WriteLine(strlog);
        }
        //-----------------------------------------------
        static List<VendorSample> ParseVendorSamples(string SDKdir)
        {
            string[] ExampleDirs = Directory.GetFiles(Path.Combine(SDKdir, "examples"), "Makefile", SearchOption.AllDirectories).ToArray();
            if (!File.Exists(Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windowsbak")))
            {
                File.Move(Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windows"), Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windowsbak"));
                File.Copy(@"..\..\Makefile.windows", Path.Combine(SDKdir, @"components\toolchain\gcc\Makefile.windows"));
            }
            List<VendorSample> allSamples = new List<VendorSample>();

            foreach (var makefile in ExampleDirs)
            {
                if (makefile.Contains(@"\ant\"))
                    continue;
                string nameExampl = makefile.Substring(makefile.IndexOf("examples") + 9).Replace("armgcc\\Makefile", "");

                if (Directory.Exists(Path.Combine(Path.GetDirectoryName(makefile), "_build")))
                    Directory.Delete(Path.Combine(Path.GetDirectoryName(makefile), "_build"), true);

                var nameLog = Path.Combine(Path.GetDirectoryName(makefile), "log.txt");
                if (File.Exists(nameLog))
                    File.Delete(nameLog);

                Console.WriteLine($"Compiling {nameExampl} ...");

                Process compiler = new Process();
                compiler.StartInfo.FileName = "cmd.exe";
                compiler.StartInfo.Arguments = "/c make VERBOSE=1 > log.txt 2>&1";
                compiler.StartInfo.UseShellExecute = false;
                compiler.StartInfo.WorkingDirectory = Path.GetDirectoryName(makefile);
                compiler.Start();
                compiler.WaitForExit();
                CntSamles++;
                bool buildSucceeded;

                buildSucceeded = compiler.ExitCode == 0;

                Console.ForegroundColor = ConsoleColor.Green;
                if (!buildSucceeded)
                {
                    FaildSamles++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    File.Copy(nameLog, Path.Combine(outputDir, $"FaildLogs{nameExampl.Replace('\\', '-')}"));
                }
                ToLog(string.Format("{2}: {0} - {1} (Failed: {3})", nameExampl, buildSucceeded ? "Succes" : "Failed ", CntSamles, FaildSamles));
                Console.ForegroundColor = ConsoleColor.Gray;

                if (!buildSucceeded)
                    continue;


                if (!File.Exists(nameLog))
                {
                    Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(makefile));
                    continue;
                }
                var vs = ParseNativeBuildLog(nameLog);
                vs.Path = Path.GetDirectoryName(makefile);
                while (Directory.GetFiles(vs.Path, "*.c").Length == 0)
                    vs.Path = Path.GetDirectoryName(vs.Path);

                allSamples.Add(vs);
                //Clear
                File.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "log.txt"));
                Directory.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "_build"), true);
            }
            return allSamples;
        }
    }
}
