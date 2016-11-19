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
        ESP8266DebugConfigurator.DebugInterfaceList _Interfaces = new ESP8266DebugConfigurator.DebugInterfaceList();
        bool _ESP32Mode;

        public ESP8266DebugExtension(bool esp32Mode)
        {
            _ESP32Mode = esp32Mode;
            try
            {
                _Interfaces = XmlTools.LoadObject<ESP8266DebugConfigurator.DebugInterfaceList>(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "interfaces.xml"));
            }
            catch { }
        }

        public ESP8266DebugExtension()
            : this(false)
        {
        }

        public void AdjustDebugMethod(LoadedBSP.ConfiguredMCU mcu, ConfiguredDebugMethod method)
        {
            string iface;
            if (method.Method.ID == "xt-ocd")
            {
                if (method.Parameters.TryGetValue("com.sysprogs.esp8266.xt-ocd.debug_iface", out iface))
                {
                    ESP8266DebugConfigurator.DebugInterface ifaceObj = null;
                    foreach (var obj in _Interfaces.Interfaces)
                        if (obj.ID == iface)
                        {
                            ifaceObj = obj;
                            break;
                        }

                    if (ifaceObj != null)
                    {
                        string templateFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "topology-template.xml");
                        XmlDocument doc = new XmlDocument();
                        doc.Load(templateFile);

                        XmlElement el = doc.CreateElement("controller");
                        el.SetAttribute("id", "Controller0");
                        el.SetAttribute("module", ifaceObj.Module);
                        foreach (var p in ifaceObj.Parameters)
                        {
                            string val;
                            if (method.Parameters.TryGetValue(ESP8266DebugConfigurator.InterfaceSettingPrefix + p.UniqueID, out val) && !string.IsNullOrEmpty(val))
                                el.SetAttribute(p.UniqueID, val);
                        }
                        doc.DocumentElement.InsertBefore(el, doc.DocumentElement.FirstChild);

                        string newFn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VisualGDB\xt-ocd-topology.xml");
                        doc.Save(newFn);
                        method.Method.GDBServerArguments.Template = method.Method.GDBServerArguments.Template.Replace("$$com.sysprogs.esp8266.xt-ocd.configfile$$", "\"" + newFn + "\"");
                    }
                }
            }
            else if (_ESP32Mode && method.Method.ID == "openocd")
            {
                if (!method.Parameters.ContainsKey("com.sysprogs.esp32.openocd.alg_timeout"))
                    method.Parameters["com.sysprogs.esp32.openocd.alg_timeout"] = "5000";
            }
        }

        QuickSetupDatabase _QuickSetupData = new QuickSetupDatabase();

        public ICustomBSPConfigurator CreateConfigurator(LoadedBSP.ConfiguredMCU mcu, DebugMethod method)
        {
            if (method.ID == "xt-ocd")
                return new ESP8266DebugConfigurator(method, _Interfaces);
            else if (method.ID == "openocd")
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
