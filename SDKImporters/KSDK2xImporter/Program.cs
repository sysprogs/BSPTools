using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;


namespace KSDK2xImporter
{
    class Program
    {
        interface IWarningSink
        {
            void LogWarning(string warning);
        }

        class ParsedSDK
        {
            public BoardSupportPackage BSP;

            public void Save(string directory)
            {
                XmlTools.SaveObject(BSP, Path.Combine(directory, "BSP.XML"));
            }
        }

        class ParsedComponent
        {
            public EmbeddedFramework Framework;
            public string OriginalType;

            public override string ToString()
            {
                return $"{Framework?.UserFriendlyName} ({OriginalType})";
            }
        }


        static ParsedSDK ParseKSDKManifest(string sdkDirectory, IWarningSink sink)
        {
            string[] manifestFiles = Directory.GetFiles(sdkDirectory, "*manifest.xml");
            if (manifestFiles.Length < 1)
                throw new Exception($"No manifest files in {sdkDirectory}");

            string manifestFile = Directory.GetFiles(sdkDirectory, "*manifest.xml")[0];

            List<VendorSample> vsl = new List<VendorSample>();

            XmlDocument doc = new XmlDocument();
            doc.Load(manifestFile);
            string boardName = doc.SelectSingleNode("//boards/board").Attributes?.GetNamedItem("name")?.Value;

            List<MCU> mcus = new List<MCU>();
            List<MCUFamily> families = new List<MCUFamily>();
            List<ParsedComponent> allFrameworks = new List<ParsedComponent>();

            foreach (XmlNode devNode in doc.SelectNodes("//devices/device"))
            {
                var mcuFamily = new MCUFamily
                {
                    ID = devNode.Attributes?.GetNamedItem("full_name")?.Value,
                    UserFriendlyName = devNode.Attributes?.GetNamedItem("name")?.Value,
                };

                int FLASHSize, RAMSize;

                int.TryParse((devNode.SelectSingleNode("memory/@flash_size_kb")?.Value ?? ""), out FLASHSize);
                int.TryParse((devNode.SelectSingleNode("memory/@ram_size_kb")?.Value ?? ""), out RAMSize);
                FLASHSize *= 1024;
                RAMSize *= 1024;
                string coreName = devNode.SelectSingleNode("core/@name")?.Value;
                CoreFlagHelper.AddCoreSpecificFlags(true, mcuFamily, coreName);

                families.Add(mcuFamily);
                string svdFile = null;

                //Map each component to an instance of EmbeddedFramework
                foreach (XmlNode componentNode in doc.SelectNodes($"//components/component"))
                {
                    string componentName = componentNode.SelectSingleNode("@name")?.Value ?? "";
                    string componentType = componentNode.SelectSingleNode("@type")?.Value ?? "";
                    string device = componentNode.SelectSingleNode("@device")?.Value ?? "";

                    switch (componentType)
                    {
                        case "documentation":
                            continue;
                        case "debugger":
                        case "linker":
                            {
                                List<string> relPaths = new List<string>();
                                bool isDebug = componentType == "debugger";
                                string sourceType = isDebug ? "debug" : "linker";
                                foreach (XmlElement fileNode in componentNode.SelectNodes($"source[@type='{sourceType}']/files"))
                                {
                                    string relPath = fileNode.ParentNode.SelectSingleNode("@path")?.Value + "/" + fileNode.SelectSingleNode("@mask")?.Value;
                                    try
                                    {
                                        if (File.Exists(Path.Combine(sdkDirectory, relPath)))
                                            relPaths.Add(relPath);
                                    }
                                    catch { }
                                }

                                if (relPaths.Count > 0)
                                {
                                    if (isDebug)
                                        svdFile = Path.Combine(sdkDirectory, relPaths[0]);
                                    else
                                    {
                                        if (relPaths.Count == 1)
                                            mcuFamily.CompilationFlags.LinkerScript = "$$SYS:BSP_ROOT$$/" + relPaths[0];
                                        else
                                        {
                                            const string optionID = "com.sysprogs.imported.ksdk2x.linker_script";
                                            mcuFamily.CompilationFlags.LinkerScript = $"$$SYS:BSP_ROOT$$/$${optionID}$$";
                                            mcuFamily.ConfigurableProperties.PropertyGroups[0].Properties.Add(new PropertyEntry.Enumerated
                                            {
                                                UniqueID = optionID,
                                                Name = "Linker script",
                                                AllowFreeEntry = false,
                                                SuggestionList = relPaths.Select(p => new PropertyEntry.Enumerated.Suggestion { InternalValue = p, UserFriendlyName = Path.GetFileName(p) }).ToArray()
                                            });
                                        }
                                    }
                                }
                            }
                            continue;
                    }

                    List<string> headerFiles = new List<string>();
                    List<string> includeDirectories = new List<string>();
                    List<string> sourceFiles = new List<string>();

                    foreach (XmlNode src in componentNode.SelectNodes("source"))
                    {
                        string sourcePath = src.Attributes.GetNamedItem("path").Value;
                        sourcePath = sourcePath.Replace("$|device|", mcuFamily.UserFriendlyName);
                        string fullPathPrefix = $"$$SYS:BSP_ROOT$$/{sourcePath}/";
                        string itemType = src.Attributes?.GetNamedItem("type")?.Value;
                        foreach (XmlNode fsrc in src.SelectNodes("files"))
                        {
                            string name = fsrc.SelectSingleNode("@mask").Value;
                            if (!string.IsNullOrEmpty(coreName))
                                name = name.Replace("$|core|", coreName);
                            string[] items;
                            if (name.Contains("*"))
                            {
                                try
                                {
                                    items = Directory.GetFiles(Path.Combine(sdkDirectory, sourcePath), name).Select(f => Path.GetFileName(f)).ToArray();
                                }
                                catch
                                {
                                    items = new string[0];
                                }
                            }
                            else
                                items = new[] { name };

                            if (itemType == "c_include")
                                includeDirectories.Add(fullPathPrefix.TrimEnd('/'));

                            foreach (var item in items)
                            {
                                if (itemType == "src" || itemType == "asm_include")
                                    sourceFiles.Add(fullPathPrefix + item);
                                else if (itemType == "c_include")
                                {
                                    headerFiles.Add(fullPathPrefix + item);
                                }
                            }
                        }
                    }

                    string fwPrefix = "com.sysprogs.ksdk2x_imported.";

                    string[] dependencyList = componentNode.Attributes?.GetNamedItem("dependency")?.Value?.Split(' ')
                        ?.Where(p => !(p.EndsWith("_startup") || p.EndsWith("test")))
                        ?.Select(id => fwPrefix + id)
                        ?.ToArray() ?? new string[0];

                    EmbeddedFramework fw = new EmbeddedFramework
                    {
                        ID = $"{fwPrefix}{componentName}",
                        UserFriendlyName = $"{componentName} ({componentType})",
                        AdditionalSourceFiles = sourceFiles.Distinct().ToArray(),
                        AdditionalHeaderFiles = headerFiles.Distinct().ToArray(),
                        RequiredFrameworks = dependencyList,
                        AdditionalIncludeDirs = includeDirectories.Distinct().ToArray(),
                    };

                    if (string.IsNullOrEmpty(fw.ID))
                    {
                        sink.LogWarning($"Found a framework with empty ID. Skipping...");
                        continue;
                    }

                    if (string.IsNullOrEmpty(fw.UserFriendlyName))
                        fw.UserFriendlyName = fw.ID;

                    allFrameworks.Add(new ParsedComponent { Framework = fw, OriginalType = componentNode?.SelectSingleNode("@type").Value });
                }

                HashSet<string> frameworkIDs = new HashSet<string>();
                foreach(var fw in allFrameworks)
                {
                    if (frameworkIDs.Contains(fw.Framework.ID))
                        sink.LogWarning("Duplicate framework: " + fw.Framework.ID);

                    frameworkIDs.Add(fw.Framework.ID);
                }

                EmbeddedFramework templateFr = allFrameworks.Where(fr => (fr.Framework.UserFriendlyName.Contains(boardName))).SingleOrDefault()?.Framework;
                if (templateFr == null)
                {
                    sink.LogWarning($"manifest has not project_template");
                    continue;
                }

                foreach (XmlNode packageNode in devNode.SelectNodes($"package/@name"))
                {
                    string pkgName = packageNode?.Value;
                    if (string.IsNullOrEmpty(pkgName))
                        continue;

                    mcus.Add(new MCU
                    {
                        ID = pkgName,
                        UserFriendlyName = pkgName,
                        FamilyID = mcuFamily.ID,
                        FLASHSize = FLASHSize,
                        RAMSize = RAMSize,
                        CompilationFlags = new ToolFlags
                        {
                            PreprocessorMacros = new string[] { "CPU_" + pkgName }
                        }
                    });
                }
            }

            if (families.Count == 0)
                throw new Exception("The selected KSDK contains no families");

            return new ParsedSDK
            {
                BSP = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.imported.ksdk2x." + families[0].ID,
                    PackageDescription = "Imported KSDK 2.x for " + families[0].ID,
                    PackageVersion = "1.0",
                    GNUTargetID = "arm-eabi",
                    Frameworks = allFrameworks.Select(f => f.Framework).ToArray(),
                    MCUFamilies = families.ToArray(),
                    SupportedMCUs = mcus.ToArray(),
                }
            };
        }

        class ConsoleWarningSink : IWarningSink
        {
            public void LogWarning(string warning)
            {
                Console.WriteLine("Warning: " + warning);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: KSDK2xImporter <KSDK directory>");

            string sdkDir = args[0];
            var bsp = ParseKSDKManifest(args[0], new ConsoleWarningSink());
            bsp.Save(args[0]);

            /*var sampleDir = BuildOrLoadSampleDirectory(SDKdir, outputDir, sampleListFile);
            if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
            {
                StandaloneBSPValidator.Program.TestVendorSamples(sampleDir, bspDir, tempDir);
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }*/
        }

        /*        private static ConstructedVendorSampleDirectory BuildOrLoadSampleDirectory(string SDKdir, string outputDir, string sampleListFile)
                {
                    ConstructedVendorSampleDirectory sampleDir;

                    if (Directory.Exists(outputDir))
                        Directory.Delete(outputDir, true);
                    Directory.CreateDirectory(outputDir);

                    var samples = ParseKSDKManifest(SDKdir);
                    sampleDir = new ConstructedVendorSampleDirectory
                    {
                        SourceDirectory = SDKdir,
                        Samples = samples.ToArray(),
                    };

                    XmlTools.SaveObject(sampleDir, sampleListFile);
                    return sampleDir;
                }*/
    }
}
