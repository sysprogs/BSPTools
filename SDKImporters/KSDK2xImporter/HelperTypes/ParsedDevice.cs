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

        public MCUFamily BuildMCUFamily(Core core)
        {
            var mcuFamily = new MCUFamily
            {
                ID = FullName + MakeCoreSuffix(core),
                UserFriendlyName = DeviceName + MakeCoreSuffix(core)
            };

            CoreFlagHelper.AddCoreSpecificFlags(CoreFlagHelper.CoreSpecificFlags.FPU, mcuFamily, core.Type);
            return mcuFamily;
        }
    }


    class ConstructedBSPDevice
    {
        public readonly ParsedDevice Device;
        public readonly ParsedDevice.Core Core;
        public readonly string Package;
        public readonly string FamilyID;

        public string ConvertedSVDFile;

        public ConstructedBSPDevice(ParsedDevice device, ParsedDevice.Core core, string package, string familyID)
        {
            Device = device;
            Core = core;
            Package = package;
            FamilyID = familyID;
        }

        public override string ToString() => Package;

        public string ExpandVariables(string str)
        {
            if (str == null || !str.Contains("$"))
                return str;

            str = str.Replace("$|device_full_name|", Device.FullName);
            str = str.Replace("$|device|", Device.DeviceName);
            str = str.Replace("$|compiler|", "GCC");
            str = str.Replace("$|core|", Core.Type);
            str = str.Replace("$|core_name|", Core.Name);
            str = str.Replace("$|package|", Package);
            return str;
        }

        public MCU Complete(ParsedDefine[] globalDefines)
        {
            return new MCU
            {
                ID = Package,
                UserFriendlyName = $"{Package} (MCUxpresso)",
                FamilyID = FamilyID,
                FLASHSize = Device.FLASHSize,
                RAMSize = Device.RAMSize,
                MemoryMap = new AdvancedMemoryMap { Memories = Device.Memories },
                CompilationFlags = new ToolFlags
                {
                    PreprocessorMacros = globalDefines.Select(d => ExpandVariables(d.Definition)).Where(d => !d.Contains("$")).ToArray()
                },

                MCUDefinitionFile = ConvertedSVDFile
            };
        }
    }
}
