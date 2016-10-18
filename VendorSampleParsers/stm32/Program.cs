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
        static public List<string> ToAbsolutePath(string dir, string topLevelDir, List<string> lstDir)
        {
            List<string> srcAbc = new List<string>();

            foreach (var sf in lstDir)
            {
                string fn = sf;
                fn = fn.Replace(@"/RVDS/", @"/GCC/");
                fn = fn.Replace(@"\RVDS\", @"\GCC\");

                if (!Path.IsPathRooted(fn))
                    fn = Path.GetFullPath(Path.Combine(dir, fn));

                if (!File.Exists(fn) && !Directory.Exists(fn))
                {
                    if (Path.GetFileName(fn).ToLower() == "readme.txt" || Path.GetFileName(fn).ToLower() == "ipv4")
                        continue;

                    if (fn.EndsWith("\\component", StringComparison.InvariantCultureIgnoreCase) && Directory.Exists(fn + "s"))
                        fn += "s";
                    else
                    {
                        string fn2 = Path.Combine(topLevelDir, sf);
                        if (Directory.Exists(fn2))
                            fn = fn2;
                        else
                            Console.WriteLine("Missing file/directory: " + fn);
                    }
                }

                srcAbc.Add(fn);
            }
            return srcAbc;
        }

        static public List<VendorSample> GetInfoProjectFromMDK(string pDirPrj, string topLevelDir, List<string> extraIncludeDirs)
        {
            List<VendorSample> aLstVSampleOut = new List<VendorSample>();
            int aCntTarget = 0;
            string aFilePrj = pDirPrj + "\\Project.uvprojx";
            string aNamePrj = pDirPrj.Replace("\\MDK-ARM", "");
            aNamePrj = aNamePrj.Substring(aNamePrj.LastIndexOf("\\") + 1);
            List<string> sourceFiles = new List<string>();
            List<string> includeDirs = new List<string>();
            bool flGetProperty = false;
            string aTarget = "";
            VendorSample aSimpleOut = new VendorSample();
            foreach (var ln in File.ReadAllLines(aFilePrj))
            {
                if (ln.Contains("<Target>"))
                {
                    if (aCntTarget == 0)
                        aSimpleOut = new VendorSample();
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
                    aSimpleOut.Path = Path.GetDirectoryName(pDirPrj);
                    aSimpleOut.Description = "This example " + aNamePrj + " for " + aTarget;
                    aSimpleOut.UserFriendlyName = aNamePrj + "_" + aTarget;
                    aSimpleOut.SourceFiles = ToAbsolutePath(pDirPrj, topLevelDir, sourceFiles).ToArray();

                    foreach (var fl in aSimpleOut.IncludeDirectories)
                        includeDirs.Add(fl);
                    includeDirs.AddRange(extraIncludeDirs);
                    aSimpleOut.IncludeDirectories = ToAbsolutePath(pDirPrj, topLevelDir, includeDirs).ToArray();

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
            const string bspDir = @"..\..\..\..\generators\stm32\output";
            string tempDir = args[1];

            bool reparseSampleDefinitions = false;
            ConstructedVendorSampleDirectory sampleDir;
            if (reparseSampleDefinitions)
            {
                if (Directory.Exists(outputDir))
                    Directory.Delete(outputDir, true);
                Directory.CreateDirectory(outputDir);

                var samples = ParseVendorSamples(SDKdir);
                sampleDir = new ConstructedVendorSampleDirectory
                {
                    SourceDirectory = SDKdir,
                    Samples = samples.ToArray(),
                };

                XmlTools.SaveObject(sampleDir, Path.Combine(outputDir, "samples.xml"));
            }
            else
            {
                sampleDir = XmlTools.LoadObject<ConstructedVendorSampleDirectory>(Path.Combine(outputDir, "samples.xml"));
            }

            StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, bspDir, tempDir);
            XmlTools.SaveObject(sampleDir, Path.Combine(outputDir, "samples.xml"));

            /*var relocator = new VendorSampleRelocator();
            relocator.InsertVendorSamplesIntoBSP(sampleDir, bspDir);*/

            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, "VendorSamples", "VendorSamples.xml"));
            expandedSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
            StandaloneBSPValidator.Program.TestVendorSamples(expandedSamples, bspDir, tempDir);

            //VendorSampleRelocator.ValidateVendorSampleDependencies(sampleDir, @"f:\sysgcc");
        }

        static List<VendorSample> ParseVendorSamples(string SDKdir)
        { 
            string stm32RulesDir = @"..\..\..\..\generators\stm32\rules\families";

            string[] familyDirs = Directory.GetFiles(stm32RulesDir, "stm32*.xml").Select(f => ExtractFirstSubdir(XmlTools.LoadObject<FamilyDefinition>(f).PrimaryHeaderDir)).ToArray();
            List<VendorSample> allSamples = new List<VendorSample>();

            foreach (var fam in familyDirs)
            {
                List<string> addInc = new List<string>();
                addInc.Add($@"{SDKdir}\{fam}\Drivers\CMSIS\Include");
                string topLevelDir = $@"{SDKdir}\{fam}";

                int aCountSampl = 0;
                Console.Write($"Discovering samples for {fam}...");

                foreach (var dir in Directory.GetDirectories(Path.Combine(SDKdir, fam), "Mdk-arm", SearchOption.AllDirectories))
                {
                    if (!dir.Contains("Projects") || !(dir.Contains("Examples") || dir.Contains("Applications")))
                        continue;

                    var aSamples = GetInfoProjectFromMDK(dir, topLevelDir, addInc);
                    aCountSampl += aSamples.Count;
                    allSamples.AddRange(aSamples);
                }
                Console.WriteLine($" {aCountSampl} samples found");
            }
            return allSamples;
        }
    }
}
