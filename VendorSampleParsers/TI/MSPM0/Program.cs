using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using VendorSampleParserEngine;

namespace MSPM0VendorSampleParser
{
    internal class Program
    {
        class ParserImpl : VendorSampleParser
        {
            public ParserImpl()
                : base(@"..\..\..\generators\TI\mspm0\output", "TI SDK Samples")
            {
            }

            class RelocatorImpl : VendorSampleRelocator
            {

            }

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                return new RelocatorImpl();
            }

            public struct ParsedProjectSpec
            {
                public string[] RelativeSourcePaths;
                public string[] CFLAGS;
                public string[] LDFLAGS;
                public string DeviceID;
            }

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                var specFiles = Directory.GetFiles(SDKdir, "*.projectspec", SearchOption.AllDirectories);
                var vendorSamples = new List<VendorSample>();
                var failedSamples = new List<UnparseableVendorSample>();
                int skippedSamples = 0;

                var commonFiles = Directory.GetFiles(Path.Combine(SDKdir, @"source\ti\driverlib"), "*.c", SearchOption.AllDirectories);

                foreach (var g in specFiles.GroupBy(f => Path.GetDirectoryName(Path.GetDirectoryName(f))))
                {
                    var arr = g.ToArray();
                    string primaryFN = arr[0];
                    if (arr.Length > 1)
                        primaryFN = g.First(x => x.Contains("\\gcc\\"));

                    var sampleDir = g.Key;
                    var relPath = sampleDir.Substring(SDKdir.Length).TrimStart('\\');
                    if (!File.Exists(Path.Combine(sampleDir, "ti_msp_dl_config.h")))
                    {
                        skippedSamples++;   //About 30% of the samples don't have the config file included. This can be theoretically fixed by running the sysconfig tool.
                        continue;
                    }

                    var doc = new XmlDocument();
                    doc.Load(primaryFN);
                    var fileNodes = doc.SelectNodes("/projectSpec/project/file");
                    try
                    {
                        var parsedSpec = new ParsedProjectSpec
                        {
                            RelativeSourcePaths = fileNodes.OfType<XmlNode>().Select(n => n.Attributes["path"].Value).ToArray(),
                            CFLAGS = doc.SelectSingleNode("/projectSpec/project")?.Attributes["compilerBuildOptions"]?.Value.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray(),
                            LDFLAGS = doc.SelectSingleNode("/projectSpec/project")?.Attributes["linkerBuildOptions"]?.Value.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray(),
                            DeviceID = doc.SelectSingleNode("/projectSpec/project")?.Attributes["device"]?.Value,
                        };

                        var frameworks = new List<string> { "com.sysprogs.arm.ti.mspm0.driverlib" };
                        if (parsedSpec.LDFLAGS.Contains("-l:arm_cortexM0l_math.a"))
                            frameworks.Add("com.sysprogs.arm.ti.mspm0.dsp_lib");

                        var vs = new VendorSample
                        {
                            InternalUniqueID = relPath.Replace('\\', '-'),
                            VirtualPath = relPath,
                            UserFriendlyName = Path.GetFileName(relPath),
                            IncludeDirectories = parsedSpec.CFLAGS.Where(f => f.StartsWith("-I")).Select(f => TranslateIncludeDir(SDKdir, sampleDir, f.Substring(2))).Where(x => x != null).ToArray(),
                            Path = sampleDir,
                            DeviceID = parsedSpec.DeviceID,
                            SourceFiles = parsedSpec.RelativeSourcePaths.Select(f => TranslateSourcePath(Path.GetDirectoryName(primaryFN), f)).Where(f => f != null).ToArray(),
                            Configuration = new VendorSampleConfiguration
                            {
                                Frameworks = frameworks.ToArray(),
                                Configuration = new PropertyDictionary2
                                {
                                    Entries = new[]
                                    {
                                        new PropertyDictionary2.KeyValue{Key = "com.sysprogs.bspoptions.mspm0.driverlib.dl_factoryregion", Value = "1"}
                                    }
                                }
                            }
                        };

                        vs.SourceFiles = vs.SourceFiles.Where(s => !Path.GetFileName(s).StartsWith("startup_"))/*.Concat(commonFiles)*/.Append(Path.Combine(sampleDir, "ti_msp_dl_config.c")).Distinct(StringComparer.InvariantCultureIgnoreCase).ToArray();
                        vendorSamples.Add(vs);
                    }
                    catch (Exception ex)
                    {
                        failedSamples.Add(new UnparseableVendorSample { UniqueID = Path.GetFileNameWithoutExtension(primaryFN), ErrorDetails = ex.Message });
                    }
                }

                return new ParsedVendorSamples { VendorSamples = vendorSamples.ToArray(), FailedSamples = failedSamples.ToArray() };
            }

            private string TranslateIncludeDir(string sdkDir, string projectDir, string escapedPath)
            {
                var result = escapedPath
                    .Replace("${PROJECT_ROOT}", projectDir)
                    .Replace("${COM_TI_MSPM0_SDK_INSTALL_DIR}", sdkDir);

                if (result.Contains("${"))
                    return null;

                return result;
            }

            private string TranslateSourcePath(string baseDir, string fn)
            {
                return Path.GetFullPath(Path.Combine(baseDir, fn));
            }
        }

        static void Main(string[] args)
        {
            new ParserImpl().Run(args);
        }
    }
}
