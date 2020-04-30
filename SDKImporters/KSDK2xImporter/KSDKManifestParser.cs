using BSPEngine;
using BSPGenerationTools;
using KSDK2xImporter.HelperTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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

            List<SpecializedDevice> _SpecializedDevice;
            ParsedDefine[] _GlobalDefines;
            ParsedComponent[] _Components;

            void LoadDevicesAndFamilies()   //Sets _ConstructedDevices and _AllFamilies
            {
                _SpecializedDevice = new List<SpecializedDevice>();

                foreach (XmlElement devNode in _Manifest.DocumentElement.SelectNodes("devices/device"))
                {
                    var dev = new ParsedDevice(devNode);
                    if (string.IsNullOrEmpty(dev.DeviceName) || string.IsNullOrEmpty(dev.FullName) || dev.PackageNames.Length == 0 || dev.Cores.Length == 0)
                    {
                        _Sink.LogWarning("Incomplete device definition: " + dev.DeviceName);
                        continue;
                    }

                    foreach(var core in dev.Cores)
                        _SpecializedDevice.Add(new SpecializedDevice(dev, core));
                }
            }

            void AttachSVDFilesAndLinkerScriptsToDevices()
            {
                foreach(var sd in _SpecializedDevice)
                {
                    if (_Components.FirstOrDefault(c => c.Type == ComponentType.SVDFile && c.Filter.MatchesDevice(sd)) is ParsedComponent svdComponent)
                    {
                        var svdFiles = svdComponent.LocateAllFiles(sd, _Directory).ToArray();
                        Debug.Assert(svdFiles.Length <= 1); //If we get multiple matching SVD files, we might have skipped some conditions.

                        if (svdFiles.Length > 0)
                        {
                            try
                            {
                                var fullPath = svdFiles[0].GetLocalPath(_Directory);

                                var mcuDef = SVDParser.ParseSVDFile(fullPath, sd.Device.DeviceName);
                                var convertedFile = new FileReference(Path.ChangeExtension(svdFiles[0].RelativePath, ".vgdbdevice"), svdFiles[0].Type);

                                XmlSerializer ser = new XmlSerializer(typeof(MCUDefinition));
                                using (var fs = File.Create(Path.ChangeExtension(convertedFile.GetLocalPath(_Directory), ".vgdbdevice.gz")))
                                using (var gs = new GZipStream(fs, CompressionMode.Compress, true))
                                    ser.Serialize(gs, new MCUDefinition(mcuDef));

                                sd.ConvertedSVDFile = convertedFile;
                            }
                            catch (Exception ex)
                            {
                                _Sink.LogWarning($"Failed to process {svdFiles[0]}: {ex.Message}");
                            }
                        }
                    }

                    sd.DiscoveredLinkerScripts = _Components.Where(c => c.Type == ComponentType.LinkerScript && c.Filter.MatchesDevice(sd))
                        .SelectMany(c => c.LocateAllFiles(sd, _Directory)).ToArray();
                }
            }

            public ParsedSDK ParseKSDKManifest()
            {
                LoadDevicesAndFamilies();

                _GlobalDefines = _Manifest.DocumentElement.SelectNodes("defines/define").OfType<XmlElement>().Select(e => new ParsedDefine(e)).ToArray();
                _Components = _Manifest.DocumentElement.SelectNodes($"components/component").OfType<XmlElement>()
                    .Select(n => new ParsedComponent(n))
                    .Where(c => !c.SkipUnconditionally)
                    .ToArray();

                AttachSVDFilesAndLinkerScriptsToDevices();

#if !DEBUG
                if (families.Count == 0)
                    throw new Exception("The selected KSDK contains no families");

                List<VendorSample> samples = new List<VendorSample>();

                foreach (XmlElement boardNode in _Manifest.DocumentElement.SelectNodes("boards/board"))
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

                var version = _Manifest.DocumentElement.GetAttribute("version");
                if (version == "")
                    version = "unknown";

                var allFamilies = _SpecializedDevice.Select(d => d.BuildMCUFamily()).ToArray();

                return new ParsedSDK
                {
                    BSP = new BoardSupportPackage
                    {
                        PackageID = "com.sysprogs.imported.ksdk2x." + allFamilies[0].ID,
                        PackageDescription = "Imported MCUXpresso SDK for " + allFamilies[0].ID,
                        PackageVersion = version,
                        GNUTargetID = "arm-eabi",
                        //Frameworks = allFrameworks.Where(f => f.OriginalType != "project_template").Select(f => f.Framework).ToArray(),
                        MCUFamilies = allFamilies.ToArray(),
                        SupportedMCUs = _SpecializedDevice.SelectMany(d => d.Complete(_GlobalDefines)).ToArray(),
                        //FileConditions = allConditions.ToArray(),
                        VendorSampleCatalogName = "MCUXpresso Samples",
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
