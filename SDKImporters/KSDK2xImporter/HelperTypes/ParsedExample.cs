using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace KSDK2xImporter.HelperTypes
{
    class ParsedExample
    {
        public readonly string[] Dependencies;
        public readonly string ID, Description;
        public readonly string[] Defines;

        public override string ToString() => ID;

        public readonly string ExplicitFPUSetting;
        public readonly string LanguageStandard;
        public readonly string FPUType;

        public readonly ParsedSourceList[] SourceLists;
        public readonly string CoreID;
        public readonly string Category;

        public ParsedExample(string baseDirectory, XmlElement exampleNode)
        {
            ID = exampleNode.GetAttribute("id");
            Description = exampleNode.GetAttribute("brief");

            var externalNode = exampleNode.SelectSingleNode("external/files");
            if (externalNode != null)
            {
                var path = (externalNode.ParentNode as XmlElement)?.GetAttribute("path");
                var mask = (externalNode as XmlElement)?.GetAttribute("mask");
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(mask))
                {
                    var sampleFiles = Directory.GetFiles(Path.Combine(baseDirectory, path), mask);
                    var fn = sampleFiles?.FirstOrDefault();
                    if (fn != null)
                    {
                        XmlDocument doc2 = new XmlDocument();
                        doc2.Load(fn);
                        exampleNode = doc2.DocumentElement.SelectSingleNode("example") as XmlElement;
                        if (exampleNode == null)
                            throw new Exception("Failed to locate example node");
                    }
                }
            }

            Dependencies = exampleNode.GetAttribute("dependency").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Defines = exampleNode.SelectNodes("toolchainSettings/toolchainSetting/option[@id='gnu.c.compiler.option.preprocessor.def.symbols']/value").OfType<XmlElement>().
                        Select(node => node.InnerText.Replace("'\"", "'<").Replace("\"'", ">'")).ToArray();

            ExplicitFPUSetting = exampleNode.SelectSingleNode("toolchainSettings/toolchainSetting/option[@id='com.crt.advproject.gcc.fpu']")?.InnerText;
            LanguageStandard = exampleNode.SelectSingleNode("toolchainSettings/toolchainSetting/option[@id='com.crt.advproject.c.misc.dialect']")?.InnerText;
            FPUType = exampleNode.SelectSingleNode("toolchainSettings/toolchainSetting/option[@id='com.crt.advproject.gcc.fpu']")?.InnerText;
            SourceLists = exampleNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSourceList(e)).ToArray();
            CoreID = exampleNode.GetAttribute("device_core");
            Category = exampleNode.GetAttribute("category");
        }

        static int CountMatches(string[] left, string[] right)
        {
            int limit = Math.Min(left.Length, right.Length);
            for (int i = 0; i < limit; i++)
            {
                if (left[i] != right[i])
                    return i;
            }
            return limit;
        }

        public VendorSample BuildVendorSample(string rootDir, string boardName, SpecializedDevice device, string package, HashSet<string> allComponentIDs, HashSet<string> implicitFrameworks)
        {
            Dictionary<string, string> properties = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(ExplicitFPUSetting))
            {
                properties["com.sysprogs.bspoptions.arm.floatmode"] = ExplicitFPUSetting.Contains("hard") ? "-mfloat-abi=hard" : "-mfloat-abi=soft";
            }

            VendorSample sample = new VendorSample
            {
                DeviceID = device.MakeMCUID(package),
                UserFriendlyName = ID,
                Description = Description,
                BoardName = boardName,
                Configuration = new VendorSampleConfiguration
                {
                    Frameworks = Dependencies.Where(d => allComponentIDs.Contains(d)).Select(d => ParsedComponent.FrameworkIDPrefix + d).Concat(implicitFrameworks).Distinct().ToArray(),
                    MCUConfiguration = new PropertyDictionary2(properties)
                },
                VirtualPath = Category,

                NoImplicitCopy = true
            };

            List<string> sources = new List<string>(), headers = new List<string>();
            HashSet<string> includeDirectories = new HashSet<string>();

            string[] matchingPathComponents = null;

            foreach (var lst in SourceLists)
            {
                foreach(var file in lst.LocateAllFiles(device, rootDir))
                {
                    var bspPath = file.GetBSPPath();
                    UpdateMatchingPathComponents(bspPath, ref matchingPathComponents);

                    file.UpdateIncludeDirectoryList(includeDirectories);

                    switch (file.Type)
                    {
                        case SourceType.Library:
                        case SourceType.Source:
                            sources.Add(bspPath);
                            break;
                        case SourceType.Header:
                            headers.Add(bspPath);
                            break;
                        case SourceType.LinkerScript:
                            sample.LinkerScript = bspPath;
                            break;
                    }
                }
            }

            if (matchingPathComponents != null)
                sample.Path = string.Join("/", matchingPathComponents);

            sample.SourceFiles = sources.ToArray();
            sample.HeaderFiles = headers.ToArray();
            sample.IncludeDirectories = includeDirectories.ToArray();

            return sample;
        }

        private void UpdateMatchingPathComponents(string bspPath, ref string[] matchingComponents)
        {
            string[] components = bspPath.Split('/', '\\');
            if (matchingComponents == null)
                matchingComponents = components;
            else
            {
                int matches = CountMatches(matchingComponents, components);
                if (matches < matchingComponents.Length)
                    Array.Resize(ref matchingComponents, matches);
            }
        }
    }
}
