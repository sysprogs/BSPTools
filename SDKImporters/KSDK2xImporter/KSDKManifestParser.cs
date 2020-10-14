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
using System.Text.RegularExpressions;
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

            public ParserImpl(string sdkDirectory, XmlDocument doc, IWarningSink sink)
            {
                _Directory = sdkDirectory;
                _Manifest = doc;
                _Sink = sink;
            }

            List<SpecializedDevice> _SpecializedDevices;
            ParsedDefine[] _GlobalDefines;
            ParsedComponent[] _Components;

            void LoadDevicesAndFamilies()   //Sets _ConstructedDevices and _AllFamilies
            {
                _SpecializedDevices = new List<SpecializedDevice>();

                foreach (XmlElement devNode in _Manifest.DocumentElement.SelectNodes("devices/device"))
                {
                    var dev = new ParsedDevice(devNode);
                    if (string.IsNullOrEmpty(dev.DeviceName) || string.IsNullOrEmpty(dev.FullName) || dev.PackageNames.Length == 0 || dev.Cores.Length == 0)
                    {
                        _Sink.LogWarning("Incomplete device definition: " + dev.DeviceName);
                        continue;
                    }

                    foreach (var core in dev.Cores)
                        _SpecializedDevices.Add(new SpecializedDevice(dev, core));
                }
            }

            void AttachSVDFilesAndLinkerScriptsToDevices(ISDKImportHost host)
            {
                foreach (var sd in _SpecializedDevices)
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

                                var mcuDef = host?.TryParseSVDFile(fullPath, sd.Device.DeviceName) ?? SVDParser.ParseSVDFile(fullPath, sd.Device.DeviceName);
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

            class PerFileContext
            {
                public HashSet<SpecializedDevice> ReferencingDevices = new HashSet<SpecializedDevice>();
                public readonly FileReference File;

                public PerFileContext(FileReference file)
                {
                    File = file;
                }
            }

            private HashSet<string> _AllComponentIDs;
            HashSet<string> _ImplicitlyIncludedFrameworks;

            EmbeddedFramework[] TranslateComponentsToFrameworks(Dictionary<string, FileCondition> fileConditions)
            {
                List<EmbeddedFramework> result = new List<EmbeddedFramework>();
                var usedProjectFolderNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                _AllComponentIDs = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

                _ImplicitlyIncludedFrameworks = new HashSet<string>();

                foreach (var component in _Components)
                    _AllComponentIDs.Add(component.ID);

                foreach (var component in _Components)
                {
                    if (!component.IsSourceComponent || component.SkipUnconditionally)
                        continue;

                    Dictionary<string, PerFileContext> fileContexts = new Dictionary<string, PerFileContext>();
                    List<SpecializedDevice> matchingDevices = new List<SpecializedDevice>();
                    foreach (var dev in _SpecializedDevices)
                    {
                        if (!component.Filter.MatchesDevice(dev))
                            continue;

                        matchingDevices.Add(dev);

                        foreach (var file in component.LocateAllFiles(dev, _Directory))
                        {
                            if (!fileContexts.TryGetValue(file.RelativePath, out var ctx))
                                fileContexts[file.RelativePath] = ctx = new PerFileContext(file);

                            ctx.ReferencingDevices.Add(dev);
                        }
                    }

                    bool foundDeviceDependentFiles = fileContexts.Values.FirstOrDefault(c => c.ReferencingDevices.Count != matchingDevices.Count) != null;

                    string projectFolderName = component.Name;
                    if (usedProjectFolderNames.Contains(projectFolderName))
                    {
                        for (int i = 0; i < 10000; i++)
                            if (!usedProjectFolderNames.Contains(projectFolderName + i))
                            {
                                projectFolderName += i;
                                break;
                            }
                    }

                    usedProjectFolderNames.Add(projectFolderName);

                    if (!foundDeviceDependentFiles)
                    {
                        string deviceNameRegex = null;
                        if (matchingDevices.Count != _SpecializedDevices.Count)
                            deviceNameRegex = "^(" + string.Join("|", matchingDevices.SelectMany(d => d.FinalMCUIDs).ToArray()) + ")$";

                        var fw = component.BuildFramework(_Directory, _Sink, fileConditions, projectFolderName, _AllComponentIDs, deviceNameRegex);
                        result.Add(fw);
                    }
                    else
                    {
                        foreach (var dev in matchingDevices)
                        {
                            var deviceNameRegex = "^(" + string.Join("|", dev.FinalMCUIDs) + ")$";

                            var fw = component.BuildFramework(_Directory, _Sink, fileConditions, projectFolderName, _AllComponentIDs, deviceNameRegex, dev);
                            result.Add(fw);
                        }
                    }

                    if (component.ReferenceImplicitly)
                        _ImplicitlyIncludedFrameworks.Add(component.TargetFrameworkID);
                }

                return result.ToArray();
            }

            public ParsedSDK ParseKSDKManifest(ISDKImportHost host)
            {
                LoadDevicesAndFamilies();

                _GlobalDefines = _Manifest.DocumentElement.SelectNodes("defines/define").OfType<XmlElement>().Select(e => new ParsedDefine(e)).ToArray();
                _Components = _Manifest.DocumentElement.SelectNodes($"components/component").OfType<XmlElement>()
                    .Select(n => new ParsedComponent(n))
                    .Where(c => !c.SkipUnconditionally)
                    .ToArray();

                AttachSVDFilesAndLinkerScriptsToDevices(host);
                Dictionary<string, FileCondition> fileConditions = new Dictionary<string, FileCondition>();

                var frameworks = TranslateComponentsToFrameworks(fileConditions);
                var vendorSamples = TranslateSampleProjects();

                var version = _Manifest.DocumentElement.GetAttribute("version");
                if (string.IsNullOrEmpty(version))
                    version = _Manifest.DocumentElement.SelectSingleNode("ksdk/@version")?.InnerText;

                if (string.IsNullOrEmpty(version))
                    version = "unknown";

                var allFamilies = _SpecializedDevices.Select(d => d.BuildMCUFamily()).ToArray();

                return new ParsedSDK
                {
                    BSP = new BoardSupportPackage
                    {
                        PackageID = "com.sysprogs.imported.ksdk2x." + allFamilies[0].ID,
                        PackageDescription = "Imported MCUXpresso SDK for " + allFamilies[0].ID,
                        PackageVersion = version,
                        GNUTargetID = "arm-eabi",
                        Frameworks = frameworks,
                        MCUFamilies = allFamilies.ToArray(),
                        SupportedMCUs = _SpecializedDevices.SelectMany(d => d.Complete(_GlobalDefines)).ToArray(),
                        FileConditions = fileConditions.Values.ToArray(),
                        VendorSampleCatalogName = "MCUXpresso Samples",
                        BSPImporterID = ID,
                    },

                    VendorSampleDirectory = new VendorSampleDirectory
                    {
                        Samples = vendorSamples.ToArray()
                    }
                };
            }

            public List<VendorSample> TranslateSampleProjects()
            {
                if (_SpecializedDevices.Count == 0)
                    throw new Exception("The selected KSDK contains no families");

                Dictionary<string, Dictionary<string, SpecializedDevice>> specializedDevicesByPackage = new Dictionary<string, Dictionary<string, SpecializedDevice>>();
                foreach (var dev in _SpecializedDevices)
                {
                    foreach (var pkg in dev.Device.PackageNames)
                    {
                        if (!specializedDevicesByPackage.TryGetValue(pkg, out var l2))
                            specializedDevicesByPackage[pkg] = l2 = new Dictionary<string, SpecializedDevice>();

                        l2[dev.Core.ID] = dev;
                    }
                }

                List<VendorSample> samples = new List<VendorSample>();

                foreach (XmlElement boardNode in _Manifest.DocumentElement.SelectNodes("boards/board"))
                {
                    string boardName = boardNode.GetAttribute("name");
                    string package = boardNode.GetAttribute("package");

                    if (!specializedDevicesByPackage.TryGetValue(package, out var specializedDevicesForThisPackage))
                    {
                        _Sink.LogWarning("Unknown device package: " + package);
                        continue;
                    }

                    foreach (XmlElement directExampleNode in boardNode.SelectNodes("examples/example"))
                    {
                        try
                        {
                            var example = new ParsedExample(_Directory, directExampleNode);
                            SpecializedDevice device;
                            if (specializedDevicesForThisPackage.Count == 1)
                                device = specializedDevicesForThisPackage.First().Value;
                            else if (!specializedDevicesForThisPackage.TryGetValue(example.CoreID, out device))
                            {
                                _Sink.LogWarning($"Invalid core ({example.CoreID}) referenced by {example.ID}");
                                continue;
                            }

                            if (device.FlagsDerivedFromSamples == null && !string.IsNullOrEmpty(example.RelativePath))
                                device.FlagsDerivedFromSamples = CollectCommonFlagsFromSample(example);

                            samples.Add(example.BuildVendorSample(_Directory, boardName, device, package, _AllComponentIDs, _ImplicitlyIncludedFrameworks));
                        }
                        catch (Exception ex)
                        {
                            _Sink.LogWarning(ex.Message);
                        }
                    }
                }

                return samples;
            }

            Regex rgCPUOrFPU = new Regex("(-mcpu|-mfpu)=([0-9a-zA-Z-_+]+)");    //'+' is needed for -mcpu=cortex-m33+nodsp

            private string CollectCommonFlagsFromSample(ParsedExample example)
            {
                try
                {
                    Dictionary<string, string> flags = new Dictionary<string, string>();
                    string dir = Path.Combine(_Directory, example.RelativePath);
                    var cmakeLists = Path.Combine(dir, "armgcc\\CMakeLists.txt");
                    HashSet<string> moreFlags = new HashSet<string>();
                    if (File.Exists(cmakeLists))
                    {
                        foreach (var line in File.ReadAllLines(cmakeLists))
                        {
                            var match = rgCPUOrFPU.Match(line);
                            if (match.Success)
                            {
                                if (!flags.TryGetValue(match.Groups[1].Value, out var oldValue) || oldValue == match.Groups[2].Value)
                                    flags[match.Groups[1].Value] = match.Groups[2].Value;
                                else
                                    flags[match.Groups[1].Value] = null;
                            }
                            if (line.Contains("-mthumb"))
                                moreFlags.Add("-mthumb");
                        }
                    }

                    return string.Join(" ", flags.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={kv.Value}").Concat(moreFlags).ToArray());
                }
                catch (Exception ex)
                {
                    _Sink.LogWarning(ex.Message);
                }

                return "";

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

            var bsp = new ParserImpl(location.Directory, doc, host.WarningSink).ParseKSDKManifest(host);
            bsp.Save(location.Directory);

            return new ImportedExternalSDK { BSPID = bsp.BSP.PackageID, Directory = location.Directory };
        }


        public const string ID = "com.sysprogs.sdkimporters.nxp.ksdk";

        public string Name => "MCUXpresso SDK";
        public string UniqueID => ID;
        public string CommandName => "Import an MCUXpresso SDK";
        public string Target => "arm-eabi";
        public string OpenFileFilter => "MCUXpresso SDK Manifest Files|*manifest*.xml";

        public bool IsCompatibleWithToolchain(LoadedToolchain toolchain)
        {
            var id = toolchain?.Toolchain?.GNUTargetID?.ToLower();
            return id?.Contains("arm") ?? true;
        }
    }
}
