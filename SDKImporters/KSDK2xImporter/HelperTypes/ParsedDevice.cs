using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace KSDK2xImporter.HelperTypes
{
    class ParsedDevice
    {
        public readonly string DeviceName;
        public readonly string ID;
        public readonly string[] PackageNames;
        public readonly string FullName;
        public readonly string VendorID;

        public struct Core
        {
            public string ID, Type, Name;

            public override string ToString() => $"{ID} ({Type})";

            public Core(XmlElement el)
            {
                ID = el.SelectSingleNode("@id")?.Value;
                Name = el.SelectSingleNode("@name")?.Value;
                Type = el.SelectSingleNode("@type")?.Value;

                if (string.IsNullOrEmpty(Type))
                    Type = ID;  //pre-KSDK 2.2
            }
        }

        public readonly Core[] Cores;
        public readonly int FLASHSize, RAMSize;
        public readonly MCUMemory[] Memories;
        public readonly string[] RedLinkServerOptions;

        public ParsedDevice(XmlElement devNode)
        {
            ID = devNode.GetAttribute("id");
            FullName = devNode.GetAttribute("full_name");
            DeviceName = devNode.GetAttribute("name");

            if (string.IsNullOrEmpty(ID))
                ID = DeviceName;

            PackageNames = devNode.SelectNodes($"package/@name").OfType<XmlAttribute>().Select(n => n.Value).Where(v => !string.IsNullOrEmpty(v)).ToArray();
            Cores = devNode.SelectNodes($"core").OfType<XmlElement>().Select(e => new Core(e)).ToArray();

            if (!int.TryParse((devNode.SelectSingleNode("memory/@flash_size_kb")?.Value ?? ""), out FLASHSize))
                int.TryParse((devNode.SelectSingleNode("total_memory/@flash_size_kb")?.Value ?? ""), out FLASHSize);

            if (!int.TryParse((devNode.SelectSingleNode("memory/@ram_size_kb")?.Value ?? ""), out RAMSize))
                int.TryParse((devNode.SelectSingleNode("total_memory/@ram_size_kb")?.Value ?? ""), out RAMSize);

            FLASHSize *= 1024;
            RAMSize *= 1024;

            Memories = devNode.SelectNodes("memory/memoryBlock").OfType<XmlElement>().Select(ParseMemoryBlock).Where(b => b != null).ToArray();

            RedLinkServerOptions = devNode.SelectNodes("debug_configurations/debug_configuration/params/params[@name='misc.options']/@value")
                .OfType<XmlAttribute>()
                .Select(a => a.Value)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            VendorID = (devNode.SelectSingleNode("metadataSet/metadata[@key='vendor']/@value") as XmlAttribute)?.Value ?? "NXP";
        }

        static MCUMemory ParseMemoryBlock(XmlElement el)
        {
            string name = el.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                return null;

            if (!ulong.TryParse(el.GetAttribute("addr"), NumberStyles.HexNumber, null, out ulong addr))
                return null;

            if (!ulong.TryParse(el.GetAttribute("size"), NumberStyles.HexNumber, null, out ulong size))
                return null;

            return new MCUMemory { Name = name, Address = addr, Size = size };
        }

        public override string ToString() => DeviceName;

        static Regex rgCore = new Regex("_core([0-9]+)_");

        public string MakeCoreSuffix(Core core)
        {
            if (Cores.Length > 1)
            {
                var m = rgCore.Match(core.ID);
                if (m.Success)
                    return "_" + core.ID.Substring(0, m.Groups[1].Index + m.Groups[1].Length).ToUpper();
                else
                    return "_" + core.ID.ToUpper();
            }
            else
                return "";
        }
    }


    //Refers to a specific core of a specific device. Corresponds to 1 MCUFamily and multiple MCUs (one per package)
    class SpecializedDevice
    {
        public readonly ParsedDevice Device;
        public readonly ParsedDevice.Core Core;

        public FileReference ConvertedSVDFile;
        public FileReference[] DiscoveredLinkerScripts;

        public readonly string CoreSuffix;

        public string FlagsDerivedFromSamples;

        public SpecializedDevice(ParsedDevice device, ParsedDevice.Core core)
        {
            Device = device;
            Core = core;
            CoreSuffix = device.MakeCoreSuffix(core);
        }

        public override string ToString() => $"{Device} ({Core})";

        static string ExpandCommonVariables(string str)
        {
            if (str == null || !str.Contains("$"))
                return str;

            str = str.Replace("$|compiler|", "GCC");
            return str;
        }

        public string ExpandVariables(string str, string packageName = null)
        {
            if (str == null || !str.Contains("$"))
                return str;

            str = ExpandCommonVariables(str);

            str = str.Replace("$|device_full_name|", Device.FullName);
            str = str.Replace("$|device|", Device.DeviceName);
            str = str.Replace("$|core|", Core.Type);
            str = str.Replace("$|core_name|", Core.Name);

            if (packageName != null)
                str = str.Replace("$|package|", packageName);
            return str;
        }

        public static string ExpandVariables(string str, SpecializedDevice optionalDevice, string packageName = null)
        {
            if (optionalDevice == null)
                return ExpandCommonVariables(str);
            else
                return optionalDevice.ExpandVariables(str, packageName);
        }

        public string FamilyID => Device.FullName + Device.MakeCoreSuffix(Core);

        public MCUFamily BuildMCUFamily()
        {
            var mcuFamily = new MCUFamily
            {
                ID = FamilyID,
                UserFriendlyName = Device.FullName + Device.MakeCoreSuffix(Core),
                CompilationFlags = new ToolFlags()
            };

            if (FlagsDerivedFromSamples?.Contains("-mcpu") == true)
            {
                mcuFamily.CompilationFlags.COMMONFLAGS = FlagsDerivedFromSamples;
                if (FlagsDerivedFromSamples.Contains("-mfpu"))
                    CoreFlagHelper.AddFPModeProperty(CoreFlagHelper.CoreSpecificFlags.DefaultHardFloat, mcuFamily);
            }
            else
            {
                CoreFlagHelper.AddCoreSpecificFlags(CoreFlagHelper.CoreSpecificFlags.FPU | CoreFlagHelper.CoreSpecificFlags.DefaultHardFloat, mcuFamily, Core.Type);
            }

            int coreIndex = 0;
            for (int i = 0; i < Device.Cores.Length; i++)
                if (Device.Cores[i].ID == Core.ID)
                {
                    coreIndex = i;
                    break;
                }

            mcuFamily.AdditionalSystemVars = new[]
            {
                new SysVarEntry{Key = "REDLINK:VENDOR_ID",  Value = Device.VendorID},
                new SysVarEntry{Key = "REDLINK:DEVICE_ID",  Value = Device.ID},
                new SysVarEntry{Key = "REDLINK:DEBUG_OPTIONS",  Value = Device.RedLinkServerOptions?.FirstOrDefault() ?? ""},
                new SysVarEntry{Key = "REDLINK:CORE_INDEX", Value = coreIndex.ToString()},
                new SysVarEntry{Key = "REDLINK:CORE_COUNT", Value = Device.Cores.Length.ToString()},
            };

            if (DiscoveredLinkerScripts != null)
            {
                if (DiscoveredLinkerScripts.Length == 1)
                    mcuFamily.CompilationFlags.LinkerScript = DiscoveredLinkerScripts[0].GetBSPPath();
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
                        SuggestionList = DiscoveredLinkerScripts.Select(p => new PropertyEntry.Enumerated.Suggestion { InternalValue = p.RelativePath, UserFriendlyName = Path.GetFileName(p.RelativePath) }).ToArray()
                    });

                }
            }

            return mcuFamily;
        }

        public string MakeMCUID(string packageName) => packageName + CoreSuffix;

        public string[] FinalMCUIDs => Device.PackageNames.Select(MakeMCUID).ToArray();

        public IEnumerable<MCU> Complete(ParsedDefine[] globalDefines)
        {
            foreach (var pkg in Device.PackageNames)
            {
                yield return new MCU
                {
                    ID = MakeMCUID(pkg),
                    UserFriendlyName = $"{pkg} {CoreSuffix}".Trim(),
                    FamilyID = FamilyID,
                    FLASHSize = Device.FLASHSize,
                    RAMSize = Device.RAMSize,
                    MemoryMap = (Device.Memories.Length == 0) ? null : new AdvancedMemoryMap { Memories = Device.Memories },
                    CompilationFlags = new ToolFlags
                    {
                        PreprocessorMacros = globalDefines.Select(d => ExpandVariables(d.Definition, pkg)).Where(d => !d.Contains("$")).ToArray(),
                        AdditionalLibraries = new[] {"m"},  //fsl_str.c on i.MXRT1064 uses pow() 
                    },

                    MCUDefinitionFile = ConvertedSVDFile.RelativePath?.Replace('\\', '/')
                };
            }
        }
    }
}
