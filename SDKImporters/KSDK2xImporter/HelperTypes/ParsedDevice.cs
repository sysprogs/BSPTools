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

        public string MakeCoreSuffix(Core core)
        {
            if (Cores.Length > 1)
                return "_" + core.Name.ToUpper();
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

        public SpecializedDevice(ParsedDevice device, ParsedDevice.Core core)
        {
            Device = device;
            Core = core;
        }

        public override string ToString() => $"{Device} ({Core})";

        public string ExpandVariables(string str, string packageName = null)
        {
            if (str == null || !str.Contains("$"))
                return str;

            str = str.Replace("$|device_full_name|", Device.FullName);
            str = str.Replace("$|device|", Device.DeviceName);
            str = str.Replace("$|compiler|", "GCC");
            str = str.Replace("$|core|", Core.Type);
            str = str.Replace("$|core_name|", Core.Name);

            if (packageName != null)
                str = str.Replace("$|package|", packageName);
            return str;
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

            CoreFlagHelper.AddCoreSpecificFlags(CoreFlagHelper.CoreSpecificFlags.FPU, mcuFamily, Core.Type);

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

        public IEnumerable<MCU> Complete(ParsedDefine[] globalDefines)
        {
            foreach (var pkg in Device.PackageNames)
            {
                yield return new MCU
                {
                    ID = pkg,
                    UserFriendlyName = $"{pkg} (MCUxpresso)",
                    FamilyID = FamilyID,
                    FLASHSize = Device.FLASHSize,
                    RAMSize = Device.RAMSize,
                    MemoryMap = new AdvancedMemoryMap { Memories = Device.Memories },
                    CompilationFlags = new ToolFlags
                    {
                        PreprocessorMacros = globalDefines.Select(d => ExpandVariables(d.Definition)).Where(d => !d.Contains("$")).ToArray()
                    },

                    MCUDefinitionFile = ConvertedSVDFile.GetBSPPath(),
                };
            }
        }
    }
}
