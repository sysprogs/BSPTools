using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace BSPGenerationTools
{
    public static class XmlExtensions
    {
        public static IEnumerable<XmlElement> SelectElements(this XmlElement el, string xpath) => el.SelectNodes(xpath).OfType<XmlElement>();

        public static string GetStringAttribute(this XmlElement el, string name)
        {
            var value = el.GetAttribute(name);
            if (string.IsNullOrEmpty(value))
                throw new Exception($"The value of '{name}' should not be empty");

            return value;
        }

        public static ulong GetUlongAttribute(this XmlElement el, string name)
        {
            var str = el.GetStringAttribute(name);
            if (str.StartsWith("0x"))
                return ulong.Parse(str.Substring(2), NumberStyles.HexNumber);
            else
                return ulong.Parse(str);
        }        
        
        public static ulong? TryGetUlongAttribute(this XmlElement el, string name)
        {
            var str = el.GetAttribute(name);
            if (string.IsNullOrEmpty(str))
                return null;

            ulong result;
            bool parsed;
            if (str.StartsWith("0x"))
                parsed = ulong.TryParse(str.Substring(2), NumberStyles.HexNumber, null, out result);
            else
                parsed = ulong.TryParse(str, out result);

            if (parsed)
                return result;
            else
                return null;
        }
    }
}
