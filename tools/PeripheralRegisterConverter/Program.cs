using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace PeripheralRegisterConverter
{
    class Program
    {
        public static uint? ParseAddress(string str)
        {
            if (str == null)
                return null;

            bool done;
            uint value;
            if (str.StartsWith("0x"))
                done = uint.TryParse(str.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out value);
            else
                done = uint.TryParse(str, out value);

            if (!done)
                return null;

            return value;
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: PeripheralRegisterConverter <input XML file> <output XML file>");

            string baseDir = Path.GetDirectoryName(args[0]);
            XmlDocument xml = new XmlDocument();
            xml.Load(args[0]);

            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();

            foreach(var node in xml.DocumentElement.SelectNodes("//cpu/instance").OfType<XmlElement>())
            {
                string instanceFile = Path.Combine(baseDir, node.GetAttribute("href"));
                if (!File.Exists(instanceFile))
                    continue;

                uint baseAddr = ParseAddress(node.GetAttribute("baseaddr")) ?? throw new Exception("Missing address");
                XmlDocument xml2 = new XmlDocument();
                xml2.Load(instanceFile);
                sets.Add(BuildRegisterSet(node.GetAttribute("id"), baseAddr, xml2));
            }

            XmlTools.SaveObject(new MCUDefinition { RegisterSets = sets.ToArray(), MCUName = Path.GetFileNameWithoutExtension(args[0]) }, args[1]);
        }

        private static HardwareRegisterSet BuildRegisterSet(string id, uint baseAddr, XmlDocument doc)
        {
            return new HardwareRegisterSet
            {
                UserFriendlyName = id,
                Registers = doc.DocumentElement.SelectNodes("register").OfType<XmlElement>().Select(e => MakeRegister(baseAddr, e)).Where(r => r != null).ToArray()
            };
        }

        private static HardwareRegister MakeRegister(uint baseAddr, XmlElement node)
        {
            string id = node.GetAttribute("id");
            uint? offset = ParseAddress(node.GetAttribute("offset"));
            uint? size = ParseAddress(node.GetAttribute("width"));
            if (!offset.HasValue || !size.HasValue)
                return null;

            return new HardwareRegister
            {
                Name = id,
                Address = $"0x{baseAddr + offset.Value:x8}",
                SizeInBits = (int)size,
                SubRegisters = NullIfEmpty(node.SelectNodes("bitfield").OfType<XmlElement>().Select(MakeSubregister).ToArray())
            };
        }

        private static HardwareSubRegister[] NullIfEmpty(HardwareSubRegister[] coll)
        {
            if (coll.Length == 0)
                return null;
            return coll;
        }

        private static HardwareSubRegister MakeSubregister(XmlElement node)
        {
            string id = node.GetAttribute("id");
            return new HardwareSubRegister
            {
                Name = id,
                FirstBit = (int)(ParseAddress(node.GetAttribute("begin")) ?? throw new Exception("Missing start offset for " + id)),
                SizeInBits = (int)(ParseAddress(node.GetAttribute("width")) ?? throw new Exception("Missing start offset for " + id)),
            };
        }
    }
}
