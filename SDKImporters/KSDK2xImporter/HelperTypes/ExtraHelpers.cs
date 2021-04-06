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

namespace KSDK2xImporter.HelperTypes
{
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

        public override string ToString() => Definition;
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

    struct ParsedFilter
    {
        public string[] Devices, Cores; //Null if not used
        public bool SkipUnconditionally;

        public bool AppliesToAllCores => Cores == null;
        public bool AppliesToAllDevices => Devices == null;

        public override string ToString()
        {
            return string.Join(" ", Devices) + "( " + string.Join(" ", Cores ?? new string[0]) + ")";
        }

        public ParsedFilter(XmlElement el)
        {
            Devices = el.GetAttribute("devices").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (Devices.Length == 0)
            {
                string singleDev = el.GetAttribute("device");
                if (singleDev != "")
                    Devices = new[] { singleDev };
                else
                    Devices = null;
            }

            Cores = el.GetAttribute("device_cores").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (Cores.Length == 0)
                Cores = null;

            SkipUnconditionally = false;
            string toolchain = el.GetAttribute("toolchain");
            if (toolchain != "" && !toolchain.Contains("gcc"))
                SkipUnconditionally = true;
            string compiler = el.GetAttribute("compiler");
            if (compiler != "" && !compiler.Contains("gcc"))
                SkipUnconditionally = true;

            string exclude = el.GetAttribute("exclude");
            if (exclude.ToLower() == "true")
            {
                if (!ShouldIgnoreExcludeAttribute(el))
                    SkipUnconditionally = true;
            }
        }

        private bool ShouldIgnoreExcludeAttribute(XmlElement el)
        {
            if (el.GetAttribute("type") != "asm_include")
                return false;

            var parentID = (el.ParentNode as XmlElement)?.GetAttribute("id");
            if (parentID?.StartsWith("middleware.azure_rtos.tx.template") == true)
            {
                //This works around a bug in the MCUXpresso SDK generator.
                //As of April 2021, generating a GCC SDK with Azure RTOS will mark tx_timer_interrupt.S and a few other files with the 'exclude' attribute.
                //This doesn't happen when generating the MCUXpresso SDK.
                return true;
            }

            return false;
        }

        public bool MatchesDevice(SpecializedDevice device)
        {
            if (SkipUnconditionally)
                return false;

            if (device == null)
                return true;

            if (Devices != null)
            {
                if (!Devices.Contains(device.Device.ID))
                    return false;
            }

            if (Cores != null)
            {
                if (!Cores.Contains(device.Core.ID))
                    return false;
            }

            return true;
        }
    }

}
