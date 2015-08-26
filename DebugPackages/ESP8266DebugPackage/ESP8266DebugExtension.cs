using BSPEngine;
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

        public ESP8266DebugExtension()
        {
            try
            {
                _Interfaces = XmlTools.LoadObject<ESP8266DebugConfigurator.DebugInterfaceList>(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "interfaces.xml"));
            }
            catch { }
        }

        public void AdjustDebugMethod(LoadedBSP.ConfiguredMCU mcu, ConfiguredDebugMethod method)
        {
            string iface;
            if (method.Parameters.TryGetValue("com.sysprogs.esp8266.xt-ocd.debug_iface", out iface))
            {
                ESP8266DebugConfigurator.DebugInterface ifaceObj = null;
                foreach(var obj in _Interfaces.Interfaces)
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
                    foreach(var p in ifaceObj.Parameters)
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

        public ICustomBSPConfigurator CreateConfigurator(LoadedBSP.ConfiguredMCU mcu, DebugMethod method)
        {
            return new ESP8266DebugConfigurator(method, _Interfaces);
        }

        public IEnumerable<ICustomStartupSequenceBuilder> StartupSequences
        {
            get { return new ICustomStartupSequenceBuilder[] { new ESP8266StartupSequence() }; }
        }
    }
}
