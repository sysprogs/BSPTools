using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Net.Sockets;

namespace AVaRICEDebugPackage
{
    public partial class CommandTestForm : Form
    {
        Process _Process;

        const int CommandPort = 4444;

        public CommandTestForm(Process nonStartedProcess)
        {
            InitializeComponent();
            _Process = nonStartedProcess;

            _Process.OutputDataReceived += new DataReceivedEventHandler(_Process_OutputDataReceived);
            _Process.ErrorDataReceived += new DataReceivedEventHandler(_Process_OutputDataReceived);
            _Process.Exited += new EventHandler(_Process_Exited);
            _Process.StartInfo.RedirectStandardError = true;
            _Process.StartInfo.RedirectStandardOutput = true;
            _Process.StartInfo.CreateNoWindow = true;
            _Process.StartInfo.UseShellExecute = false;
            _Process.EnableRaisingEvents = true;

            txtOutput.Text = nonStartedProcess.StartInfo.FileName + " " + nonStartedProcess.StartInfo.Arguments + "\r\n";
        }

        void _Process_Exited(object sender, EventArgs e)
        {
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(new ThreadStart(HandleProcessExitedFromGUIThread));
            }
            catch { }
        }

        void _Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            try
            {
                if (IsHandleCreated && e.Data != null)
                    BeginInvoke(new ThreadStart(() => AddLine(e.Data)));
            }
            catch { }
        }

        void HandleProcessExitedFromGUIThread()
        {
            try
            {
                btnAbort.Enabled = false;
                btnClose.Enabled = true;
                int code = _Process.ExitCode;
                if (code == 0)
                {
                    MessageBox.Show("Your settings appear to be valid.", "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(string.Format("AVaRICE exited with code {0}. Please check your settings.", code), "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                label2.Text = string.Format("AVaRICE exited with code {0}.", code);
            }
            catch { }
        }

        void AddLine(string line)
        {
            try
            {
                txtOutput.Text += line + "\r\n";
                txtOutput.SelectionLength = 0;
                txtOutput.SelectionStart = 0;
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _Process.Start();
            _Process.BeginOutputReadLine();
            _Process.BeginErrorReadLine();
        }

        private void btnAbort_Click(object sender, EventArgs e)
        {
            _Process.Kill();
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            DialogResult = System.Windows.Forms.DialogResult.OK;
        }


        public string AllOutput
        {
            get
            {
                return txtOutput.Text;
            }
        }
    }
}
