using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    /*
     * The original Renesas BSPs handle the pins as follows:
     *  1. A device component defines a set of pins, e.g.:
     *      File:  .mcu/.pinmapping/PinCfgR7FA2E1AxxxBU.xml
     *      XPath: pinMappings/device/components[@id='ports']/component/pins/pin
     *      
     *  2. A board component defines one or more pin configurations, assigning specific modes to pins, e.g.:
     *      File:  .module_descriptions/Renesas##BSP##Board##ra2e1_ek####3.3.0##configuration.xml
     *      XPath: raConfiguration/raPinConfiguration/pincfg
     *      
     *  In practice, as of v3.3.0, all boards define a single pin configuration with the "g_bsp_pin_cfg" symbol.
     *  
     *  We translate it to the following artifacts on the VisualGDB level:
     *  1. Each device defines a special property group (com.renesas.ra.device.pins.mode.xxx) allowing to configure each pin
     *  2. The board can override the default values for each pin configuration. The user can still replace them via GUI.
     *  3. Regardless of the referenced board, the device generates exactly one 'g_bsp_pin_cfg' pin config, using either
     *     the values from the board, or the values explicitly set by the user.
     */
    class PinConfigurationTranslator
    {
        public struct PinID
        {
            public char Port;
            public int Pin;

            public bool IsValid => Port != 0;

            public string GetMacroName(bool useBspSyntax) => (useBspSyntax ? "BSP_IO_PORT" : "IOPORT_PORT") + $"_0{Port}_PIN_{Pin:d2}";

            public PinID(char port, int pin)
            {
                Port = port;
                Pin = pin;
            }

            public override string ToString() => $"p{Port}{Pin:d2}";

            public static PinID Parse(string str, bool throwOnFailure = true)
            {
                if (!str.StartsWith("p", StringComparison.InvariantCultureIgnoreCase) || str.Length != 4)
                {
                    if (throwOnFailure)
                        throw new Exception("Invalid pin ID:" + str);
                    else
                        return default;
                }

                return new PinID(str[1], int.Parse(str.Substring(2, 2)));
            }
        }

        public class PinMode
        {
            public readonly PinSetting Setting;
            public string Name;
            public readonly string InternalID;
            public readonly string ModeMask;

            public bool IsEmpty => ModeMask == PinSetting.EmptyPinCfgValue;

            public PinMode(PinSetting setting, string name, string internalID, string[] pincfgValues)
            {
                Setting = setting;
                Name = name;
                InternalID = internalID;

                if (pincfgValues.Length == 0)
                    ModeMask = PinSetting.EmptyPinCfgValue;
                else
                    ModeMask = string.Join(" | ", pincfgValues.Select(v => $"(uint32_t) {v}"));
            }

            public string ValueForBSP => ModeMask;
        }

        public class PinDefinition
        {
            public PinID ID;
            public Dictionary<string, PinSetting> SettingsByID = new Dictionary<string, PinSetting>();
            public List<PinSetting> Settings = new List<PinSetting>();  //All settings in original order

            public override string ToString() => $"{ID}: {Settings.Count} settings";

            public bool Completed{ get; private set; }

            public void Complete()
            {
                Completed = true;
                if (!Settings[0].AffectsPincfgRegister)
                    throw new Exception("First setting should affect PINCFG register");

                Settings[0].IsFirst = true;
            }
        }

        public class PinSetting
        {
            public const string EmptyPinCfgValue = "";

            public string ID, Name;
            public string FullID => PinConfigGroupID + ID;
            public string DefaultValueID => "_default." + FullID;

            public PinMode[] AllModes;                                  //Each mode listed exactly once
            public Dictionary<string, PinMode> ModesByOriginalID;       //May have multiple entires for the same mode

            public bool IsFirst;

            public bool AffectsPincfgRegister => AllModes != null && AllModes.Length > 1 || !AllModes[0].IsEmpty;

            public override string ToString() => $"{ID}: {AllModes?.Length} alternatives";
        }

        public class DevicePinout
        {
            public string ID;
            public string Name;
            public Dictionary<PinID, PinDefinition> Pins = new Dictionary<PinID, PinDefinition>();

            public override string ToString() => Name;
        }

        public static DevicePinout ParseDevicePinout(byte[] contents)
        {
            var lines = Encoding.UTF8.GetString(contents).Split('\n').ToList();
            if (!lines[0].StartsWith("<?xml"))
                throw new Exception("Expected an <?xml?> tag");
            lines.RemoveAt(0);
            //Some PinCfg files have the namespace, some don't. In order to simplify the lookup, we strip it before loading the file.
            const string suffix = "xmlns=\"http://www.tasking.com/schema/pinmappings/v1.1\">";
            if (lines[0].TrimEnd().EndsWith(suffix))
                lines[0] = lines[0].Substring(0, lines[0].TrimEnd().Length - suffix.Length) + ">";

            var xml = new XmlDocument();
            xml.LoadXml(string.Join("\n", lines));
            var dev = xml.DocumentElement.SelectElements("device").SingleOrDefault();
            DevicePinout result = new DevicePinout { ID = dev.GetStringAttribute("id"), Name = dev.GetStringAttribute("name") };

            foreach (var comp in dev.SelectElements("components[@id='ports']/components/component"))
            {
                /*  The pin configuration files define enum properties via 'configuration' elements on 2 levels:
                 *      1. Component-level nodes (directly under 'component').
                 *      2. Pin-level nodes (under component/pins/pin/configurations).
                 *   For GPIO components, they have the same semantics and can be processed together. */
                var id = PinID.Parse(comp.GetStringAttribute("id"), false);
                if (!id.IsValid)
                    continue;
                var pd = new PinDefinition { ID = id };

                var pinNode = comp.SelectElements("pins/pin").Single();
                if (pinNode.GetStringAttribute("id") != id.ToString())
                    throw new Exception("Mismatching 'pin' node");

                var pinCfgNode = pinNode.SelectElements("configurations/configuration").Single();
                if (pinCfgNode.GetStringAttribute("id") != id.ToString())
                    throw new Exception("Mismatching 'configuration' node");

                var pinName = pinCfgNode.GetStringAttribute("name");

                foreach (var cfgNode in comp.SelectElements("configurations/configuration").Prepend(pinCfgNode))
                {
                    var pc = new PinSetting { ID = cfgNode.GetStringAttribute("id") };

                    if (cfgNode == pinCfgNode)
                        pc.Name = $"{pinName} - Mode";
                    else
                        pc.Name = $"{pinName} - " + cfgNode.GetStringAttribute("name");

                    Dictionary<string, PinMode> modesByPincfg = new Dictionary<string, PinMode>();
                    Dictionary<string, string> idToPincfg = new Dictionary<string, string>();
                    string defaultModeID = null;

                    foreach (var altNode in cfgNode.SelectElements("alt"))
                    {
                        var altID = altNode.GetStringAttribute("id");
                        var altName = altNode.GetStringAttribute("name");
                        if (altNode.TryGetStringAttribute("default") == "true")
                            defaultModeID = altID;

                        List<string> pincfgValues = new List<string>();
                        foreach (var reg in altNode.SelectElements("registerSetting"))
                        {
                            var mask = reg.GetStringAttribute("mask");
                            if (mask != "PIN_CFG_MODE_MASK")
                                throw new Exception("Unexpected I/O pin register");

                            pincfgValues.Add(reg.GetStringAttribute("value"));
                        }

                        var mode = new PinMode(pc, altName, altID, pincfgValues.ToArray());
                        idToPincfg.Add(mode.InternalID, mode.ModeMask);    //Must be unique
                        modesByPincfg[mode.ModeMask] = mode;     //Might repeat
                    }

                    foreach (var g in modesByPincfg.Values.GroupBy(v => v.Name))
                    {
                        if (g.Count() > 1)
                            foreach (var c in g)
                            {
                                int idx = c.InternalID.LastIndexOf('.');
                                if (idx == -1)
                                    throw new Exception("Unexpected ID:" + c.InternalID);

                                c.Name += $" ({c.InternalID.Substring(idx + 1)})";
                            }
                    }

                    pc.AllModes = modesByPincfg.Values.ToArray();
                    pc.ModesByOriginalID = idToPincfg.ToDictionary(kv => kv.Key, kv => modesByPincfg[kv.Value]);

                    pd.SettingsByID.Add(pc.ID, pc);
                    pd.Settings.Add(pc);
                }

                pd.Complete();
                result.Pins[id] = pd;
            }

            if (result.Pins.Count == 0)
                throw new Exception("Could not parse any pins for " + result.Name);

            return result;
        }

        public const string PinConfigGroupID = "com.renesas.ra.device.pins.mode.";

        public static (PropertyGroup, GeneratedConfigurationFile) BuildPinPropertyGroup(DevicePinout pinout)
        {
            var pg = new PropertyGroup { Name = "Pin Configuration", UniqueID = PinConfigGroupID };
            var pinAssignmentLines = new List<GeneratedConfigurationFile.Fragment.FormattedFragment.AdvancedFormattedLine>();
            var tempVars = new List<GeneratedConfigurationFile.Fragment.FormattedFragment.IntermediateVariableAssignment>();
            foreach (var pin in pinout.Pins.Values)
            {
                string tempVar = "com.renesas.ra.device.pins.pin_cfg." + pin.ID;

                List<string> inputVars = new List<string>();

                foreach (var ps in pin.Settings)
                {
                    if (!ps.AffectsPincfgRegister)
                        continue;

                    pg.Properties.Add(new PropertyEntry.Enumerated
                    {
                        UniqueID = ps.ID,
                        Name = ps.Name,
                        DefaultEntryValue = $"$${ps.DefaultValueID}$$",
                        SuggestionList = ps.AllModes.Select(m => new PropertyEntry.Enumerated.Suggestion
                        {
                            InternalValue = m.ValueForBSP,
                            UserFriendlyName = m.Name,
                        }).ToArray()
                    });

                    inputVars.Add(ps.FullID);
                }

                tempVars.Add(new GeneratedConfigurationFile.Fragment.FormattedFragment.IntermediateVariableAssignment.FormattedVariable
                {
                    Variable = tempVar,
                    Separator = " | ",
                    InputVariables = inputVars.ToArray(),
                });

                pinAssignmentLines.Add(new GeneratedConfigurationFile.Fragment.FormattedFragment.AdvancedFormattedLine
                {
                    Format = $"\t{{ .pin = {pin.ID.GetMacroName(true)}, .pin_cfg = ($${tempVar}$$) }},",
                    Condition = new Condition.Not
                    {
                        Argument = new Condition.Equals
                        {
                            Expression = $"$${tempVar}$$",
                            ExpectedValue = "",
                        }
                    }
                });
            }

            GeneratedConfigurationFile cf = new GeneratedConfigurationFile
            {
                Name = "com.renesas.ra.device.pins.cfglines",
                Contents = new[]
                {
                    new GeneratedConfigurationFile.Fragment.FormattedFragment
                    {
                        ExtraVariables = tempVars.ToArray(),
                        Lines = pinAssignmentLines.ToArray()
                    }
                }
            };

            return (pg, cf);
        }

        public static void TranslatePinConfigurationsFromBoardComponent(XmlDocument xml,
                                                                        List<SysVarEntry> vars,
                                                                        List<GeneratedConfigurationFile> fragments,
                                                                        DevicePinout pinout,
                                                                        BSPReportWriter report)
        {
            Regex rgPin = new Regex("(p[0-9b][0-9]{2})\\.symbolic_name");
            List<string> pinMacros = new List<string>();

            //1. Collect symbolic pin names for bsp_pin_cfg.h
            foreach (var prop in xml.DocumentElement.SelectElements("raPinConfiguration/symbolicName"))
            {
                var id = prop.GetStringAttribute("propertyId");
                var value = prop.GetStringAttribute("value");
                var m = rgPin.Match(id);
                if (!m.Success)
                    throw new Exception("Unexpected pin name: " + id);

                pinMacros.Add($"#define {value} ({PinID.Parse(m.Groups[1].Value).GetMacroName(false)})");
            }

            //2. Locate the pin configuration assignments
            var pincfg = xml.DocumentElement.SelectElements("raPinConfiguration/pincfg").Single();
            var symbol = pincfg.GetStringAttribute("symbol");

            if (symbol != "g_bsp_pin_cfg")
                throw new Exception("Unexpected pin configuration symbol");

            foreach (var cs in pincfg.SelectElements("configSetting"))
            {
                var settingName = cs.GetStringAttribute("configurationId");
                var value = cs.GetStringAttribute("altId");

                int idx = settingName.IndexOf('.');
                var pinID = PinID.Parse(idx == -1 ? settingName : settingName.Substring(0, idx), false);
                if (!pinID.IsValid)
                    continue;

                var pinDef = pinout.Pins[pinID];
                if (!pinDef.SettingsByID.TryGetValue(settingName, out var setting))
                {
                    report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Unknown GPIO setting referenced by board definition", settingName, false);
                    continue;
                }

                if (!setting.AffectsPincfgRegister)
                    continue;

                PinMode mode;
                if (value.EndsWith(".asel"))
                    mode = setting.AllModes.Single(v => v.ModeMask.Contains("IOPORT_CFG_ANALOG_ENABLE"));
                else if (!setting.ModesByOriginalID.TryGetValue(value, out mode))
                {
                    report.ReportMergeableMessage(BSPReportWriter.MessageSeverity.Warning, "Unknown GPIO setting value referenced by board definition", value, false);
                    continue;
                }

                vars.Add(new SysVarEntry { Key = setting.DefaultValueID, Value = mode.ValueForBSP });
            }

            if (pinMacros.Count > 0)
                fragments.Add(new GeneratedConfigurationFile
                {
                    Name = "com.renesas.ra.device.pins.macros",
                    Contents = new[]
                    {
                        new GeneratedConfigurationFile.Fragment.BasicFragment
                        {
                            Lines = pinMacros.ToArray()
                        }
                    }
                });
        }


    }
}
