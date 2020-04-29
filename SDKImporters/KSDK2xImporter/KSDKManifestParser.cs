using BSPEngine;
using BSPGenerationTools;
using KSDK2xImporter.HelperTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace KSDK2xImporter
{
    public class KSDKManifestParser : ISDKImporter
    {
        public class ParserImpl
        {
            readonly string _Directory;
            readonly XmlDocument _Manifest;
            readonly IWarningSink _Sink;

            const string FrameworkIDPrefix = "com.sysprogs.ksdk2x_imported.";

            public ParserImpl(string sdkDirectory, XmlDocument doc, IWarningSink sink)
            {
                _Directory = sdkDirectory;
                _Manifest = doc;
                _Sink = sink;
            }

#if !DEBUG
            List<ParsedComponent> LoadComponents()
            {
                List<ParsedComponent> result = new List<ParsedComponent>();

                //Map each component to an instance of EmbeddedFramework
                foreach (var componentNode in _Manifest.SelectNodes($"//components/component").OfType<XmlElement>())
                {
                    string componentName = componentNode.GetAttribute("name");
                    string componentType = componentNode.GetAttribute("type");

                    var filter = new ParsedFilter(componentNode);
                    if (filter.SkipUnconditionally)
                        continue;

                    ComponentType type;

                    switch (componentType)
                    {
                        case "documentation":
                        case "SCR":
                        case "EULA":
                        case "project_template":
                            continue;
                        case "debugger":
                            type = ComponentType.SVDFile;
                            break;
                        case "linker":
                            type = ComponentType.LinkerScript;
                            break;
                        case "CMSIS":
                            type = ComponentType.CMSIS_SDK;
                            break;
                        default:
                            break;
                    }

                    List<string> headerFiles = new List<string>();
                    List<string> includeDirectories = new List<string>();
                    List<string> sourceFiles = new List<string>();
                    List<string> libFiles = new List<string>();

                    foreach (ParsedSourceList src in componentNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSourceList(e)))
                    {
                        if (src.Filter.SkipUnconditionally)
                            continue;

                        if (src.Type == "c_include")
                            includeDirectories.Add(src.BSPPath);

                        foreach (var file in src.AllFiles)
                        {
                            if (file.BSPPath.EndsWith("ucosii.c") && !componentName.Contains("ucosii"))
                                continue;
                            if (file.BSPPath.EndsWith("ucosiii.c") && !componentName.Contains("ucosiii"))
                                continue;

                            if (file.BSPPath.Contains("freertos") || src.Condition.Contains("freertos"))
                            {
                                allConditions.Add(new FileCondition
                                {
                                    FilePath = file.BSPPath,
                                    ConditionToInclude = new Condition.ReferencesFramework
                                    {
                                        FrameworkID = fwPrefix + freeRTOSComponentID
                                    }
                                });
                            }
                            else if (src.Condition.Contains(".baremetal."))
                            {
                                allConditions.Add(new FileCondition
                                {
                                    FilePath = file.BSPPath,
                                    ConditionToInclude = new Condition.Not
                                    {
                                        Argument = new Condition.ReferencesFramework
                                        {
                                            FrameworkID = fwPrefix + freeRTOSComponentID
                                        }
                                    }
                                });
                            }

                            if (src.TargetPath != "")
                            {
                                dictCopiedFile.Add(IDFr, new CopiedFile { SourcePath = file.BSPPath, TargetPath = src.TargetPath + "/" + Path.GetFileName(file.BSPPath) });
                                foreach (XmlElement patch in componentNode.SelectNodes("include_paths/include_path"))
                                    dictAddIncludeDir.Add(IDFr, patch.GetAttribute("path"));
                            }

                            if (src.Type == "lib")
                                libFiles.Add(file.BSPPath);
                            else if (src.Type == "src" || src.Type == "asm_include")
                                sourceFiles.Add(file.BSPPath);
                            else if (src.Type == "c_include")
                                headerFiles.Add(file.BSPPath);
                        }
                    }

                    foreach (XmlElement patch in componentNode.SelectNodes("include_paths/include_path"))
                        includeDirectories.Add(patch.GetAttribute("path"));

                    string[] dependencyList = componentNode.Attributes?.GetNamedItem("dependency")?.Value?.Split(' ')
                        ?.Select(id => fwPrefix + id)
                        ?.ToArray() ?? new string[0];

                    var FilterRegex = device.Length > 5 ? $"^{device.Substring(0, 5)}.*" : $"^{device}.*"; //MK02F MK22F
                    if (device.Length == 0)
                        FilterRegex = "";

                    EmbeddedFramework fw = new EmbeddedFramework
                    {
                        ID = $"{IDFr}",
                        MCUFilterRegex = FilterRegex,
                        UserFriendlyName = $"{componentName} ({componentType})",
                        ProjectFolderName = $"{componentName}-{componentType}",
                        AdditionalSourceFiles = sourceFiles.Distinct().ToArray(),
                        AdditionalHeaderFiles = headerFiles.Distinct().ToArray(),
                        RequiredFrameworks = dependencyList,
                        AdditionalIncludeDirs = includeDirectories.Distinct().ToArray(),
                        AdditionalLibraries = libFiles.ToArray(),
                        AdditionalPreprocessorMacros = componentNode.SelectNodes("defines/define").OfType<XmlElement>().Select(el => new ParsedDefine(el).Definition).ToArray(),
                    };

                    if (usedProjectFolderNames.Contains(fw.ProjectFolderName))
                    {
                        for (int i = 2; i < 10000; i++)
                        {
                            if (!usedProjectFolderNames.Contains(fw.ProjectFolderName + i))
                            {
                                fw.ProjectFolderName += i;
                                break;
                            }
                        }

                    }
                    usedProjectFolderNames.Add(fw.ProjectFolderName);

                    if (componentName.IndexOf("freertos", StringComparison.InvariantCultureIgnoreCase) != -1 &&
                        sourceFiles.FirstOrDefault(s => Path.GetFileName(s).ToLower() == "tasks.c") != null &&
                        componentType == "OS")
                    {
                        fw.AdditionalPreprocessorMacros = LoadedBSP.Combine(fw.AdditionalPreprocessorMacros, "USE_RTOS=1;USE_FREERTOS".Split(';'));
                        fw.ConfigurableProperties = new PropertyList
                        {
                            PropertyGroups = new List<PropertyGroup>()
                            {
                                new PropertyGroup
                                {
                                    Properties = new List<PropertyEntry>()
                                    {
                                        new PropertyEntry.Enumerated
                                        {
                                            Name = "FreeRTOS Heap Implementation",
                                            UniqueID = "com.sysprogs.bspoptions.stm32.freertos.heap",
                                            DefaultEntryIndex = 3,
                                            SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                                            {
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "heap_1", UserFriendlyName = "Heap1 - no support for freeing"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "heap_2", UserFriendlyName = "Heap2 - no block consolidation"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "heap_3", UserFriendlyName = "Heap3 - use newlib malloc()/free()"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "heap_4", UserFriendlyName = "Heap4 - contiguous heap area"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "heap_5", UserFriendlyName = "Heap5 - scattered heap area"},
                                            }
                                        }
                                    }
                                }
                            }
                        };

                        foreach (var fn in fw.AdditionalSourceFiles)
                        {
                            string name = Path.GetFileName(fn);
                            if (name.StartsWith("heap_"))
                            {
                                allConditions.Add(new FileCondition { FilePath = fn, ConditionToInclude = new Condition.Equals { Expression = "$$com.sysprogs.bspoptions.stm32.freertos.heap$$", ExpectedValue = Path.GetFileNameWithoutExtension(fn) } });
                            }
                        }
                    }

                    if (frameworkDict.ContainsKey(fw.ID))
                    {
                        _Sink.LogWarning("Duplicate framework for " + fw.ID);
                        continue;
                    }

                    frameworkDict[fw.ID] = fw;

                    if (string.IsNullOrEmpty(fw.ID))
                    {
                        _Sink.LogWarning($"Found a framework with empty ID. Skipping...");
                        continue;
                    }

                    if (string.IsNullOrEmpty(fw.UserFriendlyName))
                        fw.UserFriendlyName = fw.ID;

                    allFrameworks.Add(new ParsedComponent { Framework = fw, OriginalType = componentType, OriginalName = componentName });
                }


                return result;
            }
#endif

            List<ConstructedBSPDevice> _ConstructedDevices;
            List<MCUFamily> _AllFamilies;
            ParsedDefine[] _GlobalDefines;

            void LoadDevicesAndFamilies()   //Sets _ConstructedDevices and _AllFamilies
            {
                _ConstructedDevices = new List<ConstructedBSPDevice>();
                _AllFamilies = new List<MCUFamily>();

                foreach (XmlElement devNode in _Manifest.SelectNodes("//devices/device"))
                {
                    var dev = new ParsedDevice(devNode);
                    if (string.IsNullOrEmpty(dev.DeviceName) || string.IsNullOrEmpty(dev.FullName) || dev.PackageNames.Length == 0 || dev.Cores.Length == 0)
                    {
                        _Sink.LogWarning("Incomplete device definition: " + dev.DeviceName);
                        continue;
                    }

                    foreach (var core in dev.Cores)
                    {
                        var family = dev.BuildMCUFamily(core);

                        _AllFamilies.Add(family);
                        foreach (var package in dev.PackageNames)
                            _ConstructedDevices.Add(new ConstructedBSPDevice(dev, core, package, family.ID));
                    }
                }
            }

            void LoadGlobalDefines() => _GlobalDefines = _Manifest.SelectNodes("defines/define").OfType<XmlElement>().Select(e => new ParsedDefine(e)).ToArray();

            public ParsedSDK ParseKSDKManifest()
            {
                LoadDevicesAndFamilies();
                LoadGlobalDefines();

                //TODO: Attach SVD files and linker scripts

#if !DEBUG
                if (families.Count == 0)
                    throw new Exception("The selected KSDK contains no families");

                List<VendorSample> samples = new List<VendorSample>();

                foreach (XmlElement boardNode in _Manifest.SelectNodes("//boards/board"))
                {
                    string boardName = boardNode.GetAttribute("name");
                    string deviceID = boardNode.GetAttribute("package");
                    ParsedDevice dev;
                    if (!deviceDict.TryGetValue(deviceID, out dev))
                        continue;

                    foreach (XmlElement directExampleNode in boardNode.SelectNodes("examples/example"))
                    {
                        var exampleNode = directExampleNode;

                        var externalNode = exampleNode.SelectSingleNode("external/files");
                        if (externalNode != null)
                        {
                            var path = (externalNode.ParentNode as XmlElement)?.GetAttribute("path");
                            var mask = (externalNode as XmlElement)?.GetAttribute("mask");
                            if (path != null && mask != null)
                            {
                                {
                                    var sampleFiles = Directory.GetFiles(Path.Combine(_Directory, path), mask);
                                    var fn = sampleFiles?.FirstOrDefault();
                                    if (fn != null)
                                    {
                                        XmlDocument doc2 = new XmlDocument();
                                        doc2.Load(fn);
                                        exampleNode = doc2.DocumentElement.SelectSingleNode("example") as XmlElement;
                                        if (exampleNode == null)
                                            continue;
                                    }

                                }
                            }
                        }

                        List<string> dependencyList = new List<string>(exampleNode.Attributes?.GetNamedItem("dependency")?.Value?.Split(' ')
                            ?.Select(id => fwPrefix + id) ?? new string[0]);

                        var name = exampleNode.GetAttribute("id") ?? "???";

                        dependencyList.AddRange(alwaysIncludedFrameworks);

                        for (int i = 0; i < dependencyList.Count; i++)
                        {
                            EmbeddedFramework fw;
                            if (frameworkDict.TryGetValue(dependencyList[i], out fw) && fw?.RequiredFrameworks != null)
                                dependencyList.AddRange(fw.RequiredFrameworks.Except(dependencyList));
                        }
                        List<string> dependencyList1 = new List<string>(dependencyList.Distinct());
                        List<CopiedFile> CopiedFileForSample = new List<CopiedFile>();
                        List<string> includeDirectories = new List<string>();
                        foreach (var fr1 in dependencyList1)
                        {
                            if (!dictCopiedFile.ContainsKey(fr1))
                                continue;

                            var l = dictCopiedFile[fr1];
                            CopiedFileForSample.AddRange(l);
                            if (dictAddIncludeDir.ContainsKey(fr1))
                                includeDirectories.AddRange(dictAddIncludeDir[fr1]);
                        }
                        List<PropertyDictionary2.KeyValue> CfgEntr = new List<PropertyDictionary2.KeyValue>();

                        string typFpu = "soft";
                        var tth = exampleNode.SelectSingleNode("toolchainSettings/toolchainSetting/option[@id='com.crt.advproject.gcc.fpu']")?.InnerText ?? "soft";

                        if (tth.Contains("hard"))
                            typFpu = "hard";

                        CfgEntr.Add(new PropertyDictionary2.KeyValue
                        {
                            Key = "com.sysprogs.bspoptions.arm.floatmode",
                            Value = "-mfloat-abi=" + typFpu
                        });

                        VendorSample sample = new VendorSample
                        {
                            DeviceID = deviceID,
                            UserFriendlyName = name,
                            BoardName = boardName,
                            Configuration = new VendorSampleConfiguration
                            {
                                Frameworks = dependencyList.Distinct().ToArray(),
                                MCUConfiguration = new PropertyDictionary2 { Entries = CfgEntr.ToArray() }
                            },
                            VirtualPath = exampleNode.GetAttribute("category"),
                            ExtraFiles = CopiedFileForSample.Distinct().ToArray(),

                            NoImplicitCopy = true
                        };

                        List<string> headerFiles = new List<string>();

                        List<string> sourceFiles = new List<string>();
                        foreach (var cf in CopiedFileForSample.Distinct())
                        {
                            includeDirectories.Add(Path.GetDirectoryName(cf.TargetPath));
                        }


                        foreach (var src in exampleNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSourceList(e, dev)))
                        {
                            foreach (var file in src.AllFiles)
                            {
                                if (src.Type == "src" || src.Type == "asm_include")
                                    sourceFiles.Add(file.BSPPath);
                                else if (src.Type == "c_include")
                                    headerFiles.Add(file.BSPPath);
                                if (src.Type == "lib")
                                    sourceFiles.Add(file.BSPPath);

                            }
                        }

                        sample.PreprocessorMacros = exampleNode.SelectNodes("toolchainSettings/toolchainSetting/option[@id='gnu.c.compiler.option.preprocessor.def.symbols']/value").OfType<XmlElement>().
                            Select(node => node.InnerText.Replace("'\"", "'<").Replace("\"'", ">'")).ToArray();

                        sample.SourceFiles = sourceFiles.ToArray();
                        sample.HeaderFiles = headerFiles.ToArray();

                        if (sourceFiles.Count == 0 && headerFiles.Count == 0)
                            continue;

                        string[] matchingComponents = null;

                        foreach (var fn in sourceFiles.Concat(headerFiles))
                        {
                            string[] components = fn.Split('/', '\\');
                            if (matchingComponents == null)
                                matchingComponents = components;
                            else
                            {
                                int matches = CountMatches(matchingComponents, components);
                                if (matches < matchingComponents.Length)
                                    Array.Resize(ref matchingComponents, matches);
                            }
                        }

                        if (matchingComponents != null)
                            sample.Path = string.Join("/", matchingComponents);

                        foreach (var hf in headerFiles)
                        {
                            int c = hf.LastIndexOf('/');
                            includeDirectories.Add(hf.Substring(0, c));
                        }

                        sample.IncludeDirectories = includeDirectories.Distinct().ToArray();
                        samples.Add(sample);
                    }
                }
#endif

                return new ParsedSDK
                {
                    BSP = new BoardSupportPackage
                    {
                        PackageID = "com.sysprogs.imported.ksdk2x." + _AllFamilies[0].ID,
                        PackageDescription = "Imported MCUXpresso SDK for " + _AllFamilies[0].ID,
                        PackageVersion = _Manifest.SelectSingleNode("//ksdk/@version")?.Value ?? "unknown",
                        GNUTargetID = "arm-eabi",
                        //Frameworks = allFrameworks.Where(f => f.OriginalType != "project_template").Select(f => f.Framework).ToArray(),
                        MCUFamilies = _AllFamilies.ToArray(),
                        SupportedMCUs = _ConstructedDevices.Select(d => d.Complete(_GlobalDefines)).ToArray(),
                        //FileConditions = allConditions.ToArray(),
                        VendorSampleCatalogName = "MCUXpresso Samples",
                        //EmbeddedSamples = allFrameworks.Where(f => f.OriginalType == "project_template").Select(f => f.ToProjectSample(alwaysIncludedFrameworks)).ToArray(),
                        BSPImporterID = ID,
                    },

                    VendorSampleDirectory = new VendorSampleDirectory
                    {
                        //Samples = samples.ToArray()
                    }
                };
            }

        }

        public ImportedExternalSDK GenerateBSPForSDK(ImportedSDKLocation location, ISDKImportHost host)
        {
            string[] manifestFiles = Directory.GetFiles(location.Directory, "*manifest*.xml");
            if (manifestFiles.Length < 1)
                throw new Exception($"No manifest files in {location.Directory}");

            string manifestFile = Directory.GetFiles(location.Directory, "*manifest*.xml")[0];

            XmlDocument doc = new XmlDocument();
            doc.Load(manifestFile);

            var bsp = new ParserImpl(location.Directory, doc, host.WarningSink).ParseKSDKManifest();
            bsp.Save(location.Directory);

            return new ImportedExternalSDK { BSPID = bsp.BSP.PackageID };
        }



        public const string ID = "com.sysprogs.sdkimporters.nxp.ksdk";

        public string Name => "MCUXpresso SDK";
        public string UniqueID => ID;
        public string CommandName => "Import an MCUXpresso SDK";
        public string Target => "arm-eabi";
        public string OpenFileFilter => "MCUXpresso SDK Manifest Files|*manifest*.xml";

        static int CountMatches(string[] left, string[] right)
        {
            int limit = Math.Min(left.Length, right.Length);
            for (int i = 0; i < limit; i++)
            {
                if (left[i] != right[i])
                    return i;
            }
            return limit;
        }

        public bool IsCompatibleWithToolchain(LoadedToolchain toolchain)
        {
            var id = toolchain?.Toolchain?.GNUTargetID?.ToLower();
            return id?.Contains("arm") ?? true;
        }
    }
}
