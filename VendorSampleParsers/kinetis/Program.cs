using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;


using System.Xml;


namespace GeneratorSampleKinetis
{
    class Program
    {
        //Required Framworks
        static Tuple<List<string>, List<string>, List<string>> GetRequiredFiles(EmbeddedFramework fr, List<EmbeddedFramework> aAllFr)
        //out AddSourceFile,AddIncludeFile,AddIncludeDir
        {

            List<string> alstAddSourceFile = new List<string>();
            List<string> alstAddIncludeFile = new List<string>();
            List<string> alstAddIncludeDir = new List<string>();

            if (fr.RequiredFrameworks != null)
                foreach (var RecFramwork in fr.RequiredFrameworks)
                {
                    var recfr = aAllFr.Where(p => p.ID == RecFramwork).SingleOrDefault();
                    Tuple<List<string>, List<string>, List<string>> aTupleRec = GetRequiredFiles(recfr, aAllFr);

                    if (aTupleRec != null)
                    {
                        alstAddSourceFile.AddRange(aTupleRec.Item1);
                        alstAddIncludeFile.AddRange(aTupleRec.Item2);
                        alstAddIncludeDir.AddRange(aTupleRec.Item3);
                    }
                }

            alstAddSourceFile.AddRange(fr.AdditionalSourceFiles);
            alstAddIncludeFile.AddRange(fr.AdditionalHeaderFiles);
            alstAddIncludeDir.AddRange(fr.AdditionalIncludeDirs);
            return Tuple.Create<List<string>, List<string>, List<string>>(alstAddSourceFile, alstAddIncludeFile, alstAddIncludeDir);
        }

        //----------------------------------------------------
        static List<VendorSample> ParcerManifestKSDK(string strSDKdir)
        {
            if (Directory.GetFiles(strSDKdir, "*manifest.xml").Count() != 1)
                throw new  Exception("Error count file manifest");


            string strPathToXML = Directory.GetFiles(strSDKdir, "*manifest.xml")[0];

            List<VendorSample> vsl = new List<VendorSample>();

            XmlDocument d = new XmlDocument();
            d.Load(strPathToXML);
            string aBoardName = d.SelectSingleNode("//boards/board").Attributes?.GetNamedItem("name")?.Value; // Get Board Name

            Dictionary<string,List<EmbeddedFramework>> aAllDivecesFrWs = new Dictionary<string, List<EmbeddedFramework>> ();
            foreach (XmlNode varDev in d.SelectNodes("//devices/device"))
            {
                string aDeviceId = varDev.Attributes?.GetNamedItem("full_name")?.Value; //Get Divece ID
                string aDeviceName = varDev.Attributes?.GetNamedItem("name")?.Value;

                List<EmbeddedFramework> aAllFrWs = new List<EmbeddedFramework>();
                aAllDivecesFrWs.Add(aDeviceName, aAllFrWs);
                var acompXml = d.SelectNodes(string.Format("//components/component[@device='{0}']", aDeviceName));

                Dictionary<string, int> strAttr = new Dictionary<string, int>();
                foreach (XmlNode node in acompXml)
                {
                    string nameComponent = node.SelectSingleNode("@name").Value;
                    if (nameComponent == "linker" || nameComponent == "startup")
                        continue;

                    XmlNodeList aFnT = node.SelectNodes("source");
                    string aDependency = node.Attributes?.GetNamedItem("dependency")?.Value;
                    string[] strLstDep = aDependency?.Split(' ')?.Where(p => !(p.EndsWith("_startup") || p.EndsWith("test"))).ToArray();
                    List<string> strLInclFiles = new List<string>();
                    List<string> strLIncDir = new List<string>();
                    List<string> strSourceFiles = new List<string>();
                    var node1 = node.SelectNodes("source");
                    foreach (XmlNode vsrc in node1)
                    {

                        string aPatch = vsrc.Attributes.GetNamedItem("path").Value;
                        aPatch = aPatch.Replace("$|device|", aDeviceName);
                        string FullPatch = Path.Combine(strSDKdir, aPatch) + "/";
                        string aType = vsrc.Attributes?.GetNamedItem("type")?.Value;
                        XmlNodeList aFn = vsrc.SelectNodes("files");
                        foreach (XmlNode fsrc in aFn)
                        {
                            string aNameFile = fsrc.SelectSingleNode("@mask").Value;

                            if (aType == "src")
                                strSourceFiles.Add(FullPatch + aNameFile);
                            else if (aType == "c_include")
                            {
                                strLInclFiles.Add(FullPatch + aNameFile);
                                if (!strLIncDir.Contains(FullPatch))
                                    strLIncDir.Add(FullPatch);
                            }
                        }
                    }


                    EmbeddedFramework nEmbFr = new EmbeddedFramework
                    {
                        ID = node.SelectSingleNode("@name").Value,
                        UserFriendlyName = node.SelectSingleNode("@name").Value + node?.SelectSingleNode("@type").Value,
                        AdditionalSourceFiles = strSourceFiles.ToArray(),
                        AdditionalHeaderFiles = strLInclFiles.ToArray(),
                        RequiredFrameworks = strLstDep,
                        AdditionalIncludeDirs = strLIncDir.ToArray(),
                    };
                    aAllFrWs.Add(nEmbFr);

                }

                EmbeddedFramework templateFr = aAllFrWs.Where(fr => (fr.UserFriendlyName.Contains(aBoardName + "project_template"))).SingleOrDefault();
                if (templateFr == null)
                {
                    Console.Write($"manifest has not project_template");
                    continue;
                }

                var aAddFiles = GetRequiredFiles(templateFr, aAllFrWs);
                aAddFiles.Item1.Add(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\main.c"));
                bool aIsFreeRtosFr = templateFr.RequiredFrameworks.Contains("freertos");


                VendorSample vs = new VendorSample
                {
                    Configuration = new VendorSampleConfiguration { Frameworks = templateFr?.RequiredFrameworks },
                    SourceFiles = aAddFiles.Item1.Distinct().Where(p => (!p.Contains("freertos") || aIsFreeRtosFr)).ToArray(),
                    HeaderFiles = aAddFiles.Item2.Distinct().Where(p => (!p.Contains("freertos") || aIsFreeRtosFr)).ToArray(),
                    IncludeDirectories = aAddFiles.Item3.Distinct().ToArray(),
                    BoardName = aBoardName,
                    DeviceID = aDeviceId,
                    UserFriendlyName = "LEDBlink",
                };

                VendorSample[] vsa = new VendorSample[] { vs };
                vsl.Add(vs);
            }
            return vsl;
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: stm32.exe <SW package directory> <temporary directory>");
            string SDKdir = args[0];
            string outputDir = @"..\..\Output";
            string bspDir = args[2];
            string tempDir = args[1];

            string sampleListFile = Path.Combine(outputDir, "samples.xml");

            var sampleDir = BuildOrLoadSampleDirectory(SDKdir, outputDir, sampleListFile);
            if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
            {
                StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, bspDir, tempDir);
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }
        }

        private static ConstructedVendorSampleDirectory BuildOrLoadSampleDirectory(string SDKdir, string outputDir, string sampleListFile)
        {
            ConstructedVendorSampleDirectory sampleDir;

            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            var samples = ParcerManifestKSDK(SDKdir);
            sampleDir = new ConstructedVendorSampleDirectory
            {
                SourceDirectory = SDKdir,
                Samples = samples.ToArray(),
            };

            XmlTools.SaveObject(sampleDir, sampleListFile);
            return sampleDir;
        }
    }
}
