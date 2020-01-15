using BSPEngine;
using BSPGenerationTools;
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
            var catalogFile = cubeRoot + @"\db\plugins\updater\STMupdaters.xml";
            var daysOld = (DateTime.Now - File.GetLastWriteTime(catalogFile)).TotalDays;
            if (daysOld > 14)
                throw new Exception($"STM32CubeMX device list {daysOld:f0} days old. Please update STM32CubeMX.");

            WebClient wc0 = new WebClient();
            wc0.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:72.0) Gecko/20100101 Firefox/72.0";
            wc0.Headers[HttpRequestHeader.Accept] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";
            wc0.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.5";

            var onlineCatalog = wc0.DownloadData("https://www.st.com/resource/en/utility2/updaters.zip");
            using (var archive = new ZipArchive(new MemoryStream(onlineCatalog)))
            {
                using (var stream = archive.GetEntry("STMupdaters.xml").Open())
                {
                    byte[] data = new byte[65536];
                    MemoryStream ms = new MemoryStream();
                    for (; ; )
                    {
                        int done = stream.Read(data, 0, data.Length);
                        if (done == 0)
                            break;
                        ms.Write(data, 0, done);
                    }

                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    xml.LoadXml(text);
                }
            }

            //xml.Load(catalogFile);

            var firmwaresNode = xml.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.Name == "Firmwares");
            List<ReleaseDefinition> releases = new List<ReleaseDefinition>();
            foreach (var firmwareNode in firmwaresNode.OfType<XmlElement>().Where(e => e.Name == "Firmware"))
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
                    bestReleaseForEachFamily.Add(newReleases[0]);
                else if (newReleases.Length > 1)
                {
                    var newReleasesByMajor = newReleases.GroupBy(r => r.Release.Value).ToArray();

                    var bestReleaseGroup = newReleasesByMajor.OrderByDescending(r => r.Key, new SimpleVersionComparer()).First();
                    var bestRelease = bestReleaseGroup.OrderByDescending(r => r.Patch.Value).First();

                    bestReleaseForEachFamily.Add(bestRelease);
                }
                else
                    throw new Exception($"Don't know how to pick the best release for {g.Key}. Investigate and add missing logic here.");
            }

            WebClient wc = new WebClient();
            var now = DateTime.Now;

            STM32SDKCollection expectedSDKs = new STM32SDKCollection
            {
                SDKs = bestReleaseForEachFamily.Select(c => new STM32SDKCollection.SDK(c)).ToArray(),
                BSPVersion = $"{now.Year}.{now.Month:d2}",
            };

            const string MarkerFileName = "BSPGeneratorSDK.xml";
            int newSDKsFetched = 0;

            foreach (var sdk in expectedSDKs.SDKs)
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
                foreach (var e in za.Entries)
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
