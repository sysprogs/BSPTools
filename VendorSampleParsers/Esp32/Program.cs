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
using System.Reflection;
using System.Threading;
using System.Text.RegularExpressions;

namespace Esp32VendorSampleParser
{
    class Program
    {
        static string SDKdir;
        static string pDirEspIDf = "esp-idf";
        static int CntSamles = 0;
        static int FaildSamles = 0;
        static string outputDir = @"..\..\Output";
        static string toolchainDir;

        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);
        //-----------------------------------------------
        class Esp32SampleRelocator : VendorSampleRelocator
        {
            public Esp32SampleRelocator()
            {
                AutoDetectedFrameworks = new AutoDetectedFramework[0];
                AutoPathMappings = new PathMapping[]
                {
                };
            }
        }

        static void UpdateReferenceFramwork(ref List<VendorSample> listvs)
        {
            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(@"c:\SysGCC\esp32\esp32-bsp", "bsp.xml"));
            Dictionary<string, string> dicfr = new Dictionary<string, string>();

            foreach (var fr in bsp.Frameworks)
                dicfr.Add(fr.ID.Substring(fr.ID.LastIndexOf(".") + 1), fr.ID);

            foreach (var vs in listvs)
            {
                List<string> newSC = new List<string>(vs.SourceFiles);
                List<string> newIncF = new List<string>(vs.IncludeDirectories);
                List<string> frID = new List<string>();
                foreach (var frid in dicfr)
                    if (vs.SourceFiles.Where(f => f.Contains(frid.Key)).Count() > 0)
                    {
                        newSC = newSC.Where(f => !f.Contains(frid.Key)).ToList();
                        frID.Add(frid.Value);
                        newIncF = newIncF.Where(f => !f.Contains(frid.Key)).ToList();
                    }
                vs.SourceFiles = newSC.ToArray();
                vs.IncludeDirectories = newIncF.ToArray();
                vs.Configuration.Frameworks = frID.ToArray();
            }

        }

        //-----------------------------------------------
        //parsing KConfig
        // Test how mone macros in SDKConfig from KConfig
        static int TestParserKConfig(string pSDKFile, string pDir)
        {
            string pDirOut = $@"..\..\Output\Test\";
            int c = 0;
            //pSDKFile = $@"..\..\Output\sdkconfig.h";

            if (!Directory.Exists(pDirOut))
                Directory.CreateDirectory(pDirOut);
            Dictionary<string, string> dicMac = new Dictionary<string, string>();
            foreach (var ln in File.ReadAllLines(pSDKFile))
            {
                if (ln.Trim().StartsWith("*") || ln.Trim().StartsWith("/"))
                    continue;
                Match m = Regex.Match(ln, "^[ \t]*#define[ \t]+CONFIG_([A-Zaz_0-9]+)[ \t]+([\"0-9A-Za-z-_./]+)");
                if (!m.Success)
                {
                    Console.WriteLine(ln);
                    continue;
                }

                dicMac.Add(m.Groups[1].Value, m.Groups[2].Value);
            }

            List<string> allKconf = new List<string>();
            foreach (var fl in Directory.GetFiles(pDir, "KConfig*", SearchOption.AllDirectories))
            {
                File.Copy(fl, pDirOut + $@"KConfig{++c}", true);
                allKconf.AddRange(File.ReadAllLines(fl));
            }

            c = 0;
            List<string> nomac = new List<string>();
            foreach (var mac in dicMac)
                if (allKconf.Where(l => l.Contains(mac.Key)).Count() == 0)
                {
                    nomac.Add(mac.Key + " =>" + mac.Value);
                    c++;
                    Console.WriteLine($" {c} NO MACIN KCONFIG : " + mac);
                }
            File.WriteAllLines(pDirOut + $@"NoMacSDK.txt", nomac);
            Console.WriteLine("Total Macros SDKconfig : " + dicMac.Count());
            Console.WriteLine($"Total Macros SDKconfig not in KConfig: {c}");
            Console.ReadKey();
            return c;

        }
        //parsing KConfig
        static PropertyList ParserKConfig(string pKFile)
        {
            string typ = "";
            string name = "", UID = "", min = "", max = "";
            bool newProperty = false, flHelp = false, flEnum = false;
            var def = "";
            string lstHelp = "";
            List<PropertyEntry> ListProperties = new List<PropertyEntry>();
            PropertyEntry PropEntry = new PropertyEntry.Boolean();
            bool BoolNameChoice = false;
            PropertyEntry.Enumerated.Suggestion SugEnum = null;// new PropertyEntry.Enumerated.Suggestion();
            List<PropertyEntry.Enumerated.Suggestion> lstSugEnum = new List<PropertyEntry.Enumerated.Suggestion>();
            int aCounrEnumDef = 0;
            int resParse;
            string lnHist = "";
            foreach (var ln in File.ReadAllLines(pKFile))
            {
                if (ln.Contains("menu \"Example Configuration\""))
                    resParse = 3;
                Match m = Regex.Match(ln, "^(menuconfig|config|choice)[ ]+([A-Z0-9_]+)");
                if (m.Success)
                {
                    if (flEnum)
                    {
                        SugEnum = new PropertyEntry.Enumerated.Suggestion();
                        SugEnum.InternalValue = m.Groups[2].Value;
                        if (m.Groups[2].Value == def)
                            def = $"{aCounrEnumDef}";
                        aCounrEnumDef++;
                    }

                    if (m.Groups[1].Value == "choice")
                    { BoolNameChoice = true; flEnum = true; }
                    if (m.Groups[1].Value == "config")
                    { BoolNameChoice = false; }

                    if (name != "")//save
                    {
                        if (typ == "string")
                        {
                            PropEntry = new PropertyEntry.String
                            {
                                Name = name,
                                UniqueID = UID,
                                Description = lstHelp,
                                DefaultValue = def
                            };
                        }
                        if (typ == "int")
                        {
                            PropEntry = new PropertyEntry.Integral
                            {
                                Name = name,
                                UniqueID = UID,
                                Description = lstHelp,
                                DefaultValue = Int32.TryParse(def, out resParse) ? Int32.Parse(def) : 0,
                                MinValue = Int32.TryParse(min, out resParse) ? Int32.Parse(min) : 0,
                                MaxValue = Int32.TryParse(max, out resParse) ? Int32.Parse(max) : 0x0FFFFFFF
                            };
                            //    break;
                        }
                        if (typ == "bool")
                        {
                            PropEntry = new PropertyEntry.Boolean
                            {
                                Name = name,
                                UniqueID = UID,
                                Description = lstHelp,
                                DefaultValue = def.ToLower().Contains("y") ? true : false
                            };
                            //      break;
                        }
                        ListProperties.Add(PropEntry);
                        if (!flEnum)
                            lstHelp = "";//.Clear();
                        flHelp = false;
                    }

                    UID = m.Groups[2].Value;
                    newProperty = true;

                }
                if (!newProperty)
                    continue;

                if (flHelp && !ln.TrimStart().StartsWith("#") && ln.Length > 1)
                    lstHelp += ln.Trim();


                m = Regex.Match(ln, "^[ \t]+(int|bool|hex|prompt)[ ]+[\"]?([\\w0-9_ ]*)[\"]?");
                if (m.Success)
                {
                    if (flEnum)
                    {
                        if (m.Groups[1].Value == "bool" && !BoolNameChoice)
                        {
                            SugEnum.UserFriendlyName = m.Groups[2].Value;
                            lstSugEnum.Add(SugEnum);
                            continue;
                        }
                        //  if (m.Groups[1].Value == "prompt")
                        //     throw new Exception(" no endchoice "+ lnHist);
                    }
                    typ = m.Groups[1].Value; name = m.Groups[2].Value;
                    // if (typ == "prompt") flEnum = true;
                    continue;
                }

                m = Regex.Match(ln, "^[ \t]+default[ \t]([\\w\\d]+)");
                if (m.Success)
                { def = m.Groups[1].Value; continue; }

                m = Regex.Match(ln, "^[ \t]+range[ \t]([\\w\\d]+)[ \t]+([\\w\\d]+)");
                if (m.Success)
                { min = m.Groups[1].Value; max = m.Groups[2].Value; continue; }

                if (Regex.IsMatch(ln, "^[ \t]+help[ \t]*"))
                { flHelp = true; continue; }

                if (Regex.IsMatch(ln, "^[ \t]*endchoice[ \t]*")) // end prompt
                {
                    if (typ != "prompt" && typ != "bool")
                        throw new Exception(" no prompt in endchoice");
                    flEnum = false;
                    PropEntry = new PropertyEntry.Enumerated
                    {
                        Name = name,
                        // UniqueID = UID,
                        Description = lstHelp,
                        DefaultEntryIndex = 1,// def.ToLower().StartsWith("y") ? true : false
                        SuggestionList = lstSugEnum.ToArray()
                    };
                    //      break;

                    ListProperties.Add(PropEntry);
                    lstHelp = "";//.Clear();
                    flHelp = false;
                    aCounrEnumDef = 0;
                }

                lnHist = ln;

            }

            // end file
            //save old record , need it to new function or class
            if (typ == "int")
            {
                PropEntry = new PropertyEntry.Integral
                {
                    Name = name,
                    UniqueID = UID,
                    Description = lstHelp,
                    DefaultValue = Int32.TryParse(def, out resParse) ? Int32.Parse(def) : 0,
                    MinValue = Int32.TryParse(min, out resParse) ? Int32.Parse(min) : 0,
                    MaxValue = Int32.TryParse(max, out resParse) ? Int32.Parse(max) : 0x0FFFFFFF
                };
                //    break;
            }
            if (typ == "bool")
            {
                PropEntry = new PropertyEntry.Boolean
                {
                    Name = name,
                    UniqueID = UID,
                    Description = lstHelp,
                    DefaultValue = def.ToLower().Contains("y") ? true : false
                };
                //      break;
            }
            ListProperties.Add(PropEntry);
            //-----------------------
            List<PropertyGroup> lstPrGr = new List<PropertyGroup>();
            PropertyGroup PrGr = new PropertyGroup();
            PrGr.Properties = ListProperties;
            lstPrGr.Add(PrGr);

            PropertyList ConfigurableProperties = new PropertyList
            {
                PropertyGroups = lstPrGr
            };
            return ConfigurableProperties;
        }
        /*
    PropertyList ConfigurableProperties= new PropertyList
    {
    PropertyGroups = new List<PropertyGroup>
    {
        new PropertyGroup
        {
            Properties = new List<PropertyEntry>
            {
                new PropertyEntry.Enumerated
                {
                    Name = "Execute from",
                    UniqueID = PrimaryMemoryOptionName,
                    SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                    {
                        new PropertyEntry.Enumerated.Suggestion{InternalValue = "flash", UserFriendlyName = "FLASH"},
                        new PropertyEntry.Enumerated.Suggestion{InternalValue = "sram", UserFriendlyName = "SRAM"},
                    }
                }
            }
        }
    }
    */

        //------------------------------------------------
        static void Main(string[] args)
        {
            string tempDir;
            SDKdir = args[0];
            tempDir = args[1];
            string bspDir = SDKdir + @"\esp32-bsp"; //@"..\..\..\..\generators\Esp32\output";
            const string bspRules = @"..\..\..\..\generators\Esp32\Rules";

            if (args.Length > 2)
                if (args[2] == "KConfig")
                { //Generate sdkconfig from KConfig
                    CLParserKConfig.ParserAllFilesKConfig(bspDir);
                    //CLParserKConfig.SdkconfigChangeMacros(@"C:\SysGCC\esp32\esp32-bsp\sysprogs\samples\01_Hello_world\sdkconfig.h");
                    CLParserKConfig.GenerateSdkconfigFile();
                    return;
                }

            if (args.Length < 2)
                throw new Exception("Usage: Esp32VendorSampleParser <Toolchain ESP32 Dir> <TestDir>");


            string sampleListFile = Path.Combine(outputDir, "samples.xml");
            var sampleDir = BuildOrLoadSampleDirectory(SDKdir, outputDir, sampleListFile);
            if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
            {
                //Perform Pass 1 testing - test the raw VendorSamples in-place
                StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, bspDir, tempDir, 1, true);
                foreach (var smp in sampleDir.Samples)
                    if (smp.AllDependencies == null)
                        Console.WriteLine(smp.UserFriendlyName + "no dependes");
                    else
                        for (int cntdep = 0; cntdep < smp.AllDependencies.Count(); cntdep++)
                        {
                            if (smp.AllDependencies[cntdep].StartsWith("/"))
                                smp.AllDependencies[cntdep] = smp.AllDependencies[cntdep].Replace("/usr/lib/", SDKdir + "/lib/");// @"c:/SysGCC/esp32/");

                            if (smp.AllDependencies[cntdep].StartsWith("/"))
                                smp.AllDependencies[cntdep] = SDKdir + smp.AllDependencies[cntdep];

                        }
                sampleDir.ToolchainDirectory = sampleDir.ToolchainDirectory.Replace('/', '\\');
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }

            //Insert the samples into the generated BSP

            var relocator = new Esp32SampleRelocator();

            relocator.InsertVendorSamplesEsp32IntoBSP(sampleDir, bspDir);

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(bspDir, "bsp.xml"));
            bsp.VendorSampleDirectoryPath = "VendorSamples";
            bsp.VendorSampleCatalogName = "ESP32 SDK Samples";
            XmlTools.SaveObject(bsp, Path.Combine(bspDir, "bsp.xml"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            string statFile = Path.ChangeExtension(archiveName, ".xml");
            TarPacker.PackDirectoryToTGZ(bspDir, Path.Combine(bspDir, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp" && Path.GetFileName(fn) != statFile);

            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, "VendorSamples", "VendorSamples.xml"));
            expandedSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
            var result = StandaloneBSPValidator.Program.TestVendorSamples(expandedSamples, bspDir, tempDir, 1, true);
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

            LoadDictRemoveFiles(Path.Combine(SDKdir, "esp32-bsp", "RenameSourceFiles.txt"));

            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            var samples = ParseVendorSamples(SDKdir);

            //            UpdateReferenceFramwork(ref samples);

            Console.ForegroundColor = ConsoleColor.Yellow;
            ToLog($"Total exemples : {CntSamles}");
            ToLog($"Failed exemples : {FaildSamles}, {(CntSamles / 100) * FaildSamles} % from Total");
            Console.ForegroundColor = ConsoleColor.Gray;

            sampleDir = new ConstructedVendorSampleDirectory
            {
                SourceDirectory = Path.Combine(SDKdir, $@"esp32-bsp\esp-idf\examples"),
                Samples = samples.ToArray(),
            };

            XmlTools.SaveObject(sampleDir, sampleListFile);
            return sampleDir;
        }
        static Dictionary<string, string> DictRemF = new Dictionary<string, string>();
        static void LoadDictRemoveFiles(string pFile)
        {
            var st = File.ReadAllLines(pFile);
            foreach (var ln in st)
                if (ln.Contains("=>"))
                    DictRemF.Add(ln.Substring(0, ln.LastIndexOf("=>")).Trim(), ln.Substring(ln.LastIndexOf("=>") + 2).Trim());
        }
        static string GetNewNameFiles(string pFile)
        {
            var tmpName = pFile.Replace("/esp-idf.orig/components", "").Replace("./", "").Replace("//", "/");
            if (DictRemF.ContainsKey(tmpName))
                tmpName = DictRemF[tmpName];
            else
                return pFile;

            tmpName = pFile.Substring(0, pFile.LastIndexOf("/")) + tmpName.Substring(tmpName.LastIndexOf("/"));
            return tmpName;
        }

        static void ChackRenameFiles(ref List<string> lstSource, string pBegDir)
        {
            for (int сount = 0; сount < lstSource.Count(); сount++)
            {
                lstSource[сount] = GetNewNameFiles(lstSource[сount]).Replace("\\", "/").Replace("/esp-idf.orig/", pBegDir + "/esp-idf/").TrimEnd(' ');
                int m = lstSource[сount].IndexOf("/esp-idf");
                if (m > 0)
                    lstSource[сount] = lstSource[сount].Replace(lstSource[сount].Substring(0, m), pBegDir);
            }
        }

        static VendorSample ParseDependFiles(string ExamplDir, string namelog)
        {
            VendorSample vs = new VendorSample();
            List<string> lstFileC = new List<string>();
            List<string> lstFileInc = new List<string>();
            List<string> splitArgs = new List<string>();
            List<string> lstDef = new List<string>();
            Boolean flBoot = false;
            //--------log parser---
            foreach (var ln in File.ReadAllLines(namelog))
            {

                if (ln.Contains("xtensa-esp32-elf-gcc") && ln.Contains("bootloader.elf"))
                    flBoot = true;

                if (!flBoot)
                    continue;

                if (ln.Contains("bootloader_random.c"))
                    continue;

                string ln1 = ln.Replace("-I ", "-I");
                while (ln1.Contains("-I "))
                    ln1 = ln1.Replace("-I ", "-I");
                while (ln1.Contains("-D "))
                    ln1 = ln1.Replace("-D ", "-D");


                if (!ln1.Contains("xtensa-esp32-elf-gcc") && !ln1.Contains("xtensa-esp32-elf-c++"))
                    continue;
                if (!ln1.Contains(" -C ") && !ln1.Contains(" -c "))
                    continue;
                // Get Arguments 
                int munArg;
                IntPtr ptrToSplitArgs = CommandLineToArgvW(ln1, out munArg);
                if (ptrToSplitArgs == IntPtr.Zero)
                    throw new Exception("no arg");

                for (int i = 0; i < munArg; i++)
                {
                    string arg = Marshal.PtrToStringUni(
                     Marshal.ReadIntPtr(ptrToSplitArgs, i * IntPtr.Size));
                    arg = arg.Replace('\'', '\"');
                    if (!splitArgs.Contains(arg))
                        splitArgs.Add(arg);

                }

                // Processing arguments
                lstFileInc.AddRange(string.Join(" ", splitArgs.Where(ar => ar.StartsWith("-I"))).Split(new string[] { "-I" }, StringSplitOptions.RemoveEmptyEntries));
                lstDef.AddRange(string.Join(" ", splitArgs.Where(ar => ar.StartsWith("-D") && !ar.Contains("BOOTLOADER_BUILD"))).Split(new string[] { "-D" }, StringSplitOptions.RemoveEmptyEntries));

                lstFileInc = lstFileInc.Distinct().ToList();

                lstFileC.AddRange(splitArgs.Where(ar => (ar.EndsWith(".c") || ar.EndsWith(".cpp") || ar.ToLower().EndsWith(".s"))));

                if (splitArgs.Where(ar => ar.EndsWith(".c") || ar.EndsWith(".cpp") || ar.ToLower().EndsWith(".s")).Count() > 1)
                    throw new Exception("many source argumens");


                //arguments from file
                var fileArg = splitArgs.SingleOrDefault(ar => ar.StartsWith("@"));
                if (fileArg != null)
                {
                    continue;
                }
                splitArgs.Clear();
            }

            //-------------------
            if (lstFileC.Count == 0)
            {
                Console.WriteLine($"{namelog}  No Source File");
                return null;
            }
            vs.PreprocessorMacros = lstDef.Distinct().ToArray();
            vs.BoardName = "ESP32";
            vs.DeviceID = "ESP32";
            var dlC = lstFileC.Distinct().ToList();

            //--------------------------

            ChackRenameFiles(ref dlC, "$$SYS:BSP_ROOT$$");
            ChackRenameFiles(ref lstFileInc, "$$SYS:BSP_ROOT$$");
            int ind = ExamplDir.IndexOf("\\esp-idf.orig");
            lstFileInc.Insert(1, $@"$$SYS:BSP_ROOT$$/..{ExamplDir.Remove(0, ind)}/build/include".Replace("\\", "/"));// sdkconfig.h

            vs.SourceFiles = dlC.Distinct().ToArray();
            var lstFileInc1 = lstFileInc.Select(f => Path.GetDirectoryName(f)).Distinct();
            vs.IncludeDirectories = lstFileInc.ToArray();
            return vs;
        }
        //-----------------------------------------------
        static void ToLog(string strlog)
        {
            Console.WriteLine(strlog);
        }
        //-----------------------------------------------
        static List<VendorSample> ParseVendorSamples(string SDKdir)
        {
            string[] ExampleDirs = Directory.GetFiles(Path.Combine(SDKdir, $@"esp-idf.orig\examples"), "Makefile", SearchOption.AllDirectories).ToArray();

            List<VendorSample> allSamples = new List<VendorSample>();
            var count = 0;
            int FaildSamlesdef = 0;
            foreach (var makfile in ExampleDirs)
            {
                var mainexamplDir = Path.GetDirectoryName(makfile);
                string nameExampl = mainexamplDir.Substring(mainexamplDir.IndexOf("examples") + 9).Replace("Makefile", "").Replace('\\', '-');

                /*
                if (count == 3)
                    break;
                count++;
                */

                var nameLog = Path.Combine(Path.GetDirectoryName(mainexamplDir), "Logs", $"{nameExampl}.log");
                if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(mainexamplDir), "Logs")))
                    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(mainexamplDir), "Logs"));

                if (File.Exists(nameLog))
                    continue;


                if (Directory.Exists(Path.Combine(Path.GetFullPath(mainexamplDir), "build")))
                {
                    if (Directory.GetFiles(Path.Combine(Path.GetFullPath(mainexamplDir), "build"), "*.elf").Count() > 0)
                    {
                        Console.WriteLine(mainexamplDir + " elf exist");
                        //continue;
                        Directory.Delete(Path.Combine(Path.GetFullPath(mainexamplDir), "build"), true);// new compiling!
                    }
                    try
                    {
                        Directory.Delete(Path.Combine(Path.GetFullPath(mainexamplDir), "build"), true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("No delete folder " + mainexamplDir + "-" + ex.Message);
                        continue;
                    }

                }

                Process compiler = new Process();

                DateTime start = DateTime.Now;
                Console.WriteLine($"Compiling {nameExampl} defconfig  ...");
                string DirMake = mainexamplDir.Replace('\\', '/');

                compiler.StartInfo.FileName = @"c:/sysgcc/esp32/bin/bash.exe";
                compiler.StartInfo.Arguments = $" --login -c \"make VERBOSE=1 -C {DirMake} defconfig   >" + nameLog.Replace(".", "def.") + " 2>&1 \"";
                compiler.StartInfo.UseShellExecute = false;
                compiler.StartInfo.WorkingDirectory = Path.GetDirectoryName($"c:\\SysGCC\\esp32\\bin");
                compiler.EnableRaisingEvents = true;
                compiler.Start();
                compiler.WaitForExit();
                Console.WriteLine($"[{(DateTime.Now - start).TotalMilliseconds:f0} msec]");
                bool buildSucceeded;
                buildSucceeded = compiler.ExitCode == 0;

                if (!buildSucceeded)
                {
                    FaildSamlesdef++;
                    FaildSamles++;
                    Console.WriteLine($"fail {FaildSamlesdef} compil defcongig {nameExampl} ...");
                    continue;
                }


                start = DateTime.Now;
                Console.WriteLine($"Compiling {nameExampl} ...");
                compiler.StartInfo.FileName = @"c:/sysgcc/esp32/bin/bash.exe";
                compiler.StartInfo.Arguments = $"--login -c \"make VERBOSE=1 -C {DirMake} >" + nameLog.Replace('\\', '/') + " 2>&1\"";
                compiler.StartInfo.UseShellExecute = false;
                compiler.StartInfo.WorkingDirectory = Path.GetDirectoryName(@"c:\SysGCC\esp32\bin");
                compiler.StartInfo.WorkingDirectory = Path.GetDirectoryName($"c:\\SysGCC\\esp32\\bin");
                compiler.EnableRaisingEvents = true;
                compiler.Start();

                compiler.WaitForExit();

                Console.WriteLine($"[{(DateTime.Now - start).TotalMilliseconds:f0} msec]");


                CntSamles++;


                buildSucceeded = compiler.ExitCode == 0;

                Console.ForegroundColor = ConsoleColor.Green;
                if (!buildSucceeded)
                {
                    if (!Directory.Exists(outputDir))
                        Directory.CreateDirectory(outputDir);

                    FaildSamles++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    File.Copy(nameLog, Path.Combine(outputDir, $"FaildLogs{nameExampl.Replace('\\', '-')}"), true);
                }
                ToLog(string.Format("{2}: {0} - {1} (Failed: {3})", nameExampl, buildSucceeded ? "Succes" : "Failed ", CntSamles, FaildSamles));
                Console.ForegroundColor = ConsoleColor.Gray;

                if (!buildSucceeded)
                    continue;

            }
            Console.WriteLine($"Total fail compil defcongig  {FaildSamlesdef}");
            Console.WriteLine($"Total fail compil {FaildSamles}");
            count = 0;
            foreach (var makfile in ExampleDirs)
            {
                var mainexamplDir = Path.GetDirectoryName(makfile);
                string nameExampl = mainexamplDir.Substring(mainexamplDir.IndexOf("examples") + 9).Replace("Makefile", "").Replace('\\', '-');
                /*
                if (count == 1)
                    break;
                count++;
                */

                var nameLog = Path.Combine(Path.GetDirectoryName(mainexamplDir), "Logs", $"{nameExampl}.log");

                if (!File.Exists(nameLog))
                {
                    Console.WriteLine($"No Log file {1}", Path.GetDirectoryName(nameLog));
                    continue;
                }
                Console.WriteLine($"Start parser { nameExampl}");
                VendorSample vs = ParseDependFiles(mainexamplDir, nameLog);
                if (vs == null)
                    continue;
                string[] frs = { "noFrameworks" };

                vs.CLanguageStandard = "gnu99";
                vs.CPPLanguageStandard = "gnu++11";
                vs.UserFriendlyName = nameExampl;
                vs.Configuration.Frameworks = frs.ToArray();//null;

                allSamples.Add(vs);
                //Clear
                //File.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "log.txt"));
                //Directory.Delete(Path.Combine(compiler.StartInfo.WorkingDirectory, "_build"), true);*/
            }
            return allSamples;
        }
    }
}
