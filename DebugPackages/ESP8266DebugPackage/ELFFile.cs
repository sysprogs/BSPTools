using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace ESP8266DebugPackage
{
    //ELF format is described here: http://www.skyfree.org/linux/references/ELF_Format.pdf
    public class ELFFile : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public class Elf32_Shdr
        {
            public UInt32 sh_name;		/* Section name (string tbl index) */
            public UInt32 sh_type;		/* Section type */
            public UInt32 sh_flags;		/* Section flags */
            public UInt32 sh_addr;		/* Section virtual addr at execution */
            public UInt32 sh_offset;		/* Section file offset */
            public UInt32 sh_size;		/* Section size in bytes */
            public UInt32 sh_link;		/* Link to another section */
            public UInt32 sh_info;		/* Additional section information */
            public UInt32 sh_addralign;		/* Section alignment */
            public UInt32 sh_entsize;		/* Entry size if section holds table */
        };


        [StructLayout(LayoutKind.Sequential)]
        public class Elf32_Ehdr
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Signature;
            public UInt16 e_type;			//16
            public UInt16 e_machine;		//18
            public UInt32 e_version;		//20
            public UInt32 e_entry;		//24
            public UInt32 e_phoff;		//28
            public UInt32 e_shoff;		//32
            public UInt32 e_flags;		//36
            public UInt16 e_ehsize;		//40
            public UInt16 e_phentsize;	//42
            public UInt16 e_phnum;		//44
            public UInt16 e_shentsize;	//46
            public UInt16 e_shnum;		//48
            public UInt16 e_shstrndx;		//50
        };

        [StructLayout(LayoutKind.Sequential)]
        public class elf32_sym
        {
            public UInt32 st_name;
            public UInt32 st_value;
            public UInt32 st_size;
            public byte st_info;
            public byte st_other;
            public UInt16 st_shndx;
        };

        public enum SectionType
        {
            SHT_NULL = 0,
            SHT_PROGBITS = 1,
            SHT_SYMTAB = 2,
            SHT_STRTAB = 3,
            SHT_NOBITS = 8,
        }

        public static object ConvertByteArrayToStruct(byte[] array, Type type, int offset)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(type));
            Marshal.Copy(array, offset, buffer, Marshal.SizeOf(type));
            object obj = Marshal.PtrToStructure(buffer, type);
            Marshal.FreeHGlobal(buffer);
            return obj;
        }

        protected _Type ReadStruct<_Type>(int offsetInFile)
        {
            _File.Seek(offsetInFile, SeekOrigin.Begin);
            byte[] data = new byte[Marshal.SizeOf(typeof(_Type))];
            _File.Read(data, 0, data.Length);
            return (_Type)ConvertByteArrayToStruct(data, typeof(_Type), 0);
        }

        byte[] LoadSection(Elf32_Shdr sectionHeader)
        {
            return LoadSection((int)sectionHeader.sh_offset, (int)sectionHeader.sh_size);
        }

        byte[] LoadSection(int offset, int size)
        {
            if (size + offset > _File.Length)
                throw new Exception("Invalid string size/offset");

            byte[] data = new byte[size];
            _File.Seek((int)offset, SeekOrigin.Begin);
            _File.Read(data, 0, size);
            return data;
        }

        Stream _File;

        Elf32_Ehdr _CachedHeader;

        public Elf32_Ehdr ELFHeader
        {
            get
            {
                if (_CachedHeader == null)
                {
                    var hdr = ReadStruct<Elf32_Ehdr>(0);
                    if (hdr.Signature[0] != 0x7F || hdr.Signature[1] != 'E' || hdr.Signature[2] != 'L' || hdr.Signature[3] != 'F')
                        throw new Exception("Invalid ELF file");
                    _CachedHeader = hdr;
                }
                return _CachedHeader;
            }
        }

        byte[] _CachedStringTable;

        public byte[] StringTable
        {
            get
            {
                if (_CachedStringTable == null)
                {
                    var hdr = ELFHeader;
                    if (hdr.e_shoff + hdr.e_shentsize * hdr.e_shnum > _File.Length)
                        throw new Exception("Invalid ELF file");
                    if (hdr.e_shstrndx >= hdr.e_shnum)
                        throw new Exception("Invalid ELF file");

                    var hdrStrings = ReadStruct<Elf32_Shdr>((int)(hdr.e_shoff + hdr.e_shstrndx * hdr.e_shentsize));
                    if (hdrStrings.sh_type != (short)SectionType.SHT_STRTAB)
                        throw new Exception("Invalid ELF file: wrong string table section type");

                    _CachedStringTable = LoadSection(hdrStrings);
                }
                return _CachedStringTable;
            }
        }

        public ELFFile(string fileName)
        {
            _File = new BufferedStream(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), 1024 * 1024);
        }

        public virtual void Dispose()
        {
            if (_File != null)
                _File.Dispose();
        }

        public class ParsedSection
        {
            public string SectionName;
            public uint OffsetInFile;
            public uint Size;
            public uint VirtualAddress;
            public SectionType Type;
            public bool HasData;
            public bool PresentInMemory;

            public ParsedSection()
            {
            }

            public ParsedSection(ParsedSection section)
            {
                SectionName = section.SectionName;
                OffsetInFile = section.OffsetInFile;
                Size = section.Size;
                VirtualAddress = section.VirtualAddress;
                Type = section.Type;
                HasData = section.HasData;
            }

            public override string ToString()
            {
                return string.Format("{0}: {1} bytes", SectionName, Size);
            }

            public bool ContainsAddress(uint addr)
            {
                return (addr >= VirtualAddress) && (addr < VirtualAddress + Size);
            }

            public bool ContainsFileOffset(uint off)
            {
                return (off >= OffsetInFile) && (off < OffsetInFile + Size);
            }

            public uint AddressToSectionOffset(uint addr)
            {
                return addr - VirtualAddress;
            }
        }

        List<ParsedSection> GetAllSections()
        {
            List<ParsedSection> result = new List<ParsedSection>();
            var hdr = ELFHeader;
            var strings = StringTable;
            for (int i = 0; i < hdr.e_shnum; i++)
            {
                var shdr = ReadStruct<Elf32_Shdr>((int)(hdr.e_shoff + i * hdr.e_shentsize));

                ParsedSection section = new ParsedSection
                {
                    OffsetInFile = shdr.sh_offset,
                    Size = shdr.sh_size,
                    VirtualAddress = shdr.sh_addr,
                    Type = (SectionType)shdr.sh_type,
                    HasData = (shdr.sh_type != (uint)SectionType.SHT_NOBITS),
                    PresentInMemory = (shdr.sh_flags & 2) != 0,
                };

                int eidx;
                for (eidx = (int)shdr.sh_name; eidx < strings.Length; eidx++)
                    if (strings[eidx] == 0)
                        break;

                section.SectionName = Encoding.ASCII.GetString(strings, (int)shdr.sh_name, eidx - (int)shdr.sh_name);
                result.Add(section);
            }
            return result;
        }

        List<ParsedSection> _CachedSections;

        public List<ParsedSection> AllSections
        {
            get
            {
                if (_CachedSections == null)
                    _CachedSections = GetAllSections();
                return _CachedSections;
            }
        }


        public uint FirstSectionAddress
        {
            get
            {
                uint addr = uint.MaxValue;
                foreach (var section in AllSections)
                    if (section.VirtualAddress != 0)
                        addr = Math.Min(addr, section.VirtualAddress);
                return addr;
            }
        }

        public ParsedSection FindSectionByName(string name)
        {
            foreach (var s in AllSections)
                if (s.SectionName == name)
                    return s;
            return null;
        }

        public byte[] LoadSection(ParsedSection section)
        {
            if (section == null)
                return null;
            return LoadSection((int)section.OffsetInFile, (int)section.Size);
        }

        public elf32_sym[] LoadSymbolTable(int offset, int symbolCount)
        {
            elf32_sym[] data = new elf32_sym[symbolCount];
            int symSize = Marshal.SizeOf(typeof(elf32_sym));
            for (int i = 0; i < symbolCount; i++)
                data[i] = ReadStruct<elf32_sym>(offset + i * symSize);
            return data;
        }

        public struct ParsedELFSymbol
        {
            public string Name;
            public ulong Address;
            public uint Size;

            public override string ToString()
            {
                return string.Format("{0} : 0x{1:x}", Name, Address);
            }
        }

        public List<ParsedELFSymbol> LoadAllSymbols()
        {
            List<ParsedELFSymbol> symbols = new List<ParsedELFSymbol>();
            byte[] symbolTable = LoadSection(FindSectionByName(".symtab"));
            byte[] stringTable = LoadSection(FindSectionByName(".strtab"));

            if (symbolTable == null || stringTable == null)
                return symbols;

            bool isARM = ELFHeader.e_machine == 40;

            int structSize = Marshal.SizeOf(typeof(elf32_sym));
            int symbolCount = symbolTable.Length / structSize;
            for (int i = 0; i < symbolCount; i++)
            {
                elf32_sym rawSym = (elf32_sym)ConvertByteArrayToStruct(symbolTable, typeof(elf32_sym), i * structSize);
                if (rawSym.st_value == 0)
                    continue;

                string symbolName = null;
                if (rawSym.st_name != 0 && rawSym.st_name < stringTable.Length)
                {
                    int eidx = 0;
                    for (eidx = (int)rawSym.st_name; eidx < stringTable.Length; eidx++)
                        if (stringTable[eidx] == 0)
                            break;

                    symbolName = Encoding.ASCII.GetString(stringTable, (int)rawSym.st_name, eidx - (int)rawSym.st_name);
                }

                if (string.IsNullOrEmpty(symbolName) || symbolName[0] == '$')
                    continue;

                if (isARM)
                    rawSym.st_value &= ~1U; //On ARM the LSB of the address is used to denote arm/thumb mode

                symbols.Add(new ParsedELFSymbol { Address = rawSym.st_value, Name = symbolName, Size = rawSym.st_size });
            }

            return symbols;
        }
    }
}
