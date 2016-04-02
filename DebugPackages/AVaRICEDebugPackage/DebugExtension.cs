using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace AVaRICEDebugPackage
{
    public class AVaRICEDebugExtension : IDebugMethodExtension
    {
        static bool _AssemblyResolveHandlerInstalled;

        public void AdjustDebugMethod(LoadedBSP.ConfiguredMCU mcu, ConfiguredDebugMethod method)
        {
        }

        public AVaRICEDebugExtension()
        {
            if (!_AssemblyResolveHandlerInstalled)
            {
                _AssemblyResolveHandlerInstalled = true;
                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            }
        }

        public static string UsbDriverToolExe
        {
            get
            {
                var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + @"\drivers\UsbDriverTool.exe";
                return path;
            }
        }

        Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("UsbDriverTool,"))
            {
                try
                {
                    var ass = Assembly.LoadFile(UsbDriverToolExe);
                    return ass;
                }
                catch { }
            }

            return null;
        }

        public ICustomBSPConfigurator CreateConfigurator(LoadedBSP.ConfiguredMCU mcu, DebugMethod method)
        {
            return new AVaRICEDebugSettingsControl(method, mcu.BSP.Directory);
        }
    }
}
