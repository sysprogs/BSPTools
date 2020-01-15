using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BSPGenerationTools
{
    public class SmartPropertyDefinition
    {
        public readonly string Name, IDWithoutPrefix, IDWithPrefix;
        public readonly string DefaultValue;
        public readonly string[] ExtraArguments;

        public readonly bool? IsDefaultOn;
        public readonly Item[] Items;
        public readonly int DefaultItemIndex;

        protected SmartPropertyDefinition(string name, string idWithoutPrefix, string idWithPrefix, string[] extraArguments, bool? isDefaultOn, IEnumerable<Item> items, int defaultIndex, string defaultValue)
        {
            Name = name;
            DefaultValue = defaultValue;
            IDWithoutPrefix = idWithoutPrefix;
            IDWithPrefix = idWithPrefix;
            ExtraArguments = extraArguments;

            IsDefaultOn = isDefaultOn;
            Items = items.ToArray();
            DefaultItemIndex = defaultIndex;
        }

        public struct Item  //Encoded as: key=>Value.Name[Value.ID] or key=>Value or Value
        {
            public readonly NameAndID Value;
            public string Key;

            public Item(string key, NameAndID value)
            {
                Key = key;
                Value = value;
            }
        }

        public static SmartPropertyDefinition Parse(string definition, string groupID, int extraArgumentsAfterID = 0, string onValueForBooleanProperties = "1")
        {
            int idx = -1, count = 0, prevIdx = 0;
            definition = definition.Trim();

            List<string> allArgs = new List<string>();

            const string defaultMarker = "--default=";
            string defaultValue = null;
            if (definition.StartsWith(defaultMarker))
            {
                int idx0 = definition.IndexOf('|');
                defaultValue = definition.Substring(defaultMarker.Length, idx0 - defaultMarker.Length);
                definition = definition.Substring(idx0 + 1).Trim();
            }

            for (; count < (extraArgumentsAfterID + 1); count++, prevIdx = idx)
            {
                idx = definition.IndexOf('|', prevIdx);
                if (idx == -1)
                    break;

                allArgs.Add(definition.Substring(prevIdx, idx - prevIdx));
                idx++;
            }

            if (allArgs.Count < (extraArgumentsAfterID + 1) || idx == -1)
                throw new Exception("Insufficient initial arguments in smart property definition");

            bool? defaultOn = null;
            if (allArgs[0].StartsWith("-"))
                defaultOn = false;
            else if (allArgs[0].StartsWith("+"))
                defaultOn = true;

            NameAndID name = new NameAndID(allArgs[0].TrimStart('-', '+'));

            string idWithoutPrefix, idWithPrefix;
            if (string.IsNullOrEmpty(groupID))
                idWithoutPrefix = idWithPrefix = "com.sysprogs.bspoptions." + name.ID;
            else
            {
                idWithoutPrefix = name.ID;
                idWithPrefix = groupID + idWithoutPrefix;
            }

            string[] values = definition.Substring(idx).Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            List<Item> items = new List<Item>();
            int defaultIndex = 0;

            foreach (var rawVal in values)
            {
                string val = rawVal.Trim();
                string regex, itemName;

                idx = val.IndexOf("=>");
                if (idx == -1)
                {
                    regex = val;
                    itemName = onValueForBooleanProperties;
                }
                else
                {
                    regex = val.Substring(0, idx);
                    itemName = val.Substring(idx + 2);
                    if (itemName.StartsWith("+"))
                        defaultIndex = items.Count;
                }

                items.Add(new Item(regex, new NameAndID(itemName.TrimStart('+'))));
            }

            return new SmartPropertyDefinition(name.Name, idWithoutPrefix, idWithPrefix, allArgs.Skip(1).ToArray(), defaultOn, items, defaultIndex, defaultValue);
        }
    }
}

public struct NameAndID
{
    public string Name;
    public string ID;

    public NameAndID(string str)
    {
        int idx = str.IndexOf('[');
        if (idx == -1)
        {
            Name = str;
            ID = Name.Replace(' ', '_');
        }
        else
        {
            Name = str.Substring(0, idx).Trim();
            ID = str.Substring(idx + 1).TrimEnd(']', ' ');
        }
    }

    public override string ToString()
    {
        return Name;
    }
}
