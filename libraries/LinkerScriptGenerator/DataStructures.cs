﻿/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Serialization;
using System.Xml;
using System.Linq;

namespace LinkerScriptGenerator
{
    [Flags]
    public enum MemoryAccess
    {
        Undefined   = 0x00,
        Readable    = 0x01,
        Writable    = 0x02,
        Executable  = 0x04,
        Allocatable = 0x08,
        Initialized = 0x10,
    }

    public enum MemoryType
    {
        Unknown,
        FLASH,
        RAM,
    }

    public class Memory
    {
        public string Name;
        [XmlIgnore]
        public uint Start;
        [XmlIgnore]
        public uint Size;
        public MemoryAccess Access;
        public MemoryType Type;
        public bool IsPrimary;  //Primary FLASH or SRAM

        public bool ContainsAddress(uint addr) => addr >= Start && (addr < (Start + Size));

        public override string ToString()
        {
            return string.Format("{0}: {1}", Name, SizeWithSuffix);
        }

        [XmlIgnore]
        public string MemoryDefinitionString
        {
            get
            {
                StringBuilder str = new StringBuilder(Name);
                str.Append(" (");
                if ((Access & MemoryAccess.Readable) != MemoryAccess.Undefined)
                    str.Append('R');
                if ((Access & MemoryAccess.Writable) != MemoryAccess.Undefined)
                    str.Append('W');
                if ((Access & MemoryAccess.Executable) != MemoryAccess.Undefined)
                    str.Append('X');
                if ((Access & MemoryAccess.Allocatable) != MemoryAccess.Undefined)
                    str.Append('A');
                if ((Access & MemoryAccess.Initialized) != MemoryAccess.Undefined)
                    str.Append('I');
                str.Append(")");
                return str.ToString();
            }
        }

        [XmlAnyElement("Start")]
        public XmlNode[] _StartHelper
        {
            get
            {
                var el = new XmlDocument().CreateElement("Start");
                el.InnerText = string.Format("0x{0:x8}", Start);
                return new XmlNode[] { el };
            }
            set
            {
                string text = value[0].InnerText;
                if (text.StartsWith("0x"))
                    Start = uint.Parse(text.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
                else
                    Start = uint.Parse(text);
            }
        }

        [XmlAnyElement("Size")]
        public XmlNode[] _SizeHelper
        {
            get
            {
                var el = new XmlDocument().CreateElement("Size");
                el.InnerText = FormatSize(Size);
                return new XmlNode[] { el };
            }
            set
            {
                string text = value[0].InnerText;
                if (text.EndsWith("M", StringComparison.InvariantCultureIgnoreCase))
                    Size = uint.Parse(text.Substring(0, text.Length - 1)) * 1024 * 1024;
                else if (text.EndsWith("K", StringComparison.InvariantCultureIgnoreCase))
                    Size = uint.Parse(text.Substring(0, text.Length - 1)) * 1024;
                else if (text.StartsWith("0x"))
                    Size = uint.Parse(text.Substring(2), System.Globalization.NumberStyles.HexNumber);
                else
                    Size = uint.Parse(text);
            }
        }

        [XmlIgnore]
        public string SizeWithSuffix
        {
            get
            {
                return FormatSize(Size);
            }
        }

        [XmlIgnore]
        public uint End
        {
            get
            {
                return Start + Size;
            }
        }

        static string FormatSize(uint size)
        {
            if (size == 0)
                return "0";
            if (size % (1024 * 1024) == 0)
                return string.Format("{0}M", size / (1024 * 1024));
            else if (size % 1024 == 0)
                return string.Format("{0}K", size / 1024);
            else
                return string.Format("0x{0:x}", size);
        }

        public Memory Clone()
        {
            return (Memory)this.MemberwiseClone();
        }
    }

    public abstract class MemoryLocationRule
    {
        public abstract bool IsMatch(Memory memory);

        class ByNameImpl : MemoryLocationRule
        {
            public readonly string[] Names;

            public ByNameImpl(string[] names)
            {
                Names = names;
            }

            public override bool IsMatch(Memory memory)
            {
                foreach (var n in Names)
                    if (n == memory.Name)
                        return true;
                return false;
            }
        }

        class ByAddressImpl : MemoryLocationRule
        {
            public readonly ulong Address;

            public ByAddressImpl(ulong address)
            {
                Address = address;
            }

            public override bool IsMatch(Memory memory)
            {
                return memory.Start == Address;
            }
        }

        public static MemoryLocationRule ByName(params string[] names) => new ByNameImpl(names);
        public static MemoryLocationRule ByAddress(ulong address) => new ByAddressImpl(address);
    }

    public class MemoryLayout
    {
        public string DeviceName;
        public List<Memory> Memories;

        public MemoryLayout Clone()
        {
            var r = new MemoryLayout { DeviceName = DeviceName, Memories = new List<Memory>() };
            foreach (var m in Memories)
                r.Memories.Add(m.Clone());
            return r;
        }

        public Memory TryLocateMemory(MemoryType type, params MemoryLocationRule[] rules)
        {
            foreach (var rule in rules)
            {
                var mem = Memories.FirstOrDefault(m => m.Type == type && rule.IsMatch(m));
                if (mem != null)
                    return mem;
            }

            return null;
        }

        public Memory TryLocateOnlyMemory(MemoryType type, bool markPrimary)
        {
            var mems = Memories.Where(m => m.Type == type).ToArray();
            if (mems.Length == 1)
            {
                if (markPrimary)
                    mems[0].IsPrimary = true;
                return mems[0];
            }

            return null;
        }

        public Memory TryLocateAndMarkPrimaryMemory(MemoryType type, params MemoryLocationRule[] rules)
        {
            var mem = TryLocateMemory(type, rules);
            if (mem != null)
                mem.IsPrimary = true;

            return mem;
        }
    }

    [Flags]
    public enum SectionReferenceFlags
    {
        None            = 0x00,
        Keep            = 0x01,
        Sort            = 0x02,
        AddPrefixForm   = 0x04,
        PrefixFormOnly  = 0x08,
        DotPrefixForm   = 0x10,
        NoBrackets      = 0x20,
    }

    public class SectionReference
    {
        public string NamePattern;
        public SectionReferenceFlags Flags;
    }

    [Flags]
    public enum SectionFlags
    {
        None                    = 0x00,
        InitializerInMainMemory = 0x01,
        DefineShortLabels       = 0x02,     //e.g. _sdata, _edata
        ProvideLongLabels       = 0x04,     //e.g. __data_start__
        DefineMediumLabels      = 0x08,     //e.g. _mtb_start
        Unaligned               = 0x10,
        ProvideLongLabelsLeadingUnderscores = 0x20, //e.g. __exidx_start
        NoLoad = 0x40,
    }

    public class FillInfo
    {
        public uint Pattern;
        public int TotalSize;
    }

    public class Section
    {
        public string Name;
        public SectionFlags Flags;
        public List<SectionReference> Inputs;
        public string TargetMemory;

        public FillInfo Fill;

        public int Alignment;
        public string CustomStartLabel, CustomEndLabel;
        public string[] CustomContents;

        [XmlIgnore]
        public bool IsUnaligned
        {
            get
            {
                return (Flags & SectionFlags.Unaligned) != SectionFlags.None;
            }
        }
    }

    public struct SymbolAlias
    {
        public string Name;
        public string Target;
    }

    public class LinkerScriptTemplate
    {
        public string EntryPoint;
        public int SectionAlignment = 4;
        public List<Section> Sections;
        public List<Section> SectionsAfterEnd;
        public SymbolAlias[] SymbolAliases;

        public LinkerScriptTemplate ShallowCopy()
        {
            return (LinkerScriptTemplate)MemberwiseClone();
        }
    }

}
