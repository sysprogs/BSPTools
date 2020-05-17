using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace RedLinkDebugPackage
{
    public class RedLinkDeviceDatabase
    {
        public readonly string BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VisualGDB\RedLinkDebugPackage");

        Dictionary<Key, KnownDevice> _KnownDevices = null;
        public const string DeviceSupportFilesFolder = ".mcuxpressoide_packages_support";

        public KnownDevice[] AllDevices
        {
            get
            {
                LoadKnownDevices();
                return _KnownDevices.Values.ToArray();
            }
        }

        public RedLinkDeviceDatabase()
        {
        }

        List<KnownDevice> ParseDeviceDefinitions(string directory, bool isOutsideOfCache)
        {
            List<KnownDevice> result = new List<KnownDevice>();
            if (Directory.Exists(directory))
            {
                foreach (var subdir in Directory.GetDirectories(directory))
                {
                    var partFiles = Directory.GetFiles(subdir, "*_part.xml");
                    if (partFiles.Length == 0)
                        partFiles = Directory.GetFiles(subdir, "*.xml");

                    foreach (var xmlFile in partFiles)
                    {
                        try
                        {
                            var xml = new XmlDocument();
                            xml.Load(xmlFile);
                            if (xml.DocumentElement.Name == "chips")
                            {
                                var vendor = xml.DocumentElement.GetAttribute("chipVendor");
                                foreach (var child in xml.DocumentElement.ChildNodes.OfType<XmlElement>())
                                {
                                    if (child.Name == "chip")
                                    {
                                        string name = child.GetAttribute("name");
                                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(vendor))
                                            result.Add(new KnownDevice(new Key(vendor, name), subdir, isOutsideOfCache));
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        public struct Key
        {
            public readonly string Vendor, Device;

            public Key(string vendor, string device)
            {
                Vendor = vendor;
                Device = device;
            }

            public override string ToString() => $"{Vendor} {Device}";
        }

        public class KnownDevice
        {
            public readonly Key Key;
            public string DefinitionDirectory { get; private set; }
            public bool IsOutsideOfCache { get; private set; }

            public KnownDevice(Key key, string directory, bool isOutsideOfCache)
            {
                Key = key;
                DefinitionDirectory = directory;
                IsOutsideOfCache = isOutsideOfCache;
            }

            public override string ToString() => Key.ToString();

            public bool MatchesDefinition(string vendor, string dev) => vendor != null && dev != null && Key.Vendor == vendor && Key.Device == dev;

            public string CopyToCacheDirectory(string baseDir)
            {
                var targetDir = Path.Combine(baseDir, Path.GetFileName(DefinitionDirectory));
                CopyDirectoryRecursively(DefinitionDirectory, targetDir);
                DefinitionDirectory = targetDir;
                IsOutsideOfCache = false;
                return targetDir;
            }
        }

        public KnownDevice ProvideDeviceDefinition(string vendorID, string deviceID)
        {
            LoadKnownDevices();

            if (_KnownDevices.TryGetValue(new Key(vendorID, deviceID), out var device))
            {
                if (device.IsOutsideOfCache)
                    device.CopyToCacheDirectory(BaseDirectory);

                return device;
            }

            return null;
        }

        private void LoadKnownDevices()
        {
            if (_KnownDevices == null)
            {
                _KnownDevices = new Dictionary<Key, KnownDevice>();
                Directory.CreateDirectory(BaseDirectory);
                foreach (var dev in ParseDeviceDefinitions(BaseDirectory, false))
                    _KnownDevices[dev.Key] = dev;

                try
                {
                    foreach(var docSubdir in Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))
                    {
                        var supportFolder = Path.Combine(docSubdir, "workspace\\" + DeviceSupportFilesFolder);
                        if (Directory.Exists(supportFolder))
                        {
                            try
                            {
                                foreach (var dev in ParseDeviceDefinitions(supportFolder, true))
                                    _KnownDevices[dev.Key] = dev;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public int ImportDefinitionsFromFolder(string dir)
        {
            HashSet<string> handledDirs = new HashSet<string>();
            int count = 0;

            foreach (var dev in ParseDeviceDefinitions(dir, true))
            {
                count++;
                if (handledDirs.Contains(dev.DefinitionDirectory))
                    continue;

                dev.CopyToCacheDirectory(BaseDirectory);
                handledDirs.Add(dev.DefinitionDirectory);
            }

            _KnownDevices = null;
            LoadKnownDevices();

            return count;
        }

        public static void CopyDirectoryRecursively(string sourceDirectory, string destinationDirectory)
        {
            sourceDirectory = sourceDirectory.TrimEnd('/', '\\');
            destinationDirectory = destinationDirectory.TrimEnd('/', '\\');
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                string relPath = file.Substring(sourceDirectory.Length + 1);
                File.Copy(file, Path.Combine(destinationDirectory, relPath), true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDirectory))
            {
                string relPath = dir.Substring(sourceDirectory.Length + 1);
                string newDir = Path.Combine(destinationDirectory, relPath);
                if (!Directory.Exists(newDir))
                    Directory.CreateDirectory(newDir);
                CopyDirectoryRecursively(dir, newDir);
            }
        }

    }
}
