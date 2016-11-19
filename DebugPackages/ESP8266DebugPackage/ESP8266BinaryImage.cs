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
        public readonly string Hint;

        public ArgumentValueAttribute(string name, string hint = null)
        {
            Name = name;
            Hint = hint;
        }
    }

    public class ESP8266BinaryImage
    {
        public struct Segment
        {
            public string Hint;
            public uint Address;
            public byte[] Data;

            const uint IROM_MAP_START = 0x400d0000;
            const uint IROM_MAP_END = 0x40400000;
            const uint DROM_MAP_START = 0x3F400000;
            const uint DROM_MAP_END = 0x3F700000;

            public bool IsESP32FLASHSegment
            {
                get
                {
                    return (IROM_MAP_START <= Address && Address < IROM_MAP_END) || (DROM_MAP_START <= Address && Address < DROM_MAP_END);
                }
            }


            public override string ToString()
            {
                return $"{Hint} @0x{Address:x8}";
            }

            public void Save(Stream stream)
            {
                stream.Write(BitConverter.GetBytes(Address), 0, 4);
                stream.Write(BitConverter.GetBytes(Data.Length), 0, 4);
                stream.Write(Data, 0, Data.Length);
            }
        }

        public enum FLASHMode
        {
            [ArgumentValue("qio", "QIO")]
            QIO = 0,
            [ArgumentValue("qout", "QOUT")]
            QOUT,
            [ArgumentValue("dio", "DIO")]
            DIO,
            [ArgumentValue("dout", "DOUT")]
            DOUT
        }

        public enum FLASHSize
        {
            [ArgumentValue("4m", "512KB (4mbit)")]
            size4M = 0,
            [ArgumentValue("2m", "256KB (2mbit)")]
            size2M,
            [ArgumentValue("8m", "1MB (8mbit)")]
            size8M,
            [ArgumentValue("16m", "2MB (16mbit)")]
            size16M,
            [ArgumentValue("32m", "4MB (32mbit)")]
            size32M,
            [ArgumentValue("16m-c1", "2MB - c1")]
            size16M_c1,
            [ArgumentValue("32m-c1", "4MB - c1")]
            size32M_c1,
            [ArgumentValue("32m-c2", "4MB - c2")]
            size32M_c2,
        }

        public enum FLASHFrequency
        {
            [ArgumentValue("40m", "40 MHz")]
            freq40M = 0,
            [ArgumentValue("26m", "26 MHz")]
            freq26M,
            [ArgumentValue("20m", "20 MHz")]
            freq20M,
            [ArgumentValue("80m", "80 MHz")]
            freq80M = 0x0f
        }

        public static _Ty ParseEnumValue<_Ty>(string arg)
        {
            foreach (FieldInfo fld in typeof(_Ty).GetFields(BindingFlags.Public | BindingFlags.Static))
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
        public byte AppNumber;

        public void Save(Stream stream)
        {
            byte checksum = 0xef;
            long offBase = stream.Position;
            byte[] rawHdr;

            var segments = Segments;

            if (!ESP32Mode && BootloaderImageOffset != 0)
            {
                rawHdr = new byte[] { 0xea, (byte)Segments.Count, 0, AppNumber };
                stream.Write(rawHdr, 0, rawHdr.Length);
                stream.Write(BitConverter.GetBytes(EntryPoint), 0, 4);
                segments[0].Save(stream);

                segments = new List<Segment>(Segments);
                segments.RemoveAt(0);
            }

            rawHdr = new byte[] { 0xe9, (byte)segments.Count, (byte)Header.Mode, (byte)(((byte)Header.Size << 4) | (byte)Header.Frequency) };
            stream.Write(rawHdr, 0, rawHdr.Length);
            stream.Write(BitConverter.GetBytes(EntryPoint), 0, 4);

            if (ESP32Mode)
                stream.Write(new byte[16], 0, 16);

            var sortedSegments = new List<Segment>(segments);
            sortedSegments.Sort((a, b) => (int)a.Address.CompareTo(b.Address));

            const uint IROM_ALIGN = 65536;
            const uint SEG_HEADER_LEN = 8;
            int paddingSegmentCount = 0;

            foreach (var segment in sortedSegments)
            {
                if (segment.IsESP32FLASHSegment)
                {
                    uint align_past = (segment.Address % IROM_ALIGN) - SEG_HEADER_LEN;
                    var pad_len = (IROM_ALIGN - ((stream.Position - offBase) % IROM_ALIGN)) + align_past - SEG_HEADER_LEN;

                    while (pad_len < 0)
                        pad_len += IROM_ALIGN;
                    if (pad_len > 0)
                    {
                        var paddingSegment = new Segment { Address = 0, Data = new byte[pad_len], Hint = "padding" };
                        paddingSegmentCount++;
                        paddingSegment.Save(stream);
                    }
                }

                segment.Save(stream);
                UpdateChecksum(ref checksum, segment.Data);
            }

            int alignment = (int)(15 - ((stream.Position - offBase) % 16));
            stream.Write(new byte[alignment], 0, alignment);
            stream.WriteByte(checksum);

            stream.Position = offBase + 1;
            stream.WriteByte((byte)(segments.Count + paddingSegmentCount));

            if (!ESP32Mode && BootloaderImageOffset != 0)
            {
                long endOff = stream.Position;
                stream.Position = offBase;
                byte[] buf = new byte[0x10000];
                uint crc32 = 0;
                while (stream.Position < endOff)
                {
                    int todo = Math.Min(buf.Length, (int)(endOff - stream.Position));
                    if (stream.Read(buf, 0, todo) != todo)
                        throw new IOException("Failed to read data from the generated image");

                    crc32 = CRC32.Update(crc32, buf, todo);
                }

                uint fixedCRC = 0;
                if (((int)crc32) < 0)
                    fixedCRC = (uint)-(crc32 + 1);
                else
                    fixedCRC = crc32 + 1;

                stream.Write(BitConverter.GetBytes(fixedCRC), 0, 4);
            }
        }

        const uint SPIFLASHBase = 0x40200000;
        const uint SPIFLASHLimit = 0x40400000;
        public ulong BootloaderImageOffset { get; private set; }

        public static ESP8266BinaryImage MakeNonBootloaderImageFromELFFile(ELFFile file, ParsedHeader header, bool esptoolSectionOrder = false)
        {
            ESP8266BinaryImage image = new ESP8266BinaryImage();
            image.EntryPoint = file.ELFHeader.e_entry;
            image.Header = header;
            InsertSections(file, esptoolSectionOrder, image, true);
            return image;
        }

        public static ESP8266BinaryImage MakeESP32ImageFromELFFile(ELFFile file, ParsedHeader header)
        {
            ESP8266BinaryImage image = new ESP8266BinaryImage(true);
            image.EntryPoint = file.ELFHeader.e_entry;
            image.Header = header;
            InsertSections(file, false, image, false);
            return image;
        }

        private static void InsertSections(ELFFile file, bool esptoolSectionOrder, ESP8266BinaryImage image, bool ramSectionsOnly)
        {
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
                if (!sec.HasData || !sec.PresentInMemory)
                {
                    if (sec.SectionName != ".rtc.text")
                        continue;
                }

                if (!ramSectionsOnly || (sec.VirtualAddress < SPIFLASHBase))
                {
                    var segment = new Segment { Address = sec.VirtualAddress, Data = file.LoadSection(sec), Hint = sec.SectionName };
                    int align = ((segment.Data.Length + 3) & ~3) - segment.Data.Length;
                    if (align > 0)
                        Array.Resize(ref segment.Data, segment.Data.Length + align);
                    image.Segments.Add(segment);
                }
            }
        }

        const int BootloaderImageHeaderSize = 0x10;
        public readonly bool ESP32Mode;

        public ESP8266BinaryImage(bool esp32Mode = false)
        {
            ESP32Mode = esp32Mode;
        }

        public static ESP8266BinaryImage MakeBootloaderBasedImageFromELFFile(ELFFile file, ParsedHeader header, int appNumber, bool esptoolSectionOrder = false)
        {
            ESP8266BinaryImage image = new ESP8266BinaryImage();
            image.EntryPoint = file.ELFHeader.e_entry;
            image.Header = header;

            var flashSections = GetFLASHSections(file);
            if (flashSections.Length != 1)
                throw new Exception($"Unexpected count of SPI FLASH sections: {flashSections.Length}. Cannot detect image type");

            int newSize = ((flashSections[0].Data.Length + 15) & ~15);
            Array.Resize(ref flashSections[0].Data, newSize);

            image.Segments.Add(new Segment { Address = 0, Data = flashSections[0].Data });
            image.BootloaderImageOffset = flashSections[0].OffsetInFLASH - BootloaderImageHeaderSize;
            image.AppNumber = (byte)appNumber;

            InsertSections(file, esptoolSectionOrder, image, true);
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

        public static int DetectAppMode(ELFFile elfFile, out string status)
        {
            var flashSections = GetFLASHSections(elfFile);

            if (flashSections.Length != 1)
            {
                status = $"Unexpected number of SPI FLASH sections: {flashSections.Length}. Assuming this is not an OTA layout.";
                return 0;
            }
            else if ((flashSections[0].OffsetInFLASH & 0x1FFF) == 0)
            {
                status = string.Format("Found an SPI FLASH section at 0x{0:x8}. This image is a non-OTA image.", flashSections[0].OffsetInFLASH);
                return 0;
            }
            else
            {
                int appMode;
                if (flashSections[0].OffsetInFLASH == 0x1010)
                    appMode = 1;
                else
                    appMode = 2;

                status = string.Format("Found an SPI FLASH section at 0x{0:x8}. This image is an APP{1} image.", flashSections[0].OffsetInFLASH, appMode);
                return appMode;
            }
        }
    }
}
