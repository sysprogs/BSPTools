using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Text;
using System.Windows.Forms;
using BSPEngine;
using Microsoft.Win32;
using System.IO;
using System.Diagnostics;

namespace ESP8266DebugPackage
{
    public partial class ESP8266DebugConfigurator : UserControl, ICustomBSPConfigurator2
    {
        Dictionary<string, ComboBox> _ComboBoxes = new Dictionary<string, ComboBox>();
        RegistryKey _SettingsKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\ESP8266EDP");

        public class DebugInterface
        {
            public string Name;
            public string ID;
            public string Module;
            public PropertyEntry[] Parameters;

            public override string ToString()
            {
                return Name;
            }
        }

        public class DebugInterfaceList
        {
            public DebugInterface[] Interfaces;
        }

        class CustomModePseudoInterface
        {
            public override string ToString()
            {
                return "Specify topology file manually";
            }
        }

        public ESP8266DebugConfigurator(DebugMethod method, DebugInterfaceList ifaces)
        {
            InitializeComponent();
            foreach(var prop in method.AdditionalProperties.PropertyGroups[0].Properties)
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

            foreach (var iface in ifaces.Interfaces)
                cbDebugInterface.Items.Add(iface);

            cbDebugInterface.Items.Add(new CustomModePseudoInterface());

            if (_SettingsKey != null)
            {
                var val = _SettingsKey.GetValue("XtOcdPath") as string;
                if (val != null)
                    txtXtOcd.Text = val;
                val = _SettingsKey.GetValue("DebugInterface") as string;
                if (val != null)
                    for (int i = 0;i < cbDebugInterface.Items.Count; i++)
                    {
                        if (cbDebugInterface.Items[i] is DebugInterface && (cbDebugInterface.Items[i] as DebugInterface).ID == val)
                        {
                            cbDebugInterface.SelectedIndex = i;

                            var subkey = _SettingsKey.OpenSubKey("DebugInterfaceSettings");
                            if (subkey != null)
                            {
                                foreach(var kv in _DebuggerComboBoxes)
                                {
                                    val = subkey.GetValue(kv.Key) as string;
                                    if (val != null)
                                        SetComboBoxValue(kv.Value, val);
                                }
                            }

                            break;
                        }
                    }
            }
        }

        public event EventHandler SettingsChanged;

        Dictionary<string, string> _Configuration;

        public Dictionary<string, string> Configuration
        {
            get
            {
                var result = _Configuration == null ? new Dictionary<string, string>() : new Dictionary<string, string>(_Configuration);
                if (_SettingsKey != null && File.Exists(txtXtOcd.Text))
                    _SettingsKey.SetValue("XtOcdPath", txtXtOcd.Text);

                var iface = cbDebugInterface.SelectedItem;
                if (iface is DebugInterface)
                {
                    if (_SettingsKey != null)
                    {
                        _SettingsKey.SetValue("DebugInterface", (iface as DebugInterface).ID);
                        var subkey = _SettingsKey.CreateSubKey("DebugInterfaceSettings");
                        if (subkey != null)
                            foreach (var kv in _DebuggerComboBoxes)
                                subkey.SetValue(kv.Key, kv.Value.Text);
                    }
                    result["com.sysprogs.esp8266.xt-ocd.debug_iface"] = (iface as DebugInterface).ID;
                }
                else
                    result["com.sysprogs.esp8266.xt-ocd.debug_iface"] = "";

                foreach(var kv in _ComboBoxes)
                {
                    if (kv.Value.SelectedItem is PropertyEntry.Enumerated.Suggestion)
                        result[kv.Key] = (kv.Value.SelectedItem as PropertyEntry.Enumerated.Suggestion).InternalValue;
                    else
                        result[kv.Key] = kv.Value.Text;
                }
                
                result["com.sysprogs.esp8266.xt-ocd.daemonpath"] = txtXtOcd.Text;

                foreach (var kv in _DebuggerComboBoxes)
                    if (kv.Value.SelectedItem is PropertyEntry.Enumerated.Suggestion)
                        result[InterfaceSettingPrefix +  kv.Key] = (kv.Value.SelectedItem as PropertyEntry.Enumerated.Suggestion).InternalValue;
                    else
                        result[InterfaceSettingPrefix + kv.Key] = kv.Value.Text;

                result["com.sysprogs.esp8266.xt-ocd.configfile"] = txtTopologyFile.Text;

                return result;
            }
            set
            {
                _Configuration = value;
                if (value == null)
                    return;
                string val;
                if (value.TryGetValue("com.sysprogs.esp8266.xt-ocd.daemonpath", out val))
                    txtXtOcd.Text = val;

                foreach(var kv in _ComboBoxes)
                {
                    if (value.TryGetValue(kv.Key, out val))
                        SetComboBoxValue(kv.Value, val);
                }

                bool ifaceFound = false;
                if(value.TryGetValue("com.sysprogs.esp8266.xt-ocd.debug_iface", out val))
                {
                    for (int i = 0; i < cbDebugInterface.Items.Count; i++)
                    {
                        if (cbDebugInterface.Items[i] is DebugInterface && (cbDebugInterface.Items[i] as DebugInterface).ID == val)
                        {
                            cbDebugInterface.SelectedIndex = i;
                            ifaceFound = true;
                            break;
                        }
                    }
                }

                if (value.TryGetValue("com.sysprogs.esp8266.xt-ocd.configfile", out val))
                    txtTopologyFile.Text = val;

                if (ifaceFound)
                    LoadInterfaceSettings();
                else
                    cbDebugInterface.SelectedIndex = cbDebugInterface.Items.Count - 1;
            }
        }

        public Control Control
        {
            get { return this; }
        }



        private void SettingsChangedHandler(object sender, EventArgs e)
        {
            if (SettingsChanged != null)
                SettingsChanged(this, e);
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("http://www.esp8266.com/viewtopic.php?t=1960");
        }

        private void btnOpenScript_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
                txtXtOcd.Text = openFileDialog1.FileName;
        }


        Dictionary<string, ComboBox> _DebuggerComboBoxes = new Dictionary<string, ComboBox>();

        private void cbDebugInterface_SelectedIndexChanged(object sender, EventArgs e)
        {
            SettingsChangedHandler(sender, e);
            pnlDebuggerSettings.Controls.Clear();
            _DebuggerComboBoxes.Clear();
            var iface = cbDebugInterface.SelectedItem as DebugInterface;
            if (iface != null)
            {
                int Y = 0;
                pnlDebuggerSettings.Visible = true;
                pnlCustom.Visible = false;
                if (iface.Parameters != null)
                    foreach(var pe in iface.Parameters)
                    {
                        var p = pe as PropertyEntry.Enumerated;
                        if (p == null)
                            continue;

                        var lbl = new Label { Text = p.Name + ":", Left = label1.Left, Top = label1.Top + Y, Parent = pnlDebuggerSettings };
                        var cb = new ComboBox
                        {
                            Left = cbDebugInterface.Left,
                            Top = Y + txtXtOcd.Top,
                            Parent = pnlDebuggerSettings,
                            Width = cbDebugInterface.Width,
                            Anchor = txtXtOcd.Anchor,
                            DropDownStyle = p.AllowFreeEntry ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList
                        };

                        cb.TextUpdate += SettingsChangedHandler;
                        cb.SelectedIndexChanged += SettingsChangedHandler;

                        if (p.SuggestionList != null)
                            foreach (var se in p.SuggestionList)
                                cb.Items.Add(se);
                        if (p.DefaultEntryIndex != -1 && p.DefaultEntryIndex < cb.Items.Count)
                            cb.SelectedIndex = p.DefaultEntryIndex;

                        _DebuggerComboBoxes[p.UniqueID] = cb;
                        Y += (label2.Top - label1.Top);
                    }

                pnlDebuggerSettings.Height = Y + txtXtOcd.Top;
                LoadInterfaceSettings();
            }
            else
            {
                pnlDebuggerSettings.Visible = false;
                pnlCustom.Visible = true;

            }
        }

        public const string InterfaceSettingPrefix = "com.sysprogs.esp8266.xt-ocd.iface.";

        private void LoadInterfaceSettings()
        {
            if (_Configuration == null)
                return;
            string val;
            foreach (var kv in _DebuggerComboBoxes)
                if (_Configuration.TryGetValue(InterfaceSettingPrefix + kv.Key, out val))
                    SetComboBoxValue(kv.Value, val);
        }

        private void SetComboBoxValue(ComboBox comboBox, string val)
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

        private void button1_Click(object sender, EventArgs e)
        {
            if (openFileDialog2.ShowDialog() == DialogResult.OK)
                txtTopologyFile.Text = openFileDialog2.FileName;
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            SettingsChangedHandler(this, e);
            pnlFLASH.Enabled = cbProgramMode.SelectedIndex != 1;
        }
    }
}
