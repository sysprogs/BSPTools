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
        struct PackCollection
        {
            public string Version, Status;
            public string[] FirmwareReleases;

            public override string ToString()
            {
                return Version;
            }
        }

        public static void FetchLatestSDKs(string sdkRoot, string cubeRoot)
        {
            Directory.CreateDirectory(sdkRoot);
            var xml = new XmlDocument();
            xml.Load(cubeRoot + @"\db\plugins\updater\STMupdaters.xml");

            var databases = xml.DocumentElement.ChildNodes.OfType<XmlElement>().First(e => e.Name == "DataBases");

            PackCollection[] packs = databases.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "DataBase").Select(db =>
            {
                var desc = db.ChildNodes.OfType<XmlElement>().First(e => e.Name == "PackDescription");

                return new PackCollection
                {
                    Version = desc.GetAttribute("Release"),
                    Status = desc.GetAttribute("Status"),
                    FirmwareReleases = db.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "FirmwareRelease").Select(e => e.GetAttribute("Release")).ToArray()
                };

            }).ToArray();

            var latestPack = packs.Where(p => p.Status == "New").ToArray();
            if (latestPack.Length != 1)
                throw new Exception("Unexpected count of 'New' packs in the STM32CubeMX database: " + latestPack.Length);

            var now = DateTime.Now;

            STM32SDKCollection expectedSDKs = new STM32SDKCollection
            {
                SDKs = latestPack[0].FirmwareReleases.Select(s => s.Split('.')).Where(c => c[0] == "FW").Select(c => new STM32SDKCollection.SDK(c)).ToArray(),
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
                Console.WriteLine($"Downloading {sdk.URL}...");
                string archive = Path.Combine(targetDir, Path.GetFileName(sdk.URL));
                wc.DownloadFile(sdk.URL, archive);
                Console.WriteLine($"Unpacking {Path.GetFileName(sdk.URL)}...");
                using (var za = new ZipArchive(File.OpenRead(archive)))
                    za.ExtractToDirectory(targetDir);

                XmlTools.SaveObject(sdk, markerFile);
                newSDKsFetched++;
            }

            if (newSDKsFetched > 0)
                XmlTools.SaveObject(expectedSDKs, Path.Combine(sdkRoot, SDKListFileName));
        }

        public const string SDKListFileName = "SDKs.xml";
    }

    public class STM32SDKCollection
    {
        public struct SDK
        {
            public string Family;
            public string Version;
            public string URL;

            public SDK(string[] specifier)
            {
                Family = specifier[1];
                Version = string.Join(".", specifier.Skip(2));
                string shortVersion = Version.Replace(".", "");

                URL = $"http://sw-center.st.com/packs/resource/firmware/stm32cube_fw_{Family.ToLower()}_v{shortVersion}.zip";
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
