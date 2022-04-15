using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace BSPGenerationTools
{
    public class ZipFile
    {
        protected readonly Stream _Stream;

        public struct Entry
        {
            public string FileName;
            public ushort CompressionMethod;
            public ushort FileTime;
            public ushort FileDate;
            public uint Crc32;
            public uint CompressedSize;
            public uint UncompressedSize;
            public bool IsDirectory;

            public uint Offset;

            public override string ToString()
            {
                return FileName;
            }
        }

        List<Entry> _Entries = new List<Entry>();
        public readonly ulong TotalCompressedSize, TotalUncompressedSize;

        public IEnumerable<Entry> Entries
        {
            get { return _Entries; }
        }

        public ZipFile(Stream stream)   //Will not dispose it
        {
            _Stream = stream;

            byte[] buffer = new byte[65536 + 32];

            stream.Seek(Math.Max(0, stream.Length - buffer.Length), SeekOrigin.Begin);
            int bytesRead = stream.Read(buffer, 0, buffer.Length);

            uint centralDirectoryStart = uint.MaxValue, centralDirectorySize = uint.MaxValue;
            ushort centralDirectoryEntries = 0;

            for (int i = bytesRead - 4; i >= 0; i--)
            {
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                {
                    ushort commentLength = BitConverter.ToUInt16(buffer, i + 20);
                    int endOfRecord = i + commentLength + 22;
                    if (endOfRecord == bytesRead)
                    {
                        centralDirectoryStart = BitConverter.ToUInt32(buffer, i + 16);
                        centralDirectorySize = BitConverter.ToUInt32(buffer, i + 12);
                        centralDirectoryEntries = BitConverter.ToUInt16(buffer, i + 10);
                        break;
                    }
                }
            }

            if (centralDirectoryStart == uint.MaxValue || centralDirectorySize == uint.MaxValue)
                throw new Exception("Cannot find end of central directory record in ZIP file");

            if (centralDirectoryStart >= stream.Length || (centralDirectoryStart + centralDirectorySize > stream.Length))
                throw new Exception("Invalid central directory offset/size in ZIP file");

            buffer = new byte[centralDirectorySize];
            stream.Seek(centralDirectoryStart, SeekOrigin.Begin);
            if (stream.Read(buffer, 0, (int)centralDirectorySize) != (int)centralDirectorySize)
                throw new Exception("Cannot read central directory from the zip file");

            for (int i = 0; i < centralDirectorySize;)
            {
                if (buffer[i] != 0x50 || buffer[i + 1] != 0x4b || buffer[i + 2] != 0x01 || buffer[i + 3] != 0x02)
                    throw new Exception("Corrupt central directory file header in ZIP file");

                Entry entry;

                entry.CompressionMethod = BitConverter.ToUInt16(buffer, i + 10);
                entry.FileTime = BitConverter.ToUInt16(buffer, i + 12);
                entry.FileDate = BitConverter.ToUInt16(buffer, i + 14);
                entry.Crc32 = BitConverter.ToUInt32(buffer, i + 16);
                entry.CompressedSize = BitConverter.ToUInt32(buffer, i + 20);
                entry.UncompressedSize = BitConverter.ToUInt32(buffer, i + 24);

                entry.Offset = BitConverter.ToUInt32(buffer, i + 42);

                ushort fileNameLength = BitConverter.ToUInt16(buffer, i + 28);
                ushort extraFieldLength = BitConverter.ToUInt16(buffer, i + 30);
                ushort fileCommentLength = BitConverter.ToUInt16(buffer, i + 32);

                entry.FileName = Encoding.UTF8.GetString(buffer, i + 46, fileNameLength);
                entry.IsDirectory = entry.FileName.EndsWith("/");

                i += 46 + fileNameLength + extraFieldLength + fileCommentLength;

                TotalCompressedSize += entry.CompressedSize;
                TotalUncompressedSize += entry.UncompressedSize;

                _Entries.Add(entry);
            }
        }

        public delegate void ZipProgressHandler(ulong done, ulong total);

        public void ExtractEntry(Entry entry, Stream outputStream, ZipProgressHandler progressHandler = null)
        {
            if (entry.Offset >= _Stream.Length)
                throw new Exception("Invalid ZIP entry offset");

            _Stream.Seek(entry.Offset, SeekOrigin.Begin);

            byte[] buffer = new byte[1024 * 1024];

            _Stream.Read(buffer, 0, 30);
            ushort fileNameLength = BitConverter.ToUInt16(buffer, 26);
            ushort extraFieldLength = BitConverter.ToUInt16(buffer, 28);

            _Stream.Seek(entry.Offset + 30 + fileNameLength + extraFieldLength, SeekOrigin.Begin);

            Stream dataStream;

            switch (entry.CompressionMethod)
            {
                case 0:
                    dataStream = _Stream;
                    break;
                case 8:
                    dataStream = new System.IO.Compression.DeflateStream(_Stream, System.IO.Compression.CompressionMode.Decompress, true);
                    break;
                default:
                    throw new Exception("Unsupported ZIP compression method: " + entry.CompressionMethod + " for " + entry.FileName);
            }

            uint done = 0;
            while (done < entry.UncompressedSize)
            {
                int todo = (int)Math.Min(entry.UncompressedSize - done, (uint)buffer.Length);
                if (dataStream.Read(buffer, 0, todo) != todo)
                    throw new Exception("Cannot read data from ZIP stream for " + entry.FileName);

                outputStream.Write(buffer, 0, todo);

                done += (uint)todo;

                if (progressHandler != null)
                    progressHandler(done, entry.UncompressedSize);
            }

            if (dataStream != _Stream)
                dataStream.Dispose();
        }


        public byte[] ExtractEntry(Entry entry)
        {
            using (var ms = new MemoryStream())
            {
                ExtractEntry(entry, ms);
                return ms.ToArray();
            }
        }

        public XmlDocument ExtractXMLFile(Entry entry)
        {
            using (var ms = new MemoryStream())
            {
                ExtractEntry(entry, ms);
                ms.Position = 0;
                var xml = new XmlDocument();
                xml.Load(ms);
                return xml;
            }
        }

        public static DisposableZipFile Open(string fn) => new DisposableZipFile(fn);
    }


    public class DisposableZipFile : ZipFile, IDisposable
    {
        public DisposableZipFile(string fn)
            : base(new FileStream(fn, FileMode.Open, FileAccess.Read))
        {
        }

        public void Dispose()
        {
            _Stream.Dispose();
        }
    }
}
