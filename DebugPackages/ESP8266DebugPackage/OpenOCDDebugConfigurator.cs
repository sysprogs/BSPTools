using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Text;
using System.Windows.Forms;
using BSPEngine;
using OpenOCDPackage;
using System.IO;
using System.Reflection;
using System.Diagnostics;

namespace ESP8266DebugPackage
{
    public partial class OpenOCDDebugConfigurator : UserControl, ICustomBSPConfigurator2
    {
        Dictionary<string, ComboBox> _ComboBoxes = new Dictionary<string, ComboBox>();
        readonly QuickSetupDatabase _QuickSetupData;
        private Dictionary<string, string> _Configuration;
        private DebugMethod _Method;
        private readonly string _OpenOCDDirectory;

        public OpenOCDDebugConfigurator(DebugMethod method, QuickSetupDatabase quickSetup)
        {
            InitializeComponent();
            _QuickSetupData = quickSetup;
            _Method = method;
            _OpenOCDDirectory = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + @"\..\..\..\OpenOCD");
            openOCDScriptSelector1.SubdirectoryName = "interface";
            openOCDScriptSelector1.OverrideOpenOCDDirectory(_OpenOCDDirectory);
            foreach (var prop in method.AdditionalProperties.PropertyGroups[0].Properties)
            {
                if (prop is PropertyEntry.Enumerated)
                {
                    for (int pass = 0; pass < 2; pass++)
                        foreach (var ctl in pass == 0 ? pnlFLASH.Controls : panel2.Controls)
                        {
                            if (ctl is ComboBox && (ctl as ComboBox).Tag is string && (ctl as ComboBox).Tag.ToString() == prop.UniqueID)
                            {
                                foreach (var obj in (prop as PropertyEntry.Enumerated).SuggestionList)
                                    (ctl as ComboBox).Items.Add(obj);
                                (ctl as ComboBox).SelectedIndex = (prop as PropertyEntry.Enumerated).DefaultEntryIndex;
                                _ComboBoxes[prop.UniqueID] = ctl as ComboBox;
                            }
                        }
                }
            }

            var ifaces = _QuickSetupData.AllInterfaces;
            if (ifaces != null)
                foreach (var iface in ifaces)
                    cbQuickInterface.Items.Add(iface);

            cbQuickInterface.Items.Add(new ManualIfacePseudoitem());
        }

        class ManualIfacePseudoitem
        {
            public override string ToString()
            {
                return "Specify interface script manually below";
            }
        }

        public static void SetComboBoxValue(ComboBox comboBox, string val)
        {
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is PropertyEntry.Enumerated.Suggestion && (comboBox.Items[i] as PropertyEntry.Enumerated.Suggestion).InternalValue == val)
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }

            var item = new PropertyEntry.Enumerated.Suggestion { InternalValue = val };
            comboBox.Items.Add(item);
            comboBox.SelectedItem = item;
        }

        public Dictionary<string, string> Configuration
        {
            get
            {
                var result = _Configuration == null ? new Dictionary<string, string>() : new Dictionary<string, string>(_Configuration);

                foreach (var kv in _ComboBoxes)
                {
                    if (kv.Value.SelectedItem is PropertyEntry.Enumerated.Suggestion)
                        result[kv.Key] = (kv.Value.SelectedItem as PropertyEntry.Enumerated.Suggestion).InternalValue;
                    else
                        result[kv.Key] = kv.Value.Text;
                }

                if (!cbHaveInitDataFile.Checked)
                    result["com.sysprogs.esp8266.init_data_file"] = "";

                string iface = (cbQuickInterface.SelectedItem as QuickSetupDatabase.ProgrammingInterface)?.ScriptFile;
                if (iface == null)
                    iface = openOCDScriptSelector1.Text;

                result["com.sysprogs.esp8266.openocd.iface_script"] = iface;
                if (cbQuickSpeed.Checked)
                    result["com.sysprogs.esp8266.openocd.speed_cmd"] = string.Format("-c \"adapter_khz {0}\"", numSpeed2.Value);
                else
                    result["com.sysprogs.esp8266.openocd.speed_cmd"] = "";

                result["com.sysprogs.esp8266.disable_interrupts_during_steps"] = cbSuppressInterrupts.Checked ? "on" : "off";
                result["com.sysprogs.esp8266.autofeed_watchdog"] = cbFeedWatchdog.Checked ? "on" : "off";
                result["com.sysprogs.esp8266.openocd.extra_cmdline"] = cbCmdline.Checked ? txtExtraArgs.Text : "";
                return result;
            }

            set
            {
                _Configuration = value;
                if (value == null)
                    return;
                string val;
                foreach (var kv in _ComboBoxes)
                {
                    if (value.TryGetValue(kv.Key, out val))
                        SetComboBoxValue(kv.Value, val);
                }

                cbSuppressInterrupts.Checked = value.TryGetValue("com.sysprogs.esp8266.disable_interrupts_during_steps", out val) && val == "on";
                cbFeedWatchdog.Checked = value.TryGetValue("com.sysprogs.esp8266.autofeed_watchdog", out val) && val == "on";
                cbHaveInitDataFile.Checked = value.TryGetValue("com.sysprogs.esp8266.init_data_file", out val) && val != "";
                if (cbInitDataFile.Text == "" && !cbHaveInitDataFile.Checked && cbInitDataFile.Items.Count > 0)
                    cbInitDataFile.SelectedIndex = 0;

                if (!value.TryGetValue("com.sysprogs.esp8266.openocd.extra_cmdline", out val) || string.IsNullOrEmpty(val))
                {
                    txtExtraArgs.Text = "";
                    cbCmdline.Checked = false;
                }
                else
                {
                    txtExtraArgs.Text = val;
                    cbCmdline.Checked = true;
                }

                value.TryGetValue("com.sysprogs.esp8266.openocd.iface_script", out val);
                object matchingItem = null;
                foreach(var obj in cbQuickInterface.Items)
                {
                    if (val != null && val == (obj as QuickSetupDatabase.ProgrammingInterface)?.ScriptFile)
                    {
                        matchingItem = obj;
                        break;
                    }
                }

                if (matchingItem != null)
                    cbQuickInterface.SelectedItem = matchingItem;
                else
                {
                    cbQuickInterface.SelectedIndex = cbQuickInterface.Items.Count - 1;
                    openOCDScriptSelector1.Text = val;
                }

                //This needs to be set after the interface combobox, as the interface combobox change handler presets the default value for speed checkbox
                if (value.TryGetValue("com.sysprogs.esp8266.openocd.speed_cmd", out val) && val != null && val.Contains("adapter_khz"))
                {
                    cbQuickSpeed.Checked = true;
                    val = val.TrimEnd('\"', ' ');
                    int idx = val.LastIndexOf(' ');
                    int speed = 0;
                    if (idx != -1)
                        int.TryParse(val.Substring(idx + 1), out speed);

                    numSpeed2.Value = speed;
                }
                else
                    cbQuickSpeed.Checked = false;
            }
        }

        public Control Control
        {
            get
            {
                return this;
            }
        }

        public event EventHandler SettingsChanged;

        private void SettingsChangedHandler(object sender, EventArgs e)
        {
            if (SettingsChanged != null)
                SettingsChanged(this, e);
        }

        private void cbQuickInterface_SelectedIndexChanged(object sender, EventArgs e)
        {
            openOCDScriptSelector1.Visible = (cbQuickInterface.SelectedItem is ManualIfacePseudoitem);

            var iface = cbQuickInterface.SelectedItem as QuickSetupDatabase.ProgrammingInterface;
            if (iface == null || iface.SpeedCapability == QuickSetupDatabase.AdapterSpeedCapability.Optional || (iface.SpeedCapability == QuickSetupDatabase.AdapterSpeedCapability.Required))
                cbQuickSpeed.Enabled = true;
            else
            {
                cbQuickSpeed.Enabled = false;
                if (iface.SpeedCapability == QuickSetupDatabase.AdapterSpeedCapability.NotSupported)
                    cbQuickSpeed.Checked = false;
                else
                {
                    if (!cbQuickSpeed.Checked)
                        numSpeed2.Value = 3000;
                    cbQuickSpeed.Checked = true;
                }
            }
        }

        private void cbQuickSpeed_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChangedHandler(sender, e);
            numSpeed2.Enabled = label2.Enabled = cbQuickSpeed.Checked;
        }

        private void btnDetectIface_Click(object sender, EventArgs e)
        {
            try
            {
                QuickSetupDatabase.ProgrammingInterface iface;
                var usbDevices = _QuickSetupData.FindKnownUsbDevices();
                if (usbDevices.Count == 0)
                    throw new Exception("Cannot find any known USB JTAG/SWD programmers. Please ensure your programmer is connected.");
                if (usbDevices.Count == 1)
                {
                    iface = usbDevices[0].Interface;
                    MessageBox.Show("Detected " + iface.Name + ".", "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                    iface = UsbProgrammerSelectionForm.SelectDevice(usbDevices);

                SetSelectedDevice(iface);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetSelectedDevice(QuickSetupDatabase.ProgrammingInterface iface)
        {
            if (iface != null)
                for (int i = 0; i < cbQuickInterface.Items.Count; i++)
                {
                    var thisIface = cbQuickInterface.Items[i] as QuickSetupDatabase.ProgrammingInterface;
                    if (thisIface != null && thisIface.ID == iface.ID)
                    {
                        cbQuickInterface.SelectedIndex = i;
                        break;
                    }
                }
        }

        private void cbCmdline_CheckedChanged(object sender, EventArgs e)
        {
            txtExtraArgs.Enabled = cbCmdline.Checked;
            SettingsChangedHandler(sender, e);
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            var iface = cbQuickInterface.SelectedItem as QuickSetupDatabase.ProgrammingInterface;
            if (iface != null && iface.UsbIdentities != null && iface.UsbIdentities.Length > 0)
                if (!UsbDriverHelper.TryCheckDeviceAndInstallDriversInteractively(iface.UsbIdentities, iface.UniversalDriverId))
                    return;

            Process process = new Process();
            process.StartInfo.FileName = _OpenOCDDirectory + @"\bin\openocd.exe";
            process.StartInfo.WorkingDirectory = _OpenOCDDirectory + @"\share\openocd\scripts";
            process.StartInfo.Arguments = CommandLineTools.BuildCommandLine(_Method.GDBServerArguments, new Dictionary<string, string>(), Configuration);

            using (var frm = new CommandTestForm(process))
            {
                frm.ShowDialog();
                string output = frm.AllOutput;
                if (output.Contains("An adapter speed is not selected in the init script") && !cbQuickSpeed.Checked)
                    if (MessageBox.Show("OpenOCD could not determine JTAG speed. Do you want to specify it explicitly?", "VisualGDB", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        cbQuickSpeed.Checked = true;
                        numSpeed2.Focus();
                    }
            }
        }

        private void lblStartDriverTool_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + @"\drivers\UsbDriverTool.exe";
                Process.Start(path);
            }
            catch { }

        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            cbInitDataFile.Enabled = cbHaveInitDataFile.Checked;
            SettingsChangedHandler(sender, e);
        }
    }
}
