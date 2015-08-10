using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;

namespace LinkerScriptGenerator
{
    public class TarPacker
    {
        private static void WriteTextField(byte[] hdr, int offset, int maxLen, string val)
        {
            if (val == null)
                return;

            byte[] convertedFN = Encoding.UTF8.GetBytes(val);
            for (int i = 0; i < maxLen; i++)
            {
                if (i < convertedFN.Length)
                    hdr[offset + i] = convertedFN[i];
                else
                    hdr[offset + i] = 0;
            }
        }

        private static void WriteOctalField(byte[] hdr, int offset, int maxLen, long val)
        {
            string text = Convert.ToString(val, 8);
            if (text.Length < (maxLen - 1))
                text = text.Insert(0, new string('0', maxLen - 1 - text.Length));
            WriteTextField(hdr, offset, maxLen, text);
        }

        static System.DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

        public static byte[] CreateHeader(string fileName, bool isDir, long size, DateTime lastWriteTime, out int paddingSize, bool isLongLinkPseudoentry = false)
        {
            byte[] hdr = new byte[512];
            byte[] longLinkPrefix = null;

            fileName = fileName.Replace('\\', '/');
            if (isDir && !fileName.EndsWith("/"))
                fileName += "/";

            string fnPrefix = null;

            if (fileName.Length > 100)
            {
                int idx = fileName.Length - 100;
                idx = fileName.IndexOf('/', idx);
                if (idx != -1 && idx != fileName.Length - 1)
                {
                    fnPrefix = fileName.Substring(0, idx);
                    fileName = fileName.Substring(idx + 1);
                }
                else
                {
                    byte[] rawName = Encoding.UTF8.GetBytes(fileName);
                    int sectors = (rawName.Length + 511) / 512;
                    int unused;

                    longLinkPrefix = new byte[512 + sectors * 512];
                    CreateHeader("././@LongLink", false, rawName.Length, lastWriteTime, out unused, true).CopyTo(longLinkPrefix, 0);
                    longLinkPrefix[156] = (byte)'L';
                    rawName.CopyTo(longLinkPrefix, 512);
                }
            }

            paddingSize = (int)size & 511;
            if (paddingSize != 0)
                paddingSize = 512 - paddingSize;

            WriteTextField(hdr, 0, 100, fileName);

            if (isLongLinkPseudoentry)
            {
                WriteOctalField(hdr, 100, 8, 0);    //File mode
                WriteOctalField(hdr, 108, 8, 0);    //User ID
                WriteOctalField(hdr, 116, 8, 0);    //Group ID
                WriteOctalField(hdr, 136, 12, 0);    //Last modification time
            }
            else
            {
                WriteOctalField(hdr, 100, 8, Convert.ToInt32("755", 8));    //File mode
                WriteOctalField(hdr, 108, 8, 500);    //User ID
                WriteOctalField(hdr, 116, 8, 544);    //Group ID
                WriteOctalField(hdr, 136, 12, (long)(lastWriteTime - UnixEpoch).TotalSeconds);    //Last modification time
            }


            WriteOctalField(hdr, 124, 12, size);    //File size

            //            WriteTextField(hdr, 257, 8, "ustar  ");
            WriteTextField(hdr, 257, 8, "ustar\0" + "00");

            //             WriteTextField(hdr, 265, 32, userName);
            //             WriteTextField(hdr, 297, 32, groupName);

            WriteTextField(hdr, 345, 155, fnPrefix);

            if (isLongLinkPseudoentry)
                hdr[156] = (byte)'L';
            else if (isDir)
                hdr[156] = (byte)'5';
            else
                hdr[156] = (byte)'0';

            int checksum = 0x20 * 8;
            for (int i = 0; i < hdr.Length; i++)
                checksum += hdr[i];

            WriteOctalField(hdr, 148, 8, checksum);


            if (longLinkPrefix != null)
            {
                var result = new byte[longLinkPrefix.Length + hdr.Length];
                longLinkPrefix.CopyTo(result, 0);
                hdr.CopyTo(result, longLinkPrefix.Length);

                return result;
            }

            return hdr;
        }


        #region FindFirstFile() wrappers
        public const int MAX_PATH = 260;
        public const int MAX_ALTERNATE = 14;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public FileAttributes dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public UInt32 nFileSizeHigh;
            public UInt32 nFileSizeLow;
            public UInt32 dwReserved0;
            public UInt32 dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_ALTERNATE)]
            public string cAlternate;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindFirstFile(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll")]
        public static extern bool FindClose(IntPtr hFindFile);
        #endregion

        class SubdirInfo
        {
            public string Name;
            public DateTime Date;
        }

        static long MAKELONGLONG(int high, int low)
        {
            long tmp = high;
            tmp = tmp << 32;
            return tmp | (uint)low;
        }

        private static void CopyStreamWithProgress(Stream source, Stream dest, ref int filesDone, ref long bytesDone, byte[] tempBuffer, long len = -1)
        {
            long done = 0;
            if (len < 0)
                len = source.Length;

            while (done < len)
            {
                long todo = Math.Min(tempBuffer.LongLength, len - done);
                int cdone = source.Read(tempBuffer, 0, (int)todo);
                if (cdone <= 0)
                    break;
                dest.Write(tempBuffer, 0, cdone);
                done += cdone;
                bytesDone += cdone;
            }

            filesDone++;
        }

        static void ArchiveDirectoryToTARRecursively(Stream tarStream, string absoluteDirectory, string relativeDirectoryInUnixFormat, ref int filesDone, ref long bytesDone, byte[] paddingBuffer, byte[] tempBuffer, FileNameFilter filter)
        {
            WIN32_FIND_DATA findData;
            List<SubdirInfo> subdirs = new List<SubdirInfo>();
            IntPtr hFind = FindFirstFile(absoluteDirectory + "\\*.*", out findData);
            int paddingSize;
            if (hFind == (IntPtr)(-1))
                return;
            try
            {
                do
                {
                    if ((findData.cFileName == ".") || (findData.cFileName == ".."))
                        continue;

                    if ((findData.dwFileAttributes & FileAttributes.Directory) != 0)
                        subdirs.Add(new SubdirInfo { Name = findData.cFileName, Date = DateTime.FromFileTime(MAKELONGLONG(findData.ftLastWriteTime.dwHighDateTime, findData.ftLastWriteTime.dwLowDateTime)) });
                    else
                    {
                        string localFN = Path.Combine(absoluteDirectory, findData.cFileName);
                        if (filter != null && !filter(localFN))
                            continue;

                        using (var fsLocal = File.Open(localFN, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            string rel = relativeDirectoryInUnixFormat;
                            if (rel != "")
                                rel += "/";
                            rel += findData.cFileName;
                            var timestamp = DateTime.FromFileTime(MAKELONGLONG(findData.ftLastWriteTime.dwHighDateTime, findData.ftLastWriteTime.dwLowDateTime));

                            long len = fsLocal.Length;
                            var hdr = TarPacker.CreateHeader(rel, false, len, timestamp, out paddingSize);
                            tarStream.Write(hdr, 0, hdr.Length);
                            CopyStreamWithProgress(fsLocal, tarStream, ref filesDone, ref bytesDone, tempBuffer, len);
                            tarStream.Write(paddingBuffer, 0, paddingSize);
                        }
                    }

                } while (FindNextFile(hFind, out findData));
            }
            finally
            {
                FindClose(hFind);
            }

            foreach (var subdir in subdirs)
            {
                string rel = relativeDirectoryInUnixFormat;
                if (rel != "")
                    rel += "/";
                rel += subdir.Name;

                tarStream.Write(TarPacker.CreateHeader(rel, true, 0, subdir.Date, out paddingSize), 0, 512);
                ArchiveDirectoryToTARRecursively(tarStream, Path.Combine(absoluteDirectory, subdir.Name), rel, ref filesDone, ref bytesDone, paddingBuffer, tempBuffer, filter);
            }
        }

        public delegate bool FileNameFilter(string fn);

        public static void PackDirectoryToTGZ(string dir, string archive, FileNameFilter filter)
        {
            byte[] padding = new byte[1024];
            byte[] tmp = new byte[1024 * 1024];

            int filesDone = 0;
            long bytesDone = 0;

            using (var fs = File.Create(archive))
            using (var gs = new GZipStream(fs, CompressionMode.Compress))
            {
                ArchiveDirectoryToTARRecursively(gs, dir, "", ref filesDone, ref bytesDone, padding, tmp, filter);
                gs.Write(padding, 0, 1024);
            }

        }
    }
}
