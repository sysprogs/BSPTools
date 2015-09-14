using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ESP8266DebugPackage
{
    public partial class ProgramProgressForm : Form
    {
        private bool _AllowClosing;
        bool _Canceled;

        public ProgramProgressForm()
        {
            InitializeComponent();
        }

        private void ProgramProgressForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_AllowClosing)
                e.Cancel = true;
            _Canceled = true;
        }

        public new void Close()
        {
            _AllowClosing = true;
            base.Close();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            _Canceled = true;
        }

        int _Done;

        public void UpdateProgressAndThrowIfCanceled(uint addr, int doneNow, int total)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new ThreadStart(() => UpdateProgressAndThrowIfCanceled(addr, doneNow, total)));
                return;
            }

            _Done += doneNow;
            label1.Text = string.Format("Programming at 0x{0:X8}...", addr);

            if (total == 0)
                total++;
            if (_Done > total)
                _Done = total;
            try
            {
                progressBar1.Value = (int)((_Done * progressBar1.Maximum) / total);
            }
            catch { }

            if (_Canceled)
                throw new OperationCanceledException();
        }
    }
}
