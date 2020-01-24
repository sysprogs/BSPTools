using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace STM32IDEProjectImporter
{
    public class STM32IDEProjectImporter : IExternalProjectImporter
    {
        public string Name => "STM32IDE";

        public string ImportCommandText => "Import an existing STM32IDE/SW4STM32 Project";

        public string ProjectFileFilter => "Eclipse project files|*.cproject";
        public string HelpText => null;
        public string HelpURL => null;

        public string UniqueID => "com.sysprogs.project_importers.stm32.ide";
        public object SettingsControl => null;
        public object Settings { get; set; }


        class ParserImpl : SW4STM32ProjectParserBase
        {
            protected override void OnParseFailed(Exception ex, string sampleID, string projectFileDir, string warningText)
            {
                throw ex;
            }
        }

        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            var parser = new ParserImpl();
            List<VendorSample> result = new List<VendorSample>();
            parser.ParseSingleProject(null, parameters.ProjectFile, null, null, null, SW4STM32ProjectParserBase.ProjectSubtype.Auto, result);
            if (result.Count == 0)
                throw new Exception("Failed to parse the project file");

            ImportedExternalProject.ConstructedVirtualDirectory rootDir = new ImportedExternalProject.ConstructedVirtualDirectory();

            var sample = result[0];
            foreach(var src in sample.SourceFiles ?? new string[0])
            {
                rootDir.AddFile(src, false);
            }

            foreach (var src in sample.HeaderFiles ?? new string[0])
            {
                rootDir.AddFile(src, true);
            }

            return new ImportedExternalProject
            {
                DeviceNameMask = new Regex(sample.DeviceID),
                OriginalProjectFile = parameters.ProjectFile,
                RootDirectory = rootDir,
                GNUTargetID = "arm-eabi",
                ReferencedFrameworks = new string[0],

                MCUConfiguration = sample.Configuration.MCUConfiguration,

                Configurations = new[]
                {
                    new ImportedExternalProject.ImportedConfiguration
                    {
                        Settings = new ImportedExternalProject.InvariantProjectBuildSettings
                        {
                            IncludeDirectories = sample.IncludeDirectories,
                            PreprocessorMacros = sample.PreprocessorMacros,
                        }
                    }
                }
            };
        }
    }
}
