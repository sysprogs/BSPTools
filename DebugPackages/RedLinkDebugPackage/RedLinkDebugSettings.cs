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
        public string CommandLineArguments;
        public string[] StartupCommands;
        public ProgramMode ProgramMode = ProgramMode.Enabled;

        public RedLinkDebugSettings ShallowClone() => (RedLinkDebugSettings)MemberwiseClone();
    }
}
