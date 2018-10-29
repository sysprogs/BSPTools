using BSPEngine;
using ESP8266DebugPackage.GUI;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ESP8266DebugPackage
{
    class ESP32StubDebugController : IDebugMethodController, ICustomSettingsTypeProvider
    {
        public ICustomSettingsTypeProvider TypeProvider => this;

        public bool SupportsConnectionTesting => false;

        public Type[] SettingsObjectTypes => new[] { typeof(ESP32GDBStubSettings) };

        public ICustomDebugMethodConfigurator CreateConfigurator(LoadedBSP.LoadedDebugMethod method, IBSPConfiguratorHost host)
        {
            return new ESP32GDBStubSettingsControl(host, this);
        }

        public IGDBStubInstance StartGDBStub(IDebugStartService startService, DebugStartContext context)
        {
            var settings = context.Configuration as ESP32GDBStubSettings;
            if (settings == null)
                throw new Exception("Missing ESP32 stub settings");

            var svc = startService.AdvancedDebugService as IExternallyProgrammedProjectDebugService ?? throw new Exception("This project type does not support external FLASH memory programming");
            svc.ProgramFLASHMemoryUsingExternalTool(settings.ProgramMode);

            startService.GUIService.Report("The FLASH memory has been programmed, however the ESP32 GDB stub does not support live debugging yet. Please use JTAG if you want to debug your program or reset your board to run the program without debugging.");
            throw new OperationCanceledException();
        }

        public object TryConvertLegacyConfiguration(IBSPConfiguratorHost host, string methodDirectory, Dictionary<string, string> legacyConfiguration)
        {
            return null;
        }
    }
}
