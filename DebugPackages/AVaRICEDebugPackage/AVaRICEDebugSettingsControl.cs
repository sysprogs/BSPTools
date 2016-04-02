using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Text;
using System.Windows.Forms;
using BSPEngine;
using System.Diagnostics;
using System.IO;
using UsbDriverTool;

namespace AVaRICEDebugPackage
{
    public partial class AVaRICEDebugSettingsControl : UserControl, ICustomBSPConfigurator2
    {
        class ControlObj
        {
            public Control Control;
            public PropertyEntry Property;
            public string ID;

            public override string ToString()
            {
                return ID;
            }
        }

        Dictionary<string, ControlObj> _Controls = new Dictionary<string, ControlObj>();
        private readonly DebugMethod _MyMethod;
        private readonly string _ToolchainBin;

        public AVaRICEDebugSettingsControl(DebugMethod method, string bspDir)
        {
            InitializeComponent();
            _MyMethod = method;
            _ToolchainBin = Path.Combine(Path.GetDirectoryName(bspDir), "bin");

            foreach (Control ctl in Controls)
            {
                if (ctl.Tag is string)
                {
                    _Controls[ctl.Tag.ToString()] = new ControlObj { Control = ctl, ID = ctl.Tag.ToString() };
                }
            }

            foreach (var prop in method.GDBServerArguments.Properties.PropertyGroups[0].Properties)
            {
                ControlObj obj;
                if (_Controls.TryGetValue(method.GDBServerArguments.Properties.PropertyGroups[0].UniqueID + prop.UniqueID, out obj))
                {
                    obj.Property = prop;

                    if (obj.Control is ComboBox)
                    {
                        foreach (var e in (prop as PropertyEntry.Enumerated).SuggestionList)
                            (obj.Control as ComboBox).Items.Add(e);
                        (obj.Control as ComboBox).SelectedIndex = (prop as PropertyEntry.Enumerated).DefaultEntryIndex;
                    }
                }
            }
        }


        public Dictionary<string, string> Configuration
        {
            get
            {
                Dictionary<string, string> result = new Dictionary<string, string>();
                foreach (var obj in _Controls.Values)
                {
                    if (obj.Property is PropertyEntry.Integral)
                        result[obj.ID] = (obj.Control as NumericUpDown).Value.ToString();
                    else if (obj.Property is PropertyEntry.String)
                        result[obj.ID] = (obj.Control as TextBox).Text;
                    else if (obj.Property is PropertyEntry.Enumerated)
                    {
                        string val = ((obj.Control as ComboBox)?.SelectedItem as PropertyEntry.Enumerated.Suggestion)?.InternalValue; ;
                        if (val == null)
                            val = obj.Control.Text;
                        result[obj.ID] = val;
                    }
                    else if (obj.Property is PropertyEntry.Boolean)
                    {
                        var val = (obj.Control as CheckBox).Checked ? (obj.Property as PropertyEntry.Boolean).ValueForTrue : (obj.Property as PropertyEntry.Boolean).ValueForFalse;
                        if (val != null)
                            result[obj.ID] = val;
                    }
                    else
                    {

                    }
                }

                return result;
            }

            set
            {
                foreach (var obj in _Controls.Values)
                {
                    string val = null;
                    value?.TryGetValue(obj.ID, out val);

                    if (obj.Property is PropertyEntry.Integral)
                        (obj.Control as NumericUpDown).Value = int.Parse(val);
                    else if (obj.Property is PropertyEntry.String)
                        (obj.Control as TextBox).Text = val;
                    else if (obj.Property is PropertyEntry.Enumerated)
                    {
                        SetComboBoxValue(obj.Control as ComboBox, val);
                    }
                    else if (obj.Property is PropertyEntry.Boolean)
                    {
                        if (val == (obj.Property as PropertyEntry.Boolean).ValueForTrue)
                            (obj.Control as CheckBox).Checked = true;
                        else if (val == (obj.Property as PropertyEntry.Boolean).ValueForFalse)
                            (obj.Control as CheckBox).Checked = false;
                    }
                    else
                    {

                    }
                }
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

        public Control Control
        {
            get
            {
                return this;
            }
        }

        public event EventHandler SettingsChanged;

        private void HandleSettingsChanged(object sender, EventArgs e)
        {
            if (SettingsChanged != null)
                SettingsChanged(this, EventArgs.Empty);
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            var adapterCommand = (cbAdapter.SelectedItem as PropertyEntry.Enumerated.Suggestion)?.InternalValue;
            UsbDriverHelper.UsbIdentity id = new UsbDriverHelper.UsbIdentity { VID = "03eb" };
            if (adapterCommand != null)
            {
                switch (adapterCommand)
                {
                    case "-2":
                        id.PID = "2103";
                        break;
                    case "-g":
                        id.PID = "2107";
                        break;
                }
            }

            if (id.PID != null)
                if (!UsbDriverHelper.TryCheckDeviceAndInstallDriversInteractively(new UsbDriverHelper.UsbIdentity[] { id }, "com.sysprogs.libusb.mini"))
                    return;

            Process process = new Process();
            process.StartInfo.FileName = _ToolchainBin + @"\avarice.exe";
            process.StartInfo.WorkingDirectory = _ToolchainBin;
            var cfg = Configuration;
            cfg.Remove("com.sysprogs.avr.avarice.erase");
            cfg.Remove("com.sysprogs.avr.avarice.program");
            cfg.Remove("com.sysprogs.avr.avarice.verify");
            cfg.Remove("com.sysprogs.avr.avarice.port");

            process.StartInfo.Arguments = CommandLineTools.BuildCommandLine(_MyMethod.GDBServerArguments, new Dictionary<string, string>(), cfg).Replace(_MyMethod.GDBServerArguments.GNUArgumentPrefix, "") + " -l";

            using (var frm = new CommandTestForm(process))
            {
                frm.ShowDialog();
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Process.Start(AVaRICEDebugExtension.UsbDriverToolExe);
        }

        private void btnDetect_Click(object sender, EventArgs e)
        {
            try
            {
                string devSpec = null;
                var rawDevices = DeviceRecord.FindAllUsbDevices();
                if (rawDevices != null)
                    foreach (var dev in rawDevices)
                    {
                        if (dev.VendorID?.ToLower() == "03eb")
                        {
                            switch(dev.ProductID??"")
                            {
                                case "2103":
                                    devSpec = "-2";
                                    break;
                                case "2107":
                                    devSpec = "-g";
                                    break;
                            }
                        }
                    }

                if (devSpec == null)
                    throw new Exception("Cannot find any known USB JTAG/SWD programmers. Please ensure your programmer is connected.");
                else
                {
                    SetComboBoxValue(cbAdapter, devSpec);
                    comboBox3.Text = "usb";
                    MessageBox.Show("Detected " + cbAdapter.Text + ".", "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }
    }
}
