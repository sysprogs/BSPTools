using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VendorSampleParserEngine;

namespace RS14100
{
    class Program
    {
        class RS141VendorSampleParser : VendorSampleParser
        {
            public RS141VendorSampleParser()
                : base(@"..\..\generators\RS14100\output", "RS14100 SDK Samples")
            {
            }

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                return new VendorSampleRelocator();
            }

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                /*
                 *  Since most of the original SDK samples only consist of a handful of files, we use a very simplified logic to enumerate them:
                 *      1. Locate all "Makefile" and "readme.txt" files.
                 *      2. Discard those that don't have .c files in the same directory, or have multiple directories with .c files.
                 *      3. Depending on the config files stored in the same directory, we pick one of the 3 predefined profiles (wlan, bluetooth, periph).
                 *         The profiles come from manually created VisualGDB projects and specify a minimum viable configuration to build basic samples.
                 *         
                 *   Then, we try building all samples discovered via this simplified algorithm, and the successfully built ones into the BSP.
                 */
                var sampleBase = Path.Combine(SDKdir, "Examples");
                var allFiles = Directory.GetFiles(sampleBase, "*.*", SearchOption.AllDirectories);

                List<VendorSample> vendorSamples = new List<VendorSample>();

                var wlanConfig = XmlTools.LoadObject<EmbeddedProfile>(Path.Combine(RulesDirectory, "wlan.xml"));
                var bareConfig = XmlTools.LoadObject<EmbeddedProfile>(Path.Combine(RulesDirectory, "periph.xml"));
                var btConfig = XmlTools.LoadObject<EmbeddedProfile>(Path.Combine(RulesDirectory, "bluetooth.xml"));

                foreach(var dir in allFiles.Where(f => Path.GetFileName(f) == "Makefile" || Path.GetFileName(f).ToLower() == "readme.txt").Select(Path.GetDirectoryName).Distinct())
                {
                    var matchingSources = allFiles.Where(f => Path.GetExtension(f) == ".c" && f.StartsWith(dir + "\\")).ToArray();
                    var matchingDirs = matchingSources.Select(Path.GetDirectoryName).Distinct().ToArray();

                    if (matchingDirs.Length != 1)
                        continue;

                    string name = Path.GetFileName(dir);
                    string macros = null;

                    EmbeddedProfile matchingProfile;
                    if (File.Exists(Path.Combine(dir, "rsi_ble_config.h")))
                    {
                        matchingProfile = btConfig;
                        macros = "BT_BDR_MODE=0";
                    }
                    if (File.Exists(Path.Combine(dir, "rsi_wlan_config.h")))
                        matchingProfile = wlanConfig;
                    else
                    {
                        matchingProfile = bareConfig;
                        macros = "__SYSTICK";
                    }

                    var sample = new VendorSample
                    {
                        DeviceID = "RS14100",
                        SourceFiles = matchingSources,
                        UserFriendlyName = name,
                        InternalUniqueID = dir.Substring(SDKdir.Length).TrimStart('\\').Replace('\\', '_'),
                        Path = dir,
                        IncludeDirectories = new[] { dir },
                        VirtualPath = Path.GetDirectoryName(dir).Substring(SDKdir.Length).TrimStart('\\'),

                        Configuration = matchingProfile.ToConfiguration(),
                    };

                    if (File.Exists(Path.Combine(dir, "readme.txt")))
                        sample.Description = File.ReadAllText(Path.Combine(dir, "readme.txt"));

                    if (macros != null)
                        sample.PreprocessorMacros = macros.Split(';');

                    vendorSamples.Add(sample);
                }

                return new ParsedVendorSamples { VendorSamples = vendorSamples.ToArray() };
            }
        }

        static void Main(string[] args) => new RS141VendorSampleParser().Run(args);
    }

    public class EmbeddedProfile
    {
        public string[] ReferencedFrameworks;
        public PropertyDictionary2 FrameworkProperties;

        public VendorSampleConfiguration ToConfiguration()
        {
            return new VendorSampleConfiguration
            {
                Frameworks = ReferencedFrameworks,
                Configuration = FrameworkProperties,
            };
        }
    }
}
