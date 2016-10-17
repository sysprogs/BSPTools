using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using VandorData;

namespace GeneratorSampleStm32
{

    class Program
    {
        static public List<string> ToAbcolutPath(string dir, List<string> lstDir)
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

        static public List<VendorSample> GetInfoProjectFromMDK(string pDirPrj, List<string> addInc)
        {

            List<VendorSample> aLstVSampleOut = new List<VendorSample>();
            int aCntTarget = 0;
            string aFilePrj = pDirPrj + "\\Project.uvprojx";
            string aNamePrj = pDirPrj.Replace("\\MDK-ARM", "");
            aNamePrj = aNamePrj.Substring(aNamePrj.LastIndexOf("\\") + 1);
            List<string> aSrcFile = new List<string>();
            List<string> aIncFile = new List<string>();
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

                MatchCollection m1 = Regex.Matches(ln, @"[ ]*<FilePath>([\w:\\./]*)</FilePath>[ ]*");
                foreach (Match mc in m1)
                {
                    string aFileSource = mc.Groups[1].Value;
                    if (aFileSource.StartsWith(@"./") || aFileSource.StartsWith(@".\"))
                        aFileSource = pDirPrj + aFileSource.Substring(1);
                    if (!aSrcFile.Contains(aFileSource))
                        aSrcFile.Add(aFileSource);
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

                    aSimpleOut.SourceFiles = ToAbcolutPath(pDirPrj, aSrcFile).ToArray();

                    foreach (var fl in aSimpleOut.IncludeDirectories)
                        aIncFile.Add(fl);
                    aIncFile.AddRange(addInc);
                    aSimpleOut.IncludeDirectories = ToAbcolutPath(pDirPrj, aIncFile).ToArray();

                    aLstVSampleOut.Add(aSimpleOut);
                }
            }

            return aLstVSampleOut;

        }
        //-------------------
        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: stm32.exe <SW package directory> <STM32Cube directory>");

            string constDirPrefix = "Cube_FW_";
            string aDirSDK = args[0];
            List<string> addInc = new List<string>();
            addInc.Add(aDirSDK + @"\STM32Cube_FW_F0_V1.6.0\Drivers\CMSIS\Include");
            string aOutDir = @"..\..\Output";

            if (Directory.Exists(aOutDir))
                Directory.Delete(aOutDir, true);

            Directory.CreateDirectory(aOutDir);

            int aCountSampl = 0;
            Console.WriteLine("Start generate samples");
            foreach (var aCurSDK in Directory.GetDirectories(aDirSDK, "*Cube*"))
            {
                int aIdx1 = aCurSDK.IndexOf(constDirPrefix);
                string aSuffixDir = "";
                if (aIdx1 > 0)
                    aSuffixDir = aCurSDK.Substring(aIdx1 + constDirPrefix.Length, 2);

                if (!Directory.Exists(Path.Combine(aOutDir, aSuffixDir)))
                  Directory.CreateDirectory(Path.Combine(aOutDir, aSuffixDir));

                foreach (var dir in Directory.GetDirectories(aCurSDK, "Mdk-arm", SearchOption.AllDirectories))
                {
                    if (!dir.Contains("Projects") || !(dir.Contains("Examples") || dir.Contains("Applications")))
                        continue;

                    var aSimples = GetInfoProjectFromMDK(dir, addInc);
                    aCountSampl += aSimples.Count;
                    foreach (var aSmpl in aSimples)
                        XmlTools.SaveObject(aSmpl, Path.Combine(aOutDir, aSuffixDir, "sample_" + aSmpl.UserFriendlyName + ".XML"));
                }
                Console.WriteLine("Complit {0} - {1} samples", aSuffixDir, aCountSampl);
            }
        }
    }
}
