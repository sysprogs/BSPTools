using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace ESP8266DebugPackage
{
    public class ArgumentValueAttribute : Attribute
    {
        public readonly string Name;

        public ArgumentValueAttribute(string name)
        {
            Name = name;
        }
    }

    public class ESP8266BinaryImage
    {
        public struct Segment
        {
            public uint Address;
            public byte[] Data;
        }

        public enum FLASHMode
        {
            [ArgumentValue("qio")]
            QIO = 0,
            [ArgumentValue("qout")]
            QOUT,
            [ArgumentValue("dio")]
            DIO,
            [ArgumentValue("dout")]
            DOUT
        }

        public enum FLASHSize
        {
            [ArgumentValue("4m")]
            size4M = 0,
            [ArgumentValue("2m")]
            size2M,
            [ArgumentValue("8m")]
            size8M,
            [ArgumentValue("16m")]
            size16M,
            [ArgumentValue("32m")]
            size32M,
            [ArgumentValue("16m-c1")]
            size16M_c1,
            [ArgumentValue("32m-c1")]
            size32M_c1,
            [ArgumentValue("32m-c2")]
            size32M_c2,
        }

        public enum FLASHFrequency
        {
            [ArgumentValue("40m")]
            freq40M = 0,
            [ArgumentValue("26m")]
            freq26M,
            [ArgumentValue("20m")]
            freq20M,
            [ArgumentValue("80m")]
            freq80M = 0x0f
        }

        static _Ty ParseEnumValue<_Ty>(string arg)
        {
            foreach(FieldInfo fld in typeof(_Ty).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = fld.GetCustomAttributes(typeof(ArgumentValueAttribute), false);
                if (attr != null && attr.Length > 0)
                {
                    if ((attr[0] as ArgumentValueAttribute).Name == arg)
                        return (_Ty)fld.GetValue(null);
                }
            }
            return default(_Ty);
        }

        public struct ParsedHeader
        {
            public FLASHSize Size;
            public FLASHFrequency Frequency;
            public FLASHMode Mode;

            public ParsedHeader(string frequency, string mode, string size)
            {
                Size = ParseEnumValue<FLASHSize>(size);
                Frequency = ParseEnumValue<FLASHFrequency>(frequency);
                Mode = ParseEnumValue<FLASHMode>(mode);
            }
        }

        static void UpdateChecksum(ref byte checksum, byte[] data)
        {
            foreach (var b in data)
                checksum ^= b;
        }

        public ParsedHeader Header;
        public List<Segment> Segments = new List<Segment>();
        public uint EntryPoint;

        public void Save(Stream stream)
        {
            byte checksum = 0xef;
            long offBase = stream.Position;
            stream.WriteByte(0xe9);
            stream.WriteByte((byte)Segments.Count);
            stream.WriteByte((byte)Header.Mode);
            stream.WriteByte((byte)(((byte)Header.Size << 4) | (byte)Header.Frequency));
            stream.Write(BitConverter.GetBytes(EntryPoint), 0, 4);
            foreach(var segment in Segments)
            {
                stream.Write(BitConverter.GetBytes(segment.Address), 0, 4);
                stream.Write(BitConverter.GetBytes(segment.Data.Length), 0, 4);
                stream.Write(segment.Data, 0, segment.Data.Length);
                UpdateChecksum(ref checksum, segment.Data);
            }

            int alignment = (int)(15 - ((stream.Position - offBase) % 16));
            stream.Write(new byte[alignment], 0, alignment);
            stream.WriteByte(checksum);
        }

        const uint SPIFLASHBase = 0x40200000;
        const uint SPIFLASHLimit = 0x40300000;

        public static ESP8266BinaryImage BaseImageFromELFFile(ELFFile file, ParsedHeader header, bool esptoolSectionOrder = false)
        {
            ESP8266BinaryImage image = new ESP8266BinaryImage();
            image.EntryPoint = file.ELFHeader.e_entry;
            image.Header = header;

            List<ELFFile.ParsedSection> sections = new List<ELFFile.ParsedSection>();
            if (esptoolSectionOrder)
            {
                foreach (var secN in new string[] { ".text", ".data", ".rodata" })
                    sections.Add(file.FindSectionByName(secN));
            }
            else
                sections = file.AllSections;

            foreach (var sec in sections)
            {
                if (sec.HasData && sec.PresentInMemory && sec.VirtualAddress < SPIFLASHBase)
                {
                    var segment = new Segment { Address = sec.VirtualAddress, Data = file.LoadSection(sec) };
                    int align = ((segment.Data.Length + 3) & ~3) - segment.Data.Length;
                    if (align > 0)
                        Array.Resize(ref segment.Data, segment.Data.Length + align);
                    image.Segments.Add(segment);
                }
            }
            return image;
        }

        public struct SPIFLASHSection
        {
            public ulong OffsetInFLASH;
            public byte[] Data;
        }

        public static SPIFLASHSection[] GetFLASHSections(ELFFile file)
        {
            List<SPIFLASHSection> sections = new List<SPIFLASHSection>();
            foreach (var sec in file.AllSections)
            {
                if (sec.VirtualAddress >= SPIFLASHBase && sec.VirtualAddress < SPIFLASHLimit)
                    sections.Add(new SPIFLASHSection { OffsetInFLASH = sec.VirtualAddress - SPIFLASHBase, Data = file.LoadSection(sec) });
            }
            return sections.ToArray();
        }
    }
}
