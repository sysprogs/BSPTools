using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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


        public ImportedExternalProject ImportProject(ProjectImportParameters parameters, IProjectImportService service)
        {
            throw new NotImplementedException();
        }
    }
}
