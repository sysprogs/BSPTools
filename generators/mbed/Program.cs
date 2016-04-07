using BSPEngine;
using BSPGenerationTools;
using StandaloneBSPValidator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace mbed
{
    class Program
    {
        static void Main(string[] args)
        {
            string outputDir = Path.GetFullPath(@"..\..\Output");
            string dataDir = Path.GetFullPath(@"..\..\data");
            Directory.CreateDirectory(outputDir);
            string mbedRoot = Path.Combine(outputDir, "mbed");

            List<KeyValuePair<Regex, string>> nameRules = new List<KeyValuePair<Regex, string>>();
            foreach(var line in File.ReadAllLines(Path.Combine(dataDir, "DeviceNameRules.txt")))
            {
                int idx = line.IndexOf('=');
                nameRules.Add(new KeyValuePair<Regex, string>(new Regex(line.Substring(0, idx).Trim()), line.Substring(idx + 1).Trim()));
            }

            bool regenerate = true;
            if (regenerate)
            {
                Process proc;
                if (Directory.Exists(mbedRoot))
                    proc = Process.Start(new ProcessStartInfo(@"C:\Program Files\Git\bin\git.exe", "pull") { WorkingDirectory = mbedRoot, UseShellExecute = false });
                else
                    proc = Process.Start(new ProcessStartInfo(@"C:\Program Files\Git\bin\git.exe", "clone https://github.com/mbedmicro/mbed.git") { WorkingDirectory = outputDir, UseShellExecute = false });
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new Exception("Git exited with code " + proc.ExitCode);

                string sampleDir = Path.Combine(mbedRoot, "samples");
                if (Directory.Exists(sampleDir))
                    Directory.Delete(sampleDir, true);
                PathTools.CopyDirectoryRecursive(Path.Combine(dataDir, "samples"), sampleDir);

                ProcessStartInfo bspGenInfo = new ProcessStartInfo(@"E:\ware\Python27\python.exe", Path.Combine(dataDir, "visualgdb_bsp.py"));
                bspGenInfo.UseShellExecute = false;
                bspGenInfo.EnvironmentVariables["PYTHONPATH"] = mbedRoot;
                proc = Process.Start(bspGenInfo);
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new Exception("BSP generator exited with code " + proc.ExitCode);
            }

            File.Copy(Path.Combine(dataDir, "stubs.cpp"), Path.Combine(mbedRoot, "stubs.cpp"), true);
            Dictionary<string, string> mcuDefs = new Dictionary<string, string>();
            foreach (var dir in Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\EmbeddedBSPs\arm-eabi"))
            {
                var anotherBSP = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(dir, "bsp.xml"));
                foreach (var mcu in anotherBSP.SupportedMCUs)
                {
                    if (mcu.MCUDefinitionFile != null)
                        mcuDefs[mcu.ID] = Path.Combine(dir, mcu.MCUDefinitionFile);
                }
            }

            string bspFile = Path.Combine(mbedRoot, "BSP.xml");
            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspFile);
            var defDir = Directory.CreateDirectory(Path.Combine(mbedRoot, "DeviceDefinitions"));
            foreach(var mcu in bsp.SupportedMCUs)
            {
                foreach (var rule in nameRules)
                {
                    var m = rule.Key.Match(mcu.ID);
                    if (m.Success)
                    {
                        string devRegex = rule.Value;
                        for (int i = 1; i < m.Groups.Count; i++)
                            devRegex = devRegex.Replace(@"\" + i, m.Groups[i].Value);

                        Regex devRegexObj = new Regex(devRegex);
                        string definition = null;
                        foreach (var dev in mcuDefs)
                        {
                            if (devRegexObj.IsMatch(dev.Key))
                                definition = dev.Value;
                        }

                        if (definition == null)
                            Console.WriteLine("Warning: cannot find device register definition for " + devRegex);
                        else
                        {
                            mcu.MCUDefinitionFile = "DeviceDefinitions/" + Path.GetFileName(definition);
                            File.Copy(definition + ".gz", Path.Combine(mbedRoot, mcu.MCUDefinitionFile + ".gz"), true);
                        }
                        break;
                    }
                }
            }
            ProduceBSPArchive(mbedRoot, bsp);

            if (true)
            {
                Console.WriteLine("Testing BSP...");
                var job = XmlTools.LoadObject<TestJob>(Path.Combine(dataDir, "testjob.xml"));

                var toolchain = LoadedToolchain.Load(Environment.ExpandEnvironmentVariables(job.ToolchainPath), new ToolchainRelocationManager());
                var lbsp = LoadedBSP.Load(Environment.ExpandEnvironmentVariables(Path.Combine(outputDir, "mbed")), toolchain, false);
                var r = StandaloneBSPValidator.Program.TestBSP(job, lbsp, Path.Combine(outputDir, "TestResults"));
                if (r.Failed != 0 || r.Passed < 86)
                    throw new Exception("Some of the tests failed. Check test results.");
            }
        }

        static void ProduceBSPArchive(string BSPRoot, BoardSupportPackage bsp)
        {
            bsp.PackageVersion = string.Format("{0:d4}{1:d2}{2:d2}", DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
            XmlTools.SaveObject(bsp, Path.Combine(BSPRoot, "BSP.XML"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            Console.WriteLine("Creating BSP archive...");

            TarPacker.PackDirectoryToTGZ(BSPRoot, Path.Combine(Path.GetDirectoryName(BSPRoot), archiveName), fn =>
            {
                string relPath = fn.Substring(BSPRoot.Length + 1);
                if (relPath.StartsWith(".git"))
                    return false;
                return true;
            });

            BSPSummary lst = new BSPSummary
            {
                BSPName = bsp.PackageDescription,
                BSPID = bsp.PackageID,
                BSPVersion = bsp.PackageVersion,
                MinimumEngineVersion = bsp.MinimumEngineVersion,
                FileName = archiveName,
            };

            foreach (var mcu in bsp.SupportedMCUs)
                lst.MCUs.Add(new BSPSummary.MCU { Name = mcu.ID, FLASHSize = mcu.FLASHSize, RAMSize = mcu.RAMSize });

            XmlTools.SaveObject(lst, Path.Combine(Path.GetDirectoryName(BSPRoot), Path.ChangeExtension(archiveName, ".xml")));
        }
    }
}
