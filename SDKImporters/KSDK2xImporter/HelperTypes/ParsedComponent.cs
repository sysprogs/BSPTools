using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KSDK2xImporter.HelperTypes
{
    enum ComponentType
    {
        LinkerScript,
        SVDFile,
        CMSIS_SDK,
        Other,
    }

    class ParsedComponent
    {
        public ComponentType Type;

        //public EmbeddedFramework Framework;
        public string OriginalName;
        public string OriginalType;

        public override string ToString()
        {
            return base.ToString();
            //return Framework?.UserFriendlyName;
        }

#if !DEBUG
        public EmbeddedProjectSample ToProjectSample(IEnumerable<string> extraReferences)
        {
            return new EmbeddedProjectSample
            {
                AdditionalSourcesToCopy = Framework.AdditionalSourceFiles
                    .Select(f => new AdditionalSourceFile { SourcePath = f, TargetFileName = Path.GetFileName(f) })
                    .Concat(new[] { new AdditionalSourceFile { SourcePath = "$$SYS:BSP_ROOT$$/" + ParsedSDK.MainFileName, TargetFileName = ParsedSDK.MainFileName } })
                    .ToArray(),
                AdditionalIncludeDirectories = Framework.AdditionalIncludeDirs,
                PreprocessorMacros = Framework.AdditionalPreprocessorMacros,
                RequiredFrameworks = LoadedBSP.Combine(Framework.RequiredFrameworks, extraReferences?.ToArray()),
                Name = "Empty project for " + OriginalName,
            };
        }
#endif
    }
}
