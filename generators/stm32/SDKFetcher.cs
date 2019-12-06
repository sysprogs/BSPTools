using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace stm32_bsp_generator
{
    class SDKFetcher
    {
        public static void FetchLatestSDKs(string sdkRoot, string cubeRoot)
        {
            Directory.CreateDirectory(sdkRoot);
            var xml = new XmlDocument();
            xml.Load(cubeRoot + @"\db\plugins\updater\STMupdaters.xml");

            var firmwaresNode = xml.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.Name == "Firmwares");
            List<ReleaseDefinition> releases = new List<ReleaseDefinition>();
            foreach(var firmwareNode in firmwaresNode.OfType<XmlElement>().Where(e => e.Name == "Firmware"))
            {
                var packDescriptionNodes = firmwareNode.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "PackDescription").ToArray();
                if (packDescriptionNodes.Length == 0)
                    continue;

                foreach (var node in packDescriptionNodes)
                {
                    var r = new ReleaseDefinition(node);
                    if (r.Release.IsValid)
                        releases.Add(r);
                }
            }

            List<ReleaseDefinition> bestReleaseForEachFamily = new List<ReleaseDefinition>();

            foreach (var g in releases.GroupBy(r => r.Family))
            {
                var newReleases = g.Where(r => r.Status == "New").ToArray();
                if (newReleases.Length > 1 && newReleases.Count(r => r.HasPatch) > 0)
                    newReleases = newReleases.Where(r => r.HasPatch).ToArray(); //Prefer patched releases to unpatched ones.

                if (newReleases.Length == 0 && g.Count() == 1) //Experimental pre-release family
                {
                    continue;
                }

                if (newReleases.Length == 1)
                {
                    bestReleaseForEachFamily.Add(newReleases[0]);
                    continue;
                }

                throw new Exception($"Don't know how to pick the best release for {g.Key}. Investigate and add missing logic here.");
            }
            
            var now = DateTime.Now;

            STM32SDKCollection expectedSDKs = new STM32SDKCollection
            {
                SDKs = bestReleaseForEachFamily.Select(c => new STM32SDKCollection.SDK(c)).ToArray(),
                BSPVersion = $"{now.Year}.{now.Month:d2}",
            };

            WebClient wc = new WebClient();
            const string MarkerFileName = "BSPGeneratorSDK.xml";
            int newSDKsFetched = 0;

            foreach(var sdk in expectedSDKs.SDKs)
            {
                string targetDir = Path.Combine(sdkRoot, sdk.FolderName);
                string markerFile = Path.Combine(targetDir, MarkerFileName);
                if (File.Exists(markerFile))
                {
                    var oldSDK = XmlTools.LoadObject<STM32SDKCollection.SDK>(markerFile);
                    if (oldSDK.Equals(sdk))
                        continue;
                    File.Delete(markerFile);
                }

                Directory.CreateDirectory(targetDir);
                DownloadAndUnpack(wc, sdk.URL, targetDir);

                if (sdk.PatchURL != null)
                    DownloadAndUnpack(wc, sdk.PatchURL, targetDir);

                XmlTools.SaveObject(sdk, markerFile);
                newSDKsFetched++;
            }

            if (newSDKsFetched > 0)
                XmlTools.SaveObject(expectedSDKs, Path.Combine(sdkRoot, SDKListFileName));
        }

        private static void DownloadAndUnpack(WebClient wc, string URL, string targetDir)
        {
            Console.WriteLine($"Downloading {URL}...");
            string archive = Path.Combine(targetDir, Path.GetFileName(URL));
            wc.DownloadFile(URL, archive);
            Console.WriteLine($"Unpacking {Path.GetFileName(URL)}...");
            using (var za = new ZipArchive(File.OpenRead(archive)))
            {
                foreach(var e in za.Entries)
                {
                    var targetPath = Path.Combine(targetDir, e.FullName);
                    if (e.FullName.EndsWith("/"))
                    {
                        if (e.Length != 0)
                            throw new Exception("Unexpected directory entry");
                        Directory.CreateDirectory(targetPath);
                    }
                    else
                        e.ExtractToFile(targetPath, true);
                }
            }
        }

        public const string SDKListFileName = "SDKs.xml";
    }

    internal struct STPackVersion
    {
        public readonly string[] Specifier;
        public readonly string Value;
        public override string ToString() => Value;
        public bool IsValid => !string.IsNullOrEmpty(Value);

        public STPackVersion(string value)
        {
            Value = null;
            Specifier = null;

            if (!string.IsNullOrEmpty(value))
            {
                var specifier = value.Split('.');
                if (specifier.Length > 2 && specifier[0] == "FW")
                {
                    Value = value;
                    Specifier = value.Split('.');
                }
            }
        }

        public string Version => string.Join(".", Specifier.Skip(2));

        public string URL
        {
            get
            {
                string shortVersion = Version.Replace(".", "");
                return $"http://sw-center.st.com/packs/resource/firmware/stm32cube_fw_{Family.ToLower()}_v{shortVersion}.zip";
            }
        }

        public string Family => Specifier[1];
    }

    internal struct ReleaseDefinition
    {
        public readonly string Status;
        public readonly STPackVersion Release, Patch;

        public bool HasPatch => Patch.IsValid;
        public string Family => Release.Family;

        public ReleaseDefinition(XmlElement e)
        {
            Release = new STPackVersion(e.GetAttribute("Release"));
            Patch = new STPackVersion(e.GetAttribute("Patch"));
            Status = e.GetAttribute("Status");
        }

        public override string ToString()
        {
            return Release.ToString();
        }
    }

    public class STM32SDKCollection
    {
        public struct SDK
        {
            public string Family;
            public string Version, PatchVersion;
            public string URL, PatchURL;

            internal SDK(ReleaseDefinition release)
            {
                Family = release.Family;
                Version = release.Release.Version;

                if (release.HasPatch)
                {
                    URL = release.Release.URL;
                    PatchVersion = release.Patch.Version;
                    PatchURL = release.Patch.URL;
                }
                else
                {
                    URL = release.Release.URL;
                    PatchVersion = null;
                    PatchURL = null;
                }
            }

            public string FolderName => $"{Family}_{Version}";

            public override string ToString()
            {
                return $"{Family} {Version}";
            }
        }

        public SDK[] SDKs;
        public string BSPVersion;
    }
}
