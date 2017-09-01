using BSPEngine;
using BSPGenerationTools;
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
        class ParsedDevice
        {
            public readonly string DeviceName;
            public readonly string SDKDirectory;
            public readonly string FullName;
            public readonly string CoreName;

            public ParsedDevice(XmlElement devNode, string sdkDir)
            {
                SDKDirectory = sdkDir;
                FullName = devNode.GetAttribute("full_name");
                DeviceName = devNode.GetAttribute("name");

                CoreName = devNode.SelectSingleNode("core/@type")?.Value ?? //KSDK2.2
                    devNode.SelectSingleNode("core/@name")?.Value; //KSDK2.0
            }

            internal MCUFamily ToMCUFamily()
            {
                var mcuFamily = new MCUFamily
                {
                    ID = FullName,
                    UserFriendlyName = DeviceName
                };

                CoreFlagHelper.AddCoreSpecificFlags(true, mcuFamily, CoreName);
                return mcuFamily;
            }
        }

        class ParsedSource
        {
            private ParsedDevice _Device;
            private XmlElement _Element;
            private string _Path;
            public string TargetPath;
            public bool Exclude;
            public readonly string Type;
            public CopiedFile CopiedFiles;

            public ParsedSource(XmlElement e, ParsedDevice dev)
            {
                _Element = e;
                _Device = dev;
                _Path = ExpandVariables(e.GetAttribute("path") ?? "");

                Exclude = (e.GetAttribute("exclude") ?? "false") == "true";
                Type = e.GetAttribute("type") ?? "";
                var comp = e.GetAttribute("compiler") ?? "";
                if (comp.Contains("compiler") && !comp.Contains("gcc"))
                    Exclude = true;
                if (comp.Contains("toolchain") && !comp.Contains("gcc"))
                    Exclude = true;
                var core = e.GetAttribute("core") ?? "";
                if (core != "" && core != dev.CoreName)
                    Exclude = true;
                TargetPath = ExpandVariables(e.GetAttribute("target_path") ?? "");
                if (TargetPath != "")
                    CopiedFiles = new CopiedFile { SourcePath = "$$SYS:BSP_ROOT$$/" + _Path, TargetPath = TargetPath };
            }

            public struct FileReference
            {
                public string RelativePath;
                public string FullPath;

                public string BSPPath => "$$SYS:BSP_ROOT$$/" + RelativePath.Replace('\\', '/');
            }

            string ExpandVariables(string str)
            {
                if (str == null || !str.Contains("$"))
                    return str;

                str = str.Replace("$|device_full_name|", _Device.FullName);
                str = str.Replace("$|device|", _Device.DeviceName);
                str = str.Replace("$|compiler|", "GCC");
                str = str.Replace("$|core|", _Device.CoreName);
                return str;
            }

            public IEnumerable<FileReference> AllFiles
            {
                get
                {
                    bool Exc = false;
                    var toolchain = _Element.GetAttribute("toolchain") ?? "";
                    if (toolchain != "" && !toolchain.Contains("armgcc"))
                        Exc = true;
                    foreach (XmlAttribute maskAttr in _Element.SelectNodes("files/@mask"))
                    {
                        if (Exc)
                            continue;
                        string mask = maskAttr.Value;
                        string[] items;
                        mask = ExpandVariables(mask);
                        if (mask.Contains("|") || _Path.Contains("|"))
                            continue;

                        try
                        {
                            if (mask.Contains("*"))
                                items = Directory.GetFiles(Path.Combine(_Device.SDKDirectory, _Path), mask).Select(f => Path.GetFileName(f)).ToArray();
                            else
                                items = new string[] { mask };
                        }
                        catch
                        {
                            items = new string[0];
                        }

                        foreach (var item in items)
                        {
                            string fullPath;
                            try
                            {
                                fullPath = Path.Combine(_Device.SDKDirectory, _Path + "/" + item);
                                if (!File.Exists(fullPath))
                                    continue;
                            }
                            catch
                            {
                                continue;
                            }

                            yield return new FileReference { RelativePath = _Path + "/" + item, FullPath = fullPath };
                        }
                    }
                }
            }

            public string BSPPath => "$$SYS:BSP_ROOT$$/" + _Path.Replace('\\', '/');
        }

        public class ParsedSDK
        {
            public BoardSupportPackage BSP;
            public VendorSampleDirectory VendorSampleDirectory;

            public const string MainFileName = "main.c";

            public void Save(string directory)
            {
                BSP.VendorSampleDirectoryPath = ".";
                XmlTools.SaveObject(VendorSampleDirectory, Path.Combine(directory, "VendorSamples.XML"));
                XmlTools.SaveObject(BSP, Path.Combine(directory, "BSP.XML"));

                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KSDK2xImporter." + MainFileName))
                {
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    File.WriteAllBytes(Path.Combine(directory, MainFileName), data);
                }
            }
        }

        class ParsedComponent
        {
            public EmbeddedFramework Framework;
            public string OriginalName;
            public string OriginalType;

            public override string ToString()
            {
                return Framework?.UserFriendlyName;
            }

            public EmbeddedProjectSample ToProjectSample(IEnumerable<string> extraReferences)
            {
                return new EmbeddedProjectSample
                {
                    AdditionalSourcesToCopy = Framework.AdditionalSourceFiles
                        .Select(f => new AdditionalSourceFile { SourcePath = f, TargetFileName = Path.GetFileName(f) })
                        .Concat(new[] { new AdditionalSourceFile { SourcePath = "$$SYS:BSP_ROOT$$/" + ParsedSDK.MainFileName, TargetFileName = ParsedSDK.MainFileName } })
                        .ToArray(),
                    AdditionalIncludeDirectories = Framework.AdditionalIncludeDirs,
                    PreprocessorMacros = Framework.AdditionalPreprocessorMacros,
                    RequiredFrameworks = LoadedBSP.Combine(Framework.RequiredFrameworks, extraReferences?.ToArray()),
                    Name = "Empty project for " + OriginalName,
                };
            }
        }

        struct ParsedDefine
        {
            public string Name, Value;

            public ParsedDefine(XmlElement el)
            {
                Name = el.GetAttribute("name");
                Value = el.GetAttribute("value");
            }

            public string Definition
            {
                get
                {
                    if (string.IsNullOrEmpty(Value))
                        return Name;
                    else
                        return Name + "=" + Value;
                }
            }
        }
        public class ListDictionary<TKey, TValue> : Dictionary<TKey, List<TValue>>
        {
            public void Add(TKey key, TValue value)
            {
                if (ContainsKey(key))
                    this[key].Add(value);
                else
                    Add(key, new List<TValue> { value });
            }
        }

        public static ParsedSDK ParseKSDKManifest(string sdkDirectory, IWarningSink sink)
        {
            string[] manifestFiles = Directory.GetFiles(sdkDirectory, "*manifest.xml");
            if (manifestFiles.Length < 1)
                throw new Exception($"No manifest files in {sdkDirectory}");

            string manifestFile = Directory.GetFiles(sdkDirectory, "*manifest.xml")[0];

            List<VendorSample> vsl = new List<VendorSample>();

            XmlDocument doc = new XmlDocument();
            doc.Load(manifestFile);

            List<MCU> mcus = new List<MCU>();
            List<MCUFamily> families = new List<MCUFamily>();
            List<ParsedComponent> allFrameworks = new List<ParsedComponent>();
            bool linkerScriptHandled = false;

            List<string> allFiles = new List<string>();
            Dictionary<string, ParsedDevice> deviceDict = new Dictionary<string, ParsedDevice>();
            Dictionary<string, EmbeddedFramework> frameworkDict = new Dictionary<string, EmbeddedFramework>();
            List<FileCondition> allConditions = new List<FileCondition>();
            string fwPrefix = "com.sysprogs.ksdk2x_imported.";
            HashSet<string> alwaysIncludedFrameworks = new HashSet<string>();
            // HashSet<string> alwaysExcludedFrameworks = new HashSet<string>();
            List<string> lstdevAll = new List<string>();
            List<CopiedFile> cfs = new List<CopiedFile>();
            var dictCopiedFile = new ListDictionary<string, CopiedFile>();
            var dictAddIncludeDir = new ListDictionary<string, string>();


            foreach (XmlElement devNode in doc.SelectNodes("//devices/device"))
            {
                lstdevAll.Add(new ParsedDevice(devNode, sdkDirectory).DeviceName);
            }

            foreach (XmlElement devNode in doc.SelectNodes("//devices/device"))
            {
                ParsedDevice dev = new ParsedDevice(devNode, sdkDirectory);

                var mcuFamily = dev.ToMCUFamily();

                int FLASHSize, RAMSize;
                int.TryParse((devNode.SelectSingleNode("memory/@flash_size_kb")?.Value ?? ""), out FLASHSize);
                int.TryParse((devNode.SelectSingleNode("memory/@ram_size_kb")?.Value ?? ""), out RAMSize);
                FLASHSize *= 1024;
                RAMSize *= 1024;

                families.Add(mcuFamily);
                string svdFile = null;

                //Map each component to an instance of EmbeddedFramework
                foreach (XmlNode componentNode in doc.SelectNodes($"//components/component"))
                {
                    string componentName = componentNode.SelectSingleNode("@name")?.Value ?? "";
                    string componentType = componentNode.SelectSingleNode("@type")?.Value ?? "";
                    string device = componentNode.SelectSingleNode("@device")?.Value ?? "";
                    string idComponent = componentNode.SelectSingleNode("@id")?.Value ?? "";
                    device = idComponent.Split('.').Last();

                    if (device != dev.DeviceName && (lstdevAll.Contains(device)))
                        continue;
                    switch (componentType)
                    {
                        case "documentation":
                        case "SCR":
                        case "EULA":
                            continue;
                        case "debugger":
                        case "linker":
                            {
                                List<string> relPaths = new List<string>();
                                bool isDebug = componentType == "debugger";
                                string sourceType = isDebug ? "debug" : "linker";
                                foreach (var src in componentNode.SelectNodes($"source[@type='{sourceType}' and @toolchain='armgcc']").OfType<XmlElement>().Select(e => new ParsedSource(e, dev)))
                                {
                                    foreach (var fn in src.AllFiles)
                                    {
                                        relPaths.Add(fn.RelativePath);
                                    }
                                }

                                if (relPaths.Count > 0)
                                {
                                    if (isDebug)
                                        svdFile = relPaths[0];
                                    else if (!linkerScriptHandled)
                                    {
                                        linkerScriptHandled = true;
                                        if (relPaths.Count == 1)
                                            mcuFamily.CompilationFlags.LinkerScript = "$$SYS:BSP_ROOT$$/" + relPaths[0];
                                        else
                                        {
                                            const string optionID = "com.sysprogs.imported.ksdk2x.linker_script";
                                            mcuFamily.CompilationFlags.LinkerScript = $"$$SYS:BSP_ROOT$$/$${optionID}$$";
                                            if ((mcuFamily.ConfigurableProperties?.PropertyGroups?.Count ?? 0) == 0)
                                                mcuFamily.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };

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
                        case "CMSIS":

                            //KSDK 2.x defines a Include_xxx framework for each possible CMSIS core. Those frameworks are redundant (normal 'Include' framework references the same include path) and should be removed to avoid confusion.
                            if (componentName.StartsWith("Include_"))
                                continue;
                            if (idComponent == "platform.CMSIS_Driver")
                                continue;

                            if (componentName == "Include")
                                alwaysIncludedFrameworks.Add(fwPrefix + componentName);//!!! +"."+dev.DeviceName);

                            if (idComponent == "platform.CMSIS")
                                alwaysIncludedFrameworks.Add(fwPrefix + idComponent);//!!! + "." + dev.DeviceName);
                            break;
                        case "project_template":
                            continue;
                        default:
                            break;
                    }

                    List<string> headerFiles = new List<string>();
                    List<string> includeDirectories = new List<string>();
                    List<string> sourceFiles = new List<string>();
                    List<string> libFiles = new List<string>();

                    var IDFr = fwPrefix + idComponent; 

                    foreach (ParsedSource src in componentNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSource(e, dev)))
                    {

                        if (src.Exclude)
                            continue;

                        if (src.Type == "c_include")
                            includeDirectories.Add(src.BSPPath);

                        foreach (var file in src.AllFiles)
                        {
                            if (file.BSPPath.EndsWith("ucosii.c") && !componentName.Contains("ucosii"))
                                continue;
                            if (file.BSPPath.EndsWith("ucosiii.c") && !componentName.Contains("ucosiii"))
                                continue;

                            if (file.BSPPath.Contains("freertos"))
                                allConditions.Add(new FileCondition
                                {
                                    FilePath = file.BSPPath,
                                    ConditionToInclude = new Condition.ReferencesFramework
                                    {
                                        FrameworkID = fwPrefix + "middleware.freertos." + dev.DeviceName
                                    }
                                });

                            if (src.TargetPath != "")
                            {
                                dictCopiedFile.Add(IDFr, new CopiedFile { SourcePath = file.BSPPath, TargetPath = src.TargetPath + "/" + Path.GetFileName(file.BSPPath) });
                                foreach (XmlElement patch in componentNode.SelectNodes("include_paths/include_path"))
                                    dictAddIncludeDir.Add(IDFr, patch.GetAttribute("path"));
                            }
                            if (src.Type == "lib")
                                libFiles.Add(file.BSPPath);

                            if (src.Type == "src" || src.Type == "asm_include")
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
                        ProjectFolderName = componentName,
                        AdditionalSourceFiles = sourceFiles.Distinct().ToArray(),
                        AdditionalHeaderFiles = headerFiles.Distinct().ToArray(),
                        RequiredFrameworks = dependencyList,
                        AdditionalIncludeDirs = includeDirectories.Distinct().ToArray(),
                        AdditionalLibraries = libFiles.ToArray(),
                        AdditionalPreprocessorMacros = componentNode.SelectNodes("defines/define").OfType<XmlElement>().Select(el => new ParsedDefine(el).Definition).ToArray(),
                    };

                    if (componentName == "freertos" && componentType == "OS")
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
                        sink.LogWarning("Duplicate framework for " + fw.ID);
                        continue;
                    }

                    frameworkDict[fw.ID] = fw;

                    if (string.IsNullOrEmpty(fw.ID))
                    {
                        sink.LogWarning($"Found a framework with empty ID. Skipping...");
                        continue;
                    }

                    if (string.IsNullOrEmpty(fw.UserFriendlyName))
                        fw.UserFriendlyName = fw.ID;

                    allFrameworks.Add(new ParsedComponent { Framework = fw, OriginalType = componentType, OriginalName = componentName });
                    allFiles.AddRange(sourceFiles);
                    allFiles.AddRange(headerFiles);
                }

                string deviceDefinitionFile = null;
                if (svdFile != null)
                {
                    try
                    {
                        var mcuDef = SVDParser.ParseSVDFile(Path.Combine(sdkDirectory, svdFile), dev.DeviceName);
                        deviceDefinitionFile = Path.ChangeExtension(svdFile, ".vgdbdevice");

                        XmlSerializer ser = new XmlSerializer(typeof(MCUDefinition));
                        using (var fs = File.Create(Path.Combine(sdkDirectory, Path.ChangeExtension(svdFile, ".vgdbdevice.gz"))))
                        using (var gs = new GZipStream(fs, CompressionMode.Compress, true))
                            ser.Serialize(gs, new MCUDefinition(mcuDef));
                    }
                    catch (Exception ex)
                    {
                        sink.LogWarning($"Failed to parse {svdFile}: {ex.Message}");
                    }
                }

                foreach (XmlNode packageNode in devNode.SelectNodes($"package/@name"))
                {
                    string pkgName = packageNode?.Value;
                    if (string.IsNullOrEmpty(pkgName))
                        continue;

                    deviceDict[pkgName] = dev;

                    mcus.Add(new MCU
                    {
                        ID = pkgName,
                        UserFriendlyName = $"{pkgName} (KSDK 2.x)",
                        FamilyID = mcuFamily.ID,
                        FLASHSize = FLASHSize,
                        RAMSize = RAMSize,
                        CompilationFlags = new ToolFlags
                        {
                            PreprocessorMacros = new string[] { "CPU_" + pkgName },
                        },

                        MCUDefinitionFile = deviceDefinitionFile
                    });
                }
            }

            if (families.Count == 0)
                throw new Exception("The selected KSDK contains no families");

            List<VendorSample> samples = new List<VendorSample>();

            foreach (XmlElement boardNode in doc.SelectNodes("//boards/board"))
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
                                var sampleFiles = Directory.GetFiles(Path.Combine(sdkDirectory, path), mask);
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


                    foreach (var src in exampleNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSource(e, dev)))
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

            return new ParsedSDK
            {
                BSP = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.imported.ksdk2x." + families[0].ID,
                    PackageDescription = "Imported KSDK 2.x for " + families[0].ID,
                    PackageVersion = doc.SelectSingleNode("//ksdk/@version")?.Value ?? "unknown",
                    GNUTargetID = "arm-eabi",
                    Frameworks = allFrameworks.Where(f => f.OriginalType != "project_template").Select(f => f.Framework).ToArray(),
                    MCUFamilies = families.ToArray(),
                    SupportedMCUs = mcus.ToArray(),
                    FileConditions = allConditions.ToArray(),
                    VendorSampleCatalogName = "KSDK Samples",
                    EmbeddedSamples = allFrameworks.Where(f => f.OriginalType == "project_template").Select(f => f.ToProjectSample(alwaysIncludedFrameworks)).ToArray(),
                },

                VendorSampleDirectory = new VendorSampleDirectory
                {
                    Samples = samples.ToArray()
                }
            };
        }

        public string GenerateBSPForSDK(string directory, IWarningSink sink)
        {
            var bsp = ParseKSDKManifest(directory, sink);
            bsp.Save(directory);

            return bsp.BSP.PackageID;
        }

        public string Name => "Kinetis KSDK 2.x";
        public string CommandName => "Import a Kinetis KSDK 2.x";
        public string Target => "arm-eabi";
        public string OpenFileFilter => "KSDK Manifest Files|*manifest.xml";

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
    }
}
