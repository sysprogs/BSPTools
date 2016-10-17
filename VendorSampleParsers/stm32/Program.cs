using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace GeneratorSampleStm32
{

    class Program
    {
        static public List<string> ToAbsolutePath(string dir, List<string> lstDir)
        {
            List<string> srcAbc = new List<string>();

            foreach (var sf in lstDir)
            {
                bool a_flCorrectDir = false;
                string aDirAbc = dir;
                string fn = sf;
                fn = fn.Replace(@"/RVDS/", @"/GCC/");
                fn = fn.Replace(@"\RVDS\", @"\GCC\");
                while (fn.StartsWith(@"../") || fn.StartsWith(@"..\"))
                {
                    a_flCorrectDir = true;
                    aDirAbc = aDirAbc.Substring(0, aDirAbc.LastIndexOf("\\"));
                    fn = fn.Substring(3, fn.Length - 3);
                }
                if (a_flCorrectDir)
                    fn = aDirAbc + "\\" + fn;
                srcAbc.Add(fn);
            }
            return srcAbc;
        }

        static public List<ConstructedVendorSample> GetInfoProjectFromMDK(string pDirPrj, List<string> extraIncludeDirs)
        {
            List<ConstructedVendorSample> aLstVSampleOut = new List<ConstructedVendorSample>();
            int aCntTarget = 0;
            string aFilePrj = pDirPrj + "\\Project.uvprojx";
            string aNamePrj = pDirPrj.Replace("\\MDK-ARM", "");
            aNamePrj = aNamePrj.Substring(aNamePrj.LastIndexOf("\\") + 1);
            List<string> sourceFiles = new List<string>();
            List<string> headerFiles = new List<string>();
            bool flGetProperty = false;
            string aTarget = "";
            ConstructedVendorSample aSimpleOut = new ConstructedVendorSample();
            foreach (var ln in File.ReadAllLines(aFilePrj))
            {
                if (ln.Contains("<Target>"))
                {
                    if (aCntTarget == 0)
                        aSimpleOut = new ConstructedVendorSample();
                    aCntTarget++;
                }
                if (ln.Contains("</Target>"))
                    if (aCntTarget == 0)
                        throw new Exception("wrong tag Targets");
                    else
                        aCntTarget--;

                if (ln.Contains("<Cads>"))
                    flGetProperty = true;
                else if (ln.Contains("</Cads>"))
                    flGetProperty = false;

                Match m = Regex.Match(ln, "[ \t]*<Device>(.*)</Device>[ \t]*");
                if (m.Success)
                {
                    aSimpleOut.DeviceID = m.Groups[1].Value;
                    if (aSimpleOut.DeviceID.EndsWith("x"))
                        aSimpleOut.DeviceID = aSimpleOut.DeviceID.Remove(aSimpleOut.DeviceID.Length - 2, 2);
                }
                m = Regex.Match(ln, "[ \t]*<TargetName>(.*)</TargetName>[ \t]*");
                if (m.Success)
                    aTarget = m.Groups[1].Value;

                MatchCollection m1 = Regex.Matches(ln, @"[ ]*<FilePath>([\w\-:\\./]*)</FilePath>[ ]*");
                foreach (Match mc in m1)
                {
                    string filePath = mc.Groups[1].Value;
                    if (filePath.StartsWith(@"./") || filePath.StartsWith(@".\"))
                        filePath = pDirPrj + filePath.Substring(1);
                    if (filePath.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    if (!sourceFiles.Contains(filePath))
                        sourceFiles.Add(filePath);
                }

                if (flGetProperty)
                {
                    m = Regex.Match(ln, "[ \t]*<IncludePath>(.*)</IncludePath>[ \t]*");
                    if (m.Success && m.Groups[1].Value != "")
                        aSimpleOut.IncludeDirectories = m.Groups[1].Value.Split(';');

                    m = Regex.Match(ln, "[ \t]*<Define>(.*)</Define>[ \t]*");
                    if (m.Success && m.Groups[1].Value != "")
                        aSimpleOut.PreprocessorMacros = m.Groups[1].Value.Split(',');
                }


                if (ln.Contains("</Target>") && aCntTarget == 0)
                {
                    aSimpleOut.Path = pDirPrj;
                    aSimpleOut.Description = "This example " + aNamePrj + " for " + aTarget;
                    aSimpleOut.UserFriendlyName = aNamePrj + "_" + aTarget;
                    aSimpleOut.SourceFiles = ToAbsolutePath(pDirPrj, sourceFiles).ToArray();

                    foreach (var fl in aSimpleOut.IncludeDirectories)
                        headerFiles.Add(fl);
                    headerFiles.AddRange(extraIncludeDirs);
                    aSimpleOut.IncludeDirectories = ToAbsolutePath(pDirPrj, headerFiles).ToArray();

                    aLstVSampleOut.Add(aSimpleOut);
                }
            }
            return aLstVSampleOut;
        }

        static string ExtractFirstSubdir(string dir) => dir.Split('\\')[1];

        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: stm32.exe <SW package directory> <temporary directory>");
            string SDKdir = args[0];
            string outputDir = @"..\..\Output";
            string tempDir = args[1];

            bool reparseSampleDefinitions = true;
            ConstructedVendorSampleDirectory sampleDir;
            if (reparseSampleDefinitions)
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
                Directory.CreateDirectory(outputDir);

                var samples = ParseVendorSamples(SDKdir);
                sampleDir = new ConstructedVendorSampleDirectory
                {
                    Samples = samples.ToArray()
                };

                XmlTools.SaveObject(sampleDir, Path.Combine(outputDir, "samples.xml"));
            }
            else
            {
                sampleDir = XmlTools.LoadObject<ConstructedVendorSampleDirectory>(Path.Combine(outputDir, "samples.xml"));
            }

            StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, @"..\..\..\..\generators\stm32\output", tempDir);
            XmlTools.SaveObject(sampleDir, Path.Combine(outputDir, "samples.xml"));
        }

        static List<ConstructedVendorSample> ParseVendorSamples(string SDKdir)
        { 
            string stm32RulesDir = @"..\..\..\..\generators\stm32\rules\families";

            string[] familyDirs = Directory.GetFiles(stm32RulesDir, "stm32*.xml").Select(f => ExtractFirstSubdir(XmlTools.LoadObject<FamilyDefinition>(f).PrimaryHeaderDir)).ToArray();
            List<ConstructedVendorSample> allSamples = new List<ConstructedVendorSample>();

            foreach (var fam in familyDirs)
            {
                List<string> addInc = new List<string>();
                addInc.Add($@"{SDKdir}\{fam}\Drivers\CMSIS\Include");

                int aCountSampl = 0;
                Console.Write($"Discovering samples for {fam}...");

                foreach (var dir in Directory.GetDirectories(Path.Combine(SDKdir, fam), "Mdk-arm", SearchOption.AllDirectories))
                {
                    if (!dir.Contains("Projects") || !(dir.Contains("Examples") || dir.Contains("Applications")))
                        continue;

                    var aSamples = GetInfoProjectFromMDK(dir, addInc);
                    aCountSampl += aSamples.Count;
                    allSamples.AddRange(aSamples);
                }
                Console.WriteLine($" {aCountSampl} samples found");
            }
            return allSamples;
        }
    }
}
