using BSPEngine;
using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;

namespace ESP8266DebugPackage
{
    public class ESP8266DebugExtension : IDebugMethodExtension2
    {
        OpenOCDDebugConfigurator.DebugInterfaceList _Interfaces = new OpenOCDDebugConfigurator.DebugInterfaceList();
        bool _ESP32Mode;

        public ESP8266DebugExtension(bool esp32Mode)
        {
            _ESP32Mode = esp32Mode;
            try
            {
                _Interfaces = XmlTools.LoadObject<OpenOCDDebugConfigurator.DebugInterfaceList>(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "interfaces.xml"));
            }
            catch { }
        }

        public ESP8266DebugExtension()
            : this(false)
        {
        }

        public void AdjustDebugMethod(LoadedBSP.ConfiguredMCU mcu, ConfiguredDebugMethod method)
        {
            if (_ESP32Mode && method.Method.ID == "openocd")
            {
                if (!method.Parameters.ContainsKey("com.sysprogs.esp32.openocd.alg_timeout"))
                    method.Parameters["com.sysprogs.esp32.openocd.alg_timeout"] = "5000";
            }
        }

        QuickSetupDatabase _QuickSetupData = new QuickSetupDatabase();

        public ICustomBSPConfigurator CreateConfigurator(LoadedBSP.ConfiguredMCU mcu, DebugMethod method)
        {
            if (method.ID == "openocd")
                return new OpenOCDDebugConfigurator(method, _QuickSetupData);
            return null;
        }

        public IEnumerable<ICustomStartupSequenceBuilder> StartupSequences
        {
            get
            {
                if (_ESP32Mode)
                    return new ICustomStartupSequenceBuilder[] { new ESP32StartupSequence() };
                else
                    return new ICustomStartupSequenceBuilder[] { new ESP8266StartupSequence() };
            }
        }
    }

    public class ESP32DebugExtension : ESP8266DebugExtension
    {
        public ESP32DebugExtension()
            : base(true)
        {
        }
    }

}
