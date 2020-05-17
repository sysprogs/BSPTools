using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace RedLinkDebugPackage
{
    [XmlType("com.visualgdb.edp.nxp.redlink")]
    public class RedLinkDebugSettings
    {
        public const string DefaultCommandLineArguments = "--server :$$SYS:GDB_PORT$$ -g -mi -2 -p$$REDLINK:DEVICE_ID$$ -vendor=$$REDLINK:VENDOR_ID$$ $$REDLINK:DEBUG_OPTIONS$$ -CoreIndex=$$REDLINK:CORE_INDEX$$ -x $$REDLINK:DEVICE_DIRECTORY$$ --flash-dir $$REDLINK:FLASH_DRIVER_DIRECTORY$$";
        public const string DefaultStartupCommands = "target remote :$$SYS:GDB_PORT$$";

        public string CommandLineArguments = DefaultCommandLineArguments;
        public string[] StartupCommands = DefaultStartupCommands.Split('\n');
        public ProgramMode ProgramMode = ProgramMode.Enabled;
        public bool AlwaysUseProbeSerialNumber;

        public RedLinkDebugSettings ShallowClone() => (RedLinkDebugSettings)MemberwiseClone();
    }
}
