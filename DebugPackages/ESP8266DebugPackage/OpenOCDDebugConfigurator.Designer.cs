namespace ESP8266DebugPackage
{
    partial class OpenOCDDebugConfigurator
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.panel2 = new System.Windows.Forms.Panel();
            this.label10 = new System.Windows.Forms.Label();
            this.label8 = new System.Windows.Forms.Label();
            this.cbProgramMode = new System.Windows.Forms.ComboBox();
            this.cbResetMode = new System.Windows.Forms.ComboBox();
            this.pnlFLASH = new System.Windows.Forms.TableLayoutPanel();
            this.label7 = new System.Windows.Forms.Label();
            this.comboBox6 = new System.Windows.Forms.ComboBox();
            this.label6 = new System.Windows.Forms.Label();
            this.comboBox5 = new System.Windows.Forms.ComboBox();
            this.label5 = new System.Windows.Forms.Label();
            this.comboBox4 = new System.Windows.Forms.ComboBox();
            this.comboBox3 = new System.Windows.Forms.ComboBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.comboBox2 = new System.Windows.Forms.ComboBox();
            this.panel1 = new System.Windows.Forms.Panel();
            this.btnDetectIface = new System.Windows.Forms.Button();
            this.cbQuickInterface = new System.Windows.Forms.ComboBox();
            this.label1 = new System.Windows.Forms.Label();
            this.panel3 = new System.Windows.Forms.Panel();
            this.txtExtraArgs = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.cbFeedWatchdog = new System.Windows.Forms.CheckBox();
            this.cbSuppressInterrupts = new System.Windows.Forms.CheckBox();
            this.cbCmdline = new System.Windows.Forms.CheckBox();
            this.cbQuickSpeed = new System.Windows.Forms.CheckBox();
            this.numSpeed2 = new System.Windows.Forms.NumericUpDown();
            this.openOCDScriptSelector1 = new OpenOCDPackage.OpenOCDScriptSelector();
            this.panel2.SuspendLayout();
            this.pnlFLASH.SuspendLayout();
            this.panel1.SuspendLayout();
            this.panel3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numSpeed2)).BeginInit();
            this.SuspendLayout();
            // 
            // panel2
            // 
            this.panel2.Controls.Add(this.label10);
            this.panel2.Controls.Add(this.label8);
            this.panel2.Controls.Add(this.cbProgramMode);
            this.panel2.Controls.Add(this.cbResetMode);
            this.panel2.Controls.Add(this.pnlFLASH);
            this.panel2.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel2.Location = new System.Drawing.Point(0, 147);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(359, 147);
            this.panel2.TabIndex = 42;
            // 
            // label10
            // 
            this.label10.AutoSize = true;
            this.label10.Location = new System.Drawing.Point(3, 31);
            this.label10.Name = "label10";
            this.label10.Size = new System.Drawing.Size(86, 13);
            this.label10.TabIndex = 5;
            this.label10.Text = "Program FLASH:";
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(3, 6);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(67, 13);
            this.label8.TabIndex = 5;
            this.label8.Text = "Reset mode:";
            // 
            // cbProgramMode
            // 
            this.cbProgramMode.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbProgramMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbProgramMode.FormattingEnabled = true;
            this.cbProgramMode.Location = new System.Drawing.Point(120, 28);
            this.cbProgramMode.Name = "cbProgramMode";
            this.cbProgramMode.Size = new System.Drawing.Size(239, 21);
            this.cbProgramMode.TabIndex = 16;
            this.cbProgramMode.Tag = "com.sysprogs.esp8266.xt-ocd.program_flash";
            this.cbProgramMode.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.cbProgramMode.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // cbResetMode
            // 
            this.cbResetMode.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbResetMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbResetMode.FormattingEnabled = true;
            this.cbResetMode.Location = new System.Drawing.Point(120, 3);
            this.cbResetMode.Name = "cbResetMode";
            this.cbResetMode.Size = new System.Drawing.Size(239, 21);
            this.cbResetMode.TabIndex = 15;
            this.cbResetMode.Tag = "com.sysprogs.esp8266.xt-ocd.flash_start_mode";
            this.cbResetMode.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.cbResetMode.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // pnlFLASH
            // 
            this.pnlFLASH.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlFLASH.ColumnCount = 4;
            this.pnlFLASH.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.pnlFLASH.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.pnlFLASH.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.pnlFLASH.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.pnlFLASH.Controls.Add(this.label7, 0, 2);
            this.pnlFLASH.Controls.Add(this.comboBox6, 0, 2);
            this.pnlFLASH.Controls.Add(this.label6, 2, 1);
            this.pnlFLASH.Controls.Add(this.comboBox5, 3, 1);
            this.pnlFLASH.Controls.Add(this.label5, 0, 1);
            this.pnlFLASH.Controls.Add(this.comboBox4, 1, 1);
            this.pnlFLASH.Controls.Add(this.comboBox3, 3, 0);
            this.pnlFLASH.Controls.Add(this.label3, 0, 0);
            this.pnlFLASH.Controls.Add(this.label4, 2, 0);
            this.pnlFLASH.Controls.Add(this.comboBox2, 1, 0);
            this.pnlFLASH.Location = new System.Drawing.Point(46, 55);
            this.pnlFLASH.Name = "pnlFLASH";
            this.pnlFLASH.RowCount = 3;
            this.pnlFLASH.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 33.33333F));
            this.pnlFLASH.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 33.33333F));
            this.pnlFLASH.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 33.33333F));
            this.pnlFLASH.Size = new System.Drawing.Size(313, 83);
            this.pnlFLASH.TabIndex = 10;
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(3, 54);
            this.label7.Name = "label7";
            this.label7.Padding = new System.Windows.Forms.Padding(0, 3, 0, 0);
            this.label7.Size = new System.Drawing.Size(37, 16);
            this.label7.TabIndex = 8;
            this.label7.Text = "Mode:";
            // 
            // comboBox6
            // 
            this.comboBox6.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox6.FormattingEnabled = true;
            this.comboBox6.Location = new System.Drawing.Point(71, 57);
            this.comboBox6.Name = "comboBox6";
            this.comboBox6.Size = new System.Drawing.Size(70, 21);
            this.comboBox6.TabIndex = 5;
            this.comboBox6.Tag = "com.sysprogs.esp8266.xt-ocd.flash_mode";
            this.comboBox6.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox6.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(147, 27);
            this.label6.Name = "label6";
            this.label6.Padding = new System.Windows.Forms.Padding(0, 3, 0, 0);
            this.label6.Size = new System.Drawing.Size(30, 16);
            this.label6.TabIndex = 6;
            this.label6.Text = "Size:";
            // 
            // comboBox5
            // 
            this.comboBox5.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox5.FormattingEnabled = true;
            this.comboBox5.Location = new System.Drawing.Point(240, 30);
            this.comboBox5.Name = "comboBox5";
            this.comboBox5.Size = new System.Drawing.Size(70, 21);
            this.comboBox5.TabIndex = 4;
            this.comboBox5.Tag = "com.sysprogs.esp8266.xt-ocd.flash_size";
            this.comboBox5.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox5.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(3, 27);
            this.label5.Name = "label5";
            this.label5.Padding = new System.Windows.Forms.Padding(0, 3, 0, 0);
            this.label5.Size = new System.Drawing.Size(60, 16);
            this.label5.TabIndex = 4;
            this.label5.Text = "Frequency:";
            // 
            // comboBox4
            // 
            this.comboBox4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox4.FormattingEnabled = true;
            this.comboBox4.Location = new System.Drawing.Point(71, 30);
            this.comboBox4.Name = "comboBox4";
            this.comboBox4.Size = new System.Drawing.Size(70, 21);
            this.comboBox4.TabIndex = 3;
            this.comboBox4.Tag = "com.sysprogs.esp8266.xt-ocd.flash_freq";
            this.comboBox4.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox4.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // comboBox3
            // 
            this.comboBox3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox3.FormattingEnabled = true;
            this.comboBox3.Location = new System.Drawing.Point(240, 3);
            this.comboBox3.Name = "comboBox3";
            this.comboBox3.Size = new System.Drawing.Size(70, 21);
            this.comboBox3.TabIndex = 2;
            this.comboBox3.Tag = "com.sysprogs.esp8266.xt-ocd.erase_sector_size";
            this.comboBox3.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox3.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(3, 0);
            this.label3.Name = "label3";
            this.label3.Padding = new System.Windows.Forms.Padding(0, 3, 0, 0);
            this.label3.Size = new System.Drawing.Size(62, 16);
            this.label3.TabIndex = 0;
            this.label3.Text = "Sector size:";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(147, 0);
            this.label4.Name = "label4";
            this.label4.Padding = new System.Windows.Forms.Padding(0, 3, 0, 0);
            this.label4.Size = new System.Drawing.Size(87, 16);
            this.label4.TabIndex = 1;
            this.label4.Text = "Erase block size:";
            // 
            // comboBox2
            // 
            this.comboBox2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox2.FormattingEnabled = true;
            this.comboBox2.Location = new System.Drawing.Point(71, 3);
            this.comboBox2.Name = "comboBox2";
            this.comboBox2.Size = new System.Drawing.Size(70, 21);
            this.comboBox2.TabIndex = 1;
            this.comboBox2.Tag = "com.sysprogs.esp8266.xt-ocd.prog_sector_size";
            this.comboBox2.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox2.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.btnDetectIface);
            this.panel1.Controls.Add(this.cbQuickInterface);
            this.panel1.Controls.Add(this.label1);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel1.Location = new System.Drawing.Point(0, 0);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(359, 28);
            this.panel1.TabIndex = 43;
            // 
            // btnDetectIface
            // 
            this.btnDetectIface.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnDetectIface.Location = new System.Drawing.Point(276, 2);
            this.btnDetectIface.Name = "btnDetectIface";
            this.btnDetectIface.Size = new System.Drawing.Size(80, 23);
            this.btnDetectIface.TabIndex = 2;
            this.btnDetectIface.Text = "Detect";
            this.btnDetectIface.UseVisualStyleBackColor = true;
            this.btnDetectIface.Click += new System.EventHandler(this.btnDetectIface_Click);
            // 
            // cbQuickInterface
            // 
            this.cbQuickInterface.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbQuickInterface.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbQuickInterface.FormattingEnabled = true;
            this.cbQuickInterface.Location = new System.Drawing.Point(124, 3);
            this.cbQuickInterface.Name = "cbQuickInterface";
            this.cbQuickInterface.Size = new System.Drawing.Size(146, 21);
            this.cbQuickInterface.TabIndex = 1;
            this.cbQuickInterface.SelectedIndexChanged += new System.EventHandler(this.cbQuickInterface_SelectedIndexChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(3, 6);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(115, 13);
            this.label1.TabIndex = 4;
            this.label1.Text = "Programming interface:";
            // 
            // panel3
            // 
            this.panel3.Controls.Add(this.txtExtraArgs);
            this.panel3.Controls.Add(this.label2);
            this.panel3.Controls.Add(this.cbFeedWatchdog);
            this.panel3.Controls.Add(this.cbSuppressInterrupts);
            this.panel3.Controls.Add(this.cbCmdline);
            this.panel3.Controls.Add(this.cbQuickSpeed);
            this.panel3.Controls.Add(this.numSpeed2);
            this.panel3.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel3.Location = new System.Drawing.Point(0, 50);
            this.panel3.Name = "panel3";
            this.panel3.Size = new System.Drawing.Size(359, 97);
            this.panel3.TabIndex = 45;
            // 
            // txtExtraArgs
            // 
            this.txtExtraArgs.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtExtraArgs.Enabled = false;
            this.txtExtraArgs.Location = new System.Drawing.Point(204, 2);
            this.txtExtraArgs.Name = "txtExtraArgs";
            this.txtExtraArgs.Size = new System.Drawing.Size(152, 20);
            this.txtExtraArgs.TabIndex = 12;
            this.txtExtraArgs.TextChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Enabled = false;
            this.label2.Location = new System.Drawing.Point(201, 28);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(27, 13);
            this.label2.TabIndex = 10;
            this.label2.Text = "KHz";
            // 
            // cbFeedWatchdog
            // 
            this.cbFeedWatchdog.AutoSize = true;
            this.cbFeedWatchdog.Checked = true;
            this.cbFeedWatchdog.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbFeedWatchdog.Location = new System.Drawing.Point(6, 73);
            this.cbFeedWatchdog.Name = "cbFeedWatchdog";
            this.cbFeedWatchdog.Size = new System.Drawing.Size(241, 17);
            this.cbFeedWatchdog.TabIndex = 16;
            this.cbFeedWatchdog.Text = "Feed ESP8266 watchdog timer while stopped";
            this.cbFeedWatchdog.UseVisualStyleBackColor = true;
            this.cbFeedWatchdog.CheckedChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // cbSuppressInterrupts
            // 
            this.cbSuppressInterrupts.AutoSize = true;
            this.cbSuppressInterrupts.Checked = true;
            this.cbSuppressInterrupts.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbSuppressInterrupts.Location = new System.Drawing.Point(6, 50);
            this.cbSuppressInterrupts.Name = "cbSuppressInterrupts";
            this.cbSuppressInterrupts.Size = new System.Drawing.Size(221, 17);
            this.cbSuppressInterrupts.TabIndex = 15;
            this.cbSuppressInterrupts.Text = "Suppress interrupts during single-stepping";
            this.cbSuppressInterrupts.UseVisualStyleBackColor = true;
            this.cbSuppressInterrupts.CheckedChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // cbCmdline
            // 
            this.cbCmdline.AutoSize = true;
            this.cbCmdline.Location = new System.Drawing.Point(6, 4);
            this.cbCmdline.Name = "cbCmdline";
            this.cbCmdline.Size = new System.Drawing.Size(195, 17);
            this.cbCmdline.TabIndex = 11;
            this.cbCmdline.Text = "Additional command-line arguments:";
            this.cbCmdline.UseVisualStyleBackColor = true;
            this.cbCmdline.CheckedChanged += new System.EventHandler(this.cbCmdline_CheckedChanged);
            // 
            // cbQuickSpeed
            // 
            this.cbQuickSpeed.AutoSize = true;
            this.cbQuickSpeed.Location = new System.Drawing.Point(6, 27);
            this.cbQuickSpeed.Name = "cbQuickSpeed";
            this.cbQuickSpeed.Size = new System.Drawing.Size(112, 17);
            this.cbQuickSpeed.TabIndex = 13;
            this.cbQuickSpeed.Text = "Set explicit speed:";
            this.cbQuickSpeed.UseVisualStyleBackColor = true;
            this.cbQuickSpeed.CheckedChanged += new System.EventHandler(this.cbQuickSpeed_CheckedChanged);
            // 
            // numSpeed2
            // 
            this.numSpeed2.Enabled = false;
            this.numSpeed2.Location = new System.Drawing.Point(125, 26);
            this.numSpeed2.Maximum = new decimal(new int[] {
            100000,
            0,
            0,
            0});
            this.numSpeed2.Name = "numSpeed2";
            this.numSpeed2.Size = new System.Drawing.Size(70, 20);
            this.numSpeed2.TabIndex = 14;
            this.numSpeed2.Value = new decimal(new int[] {
            3000,
            0,
            0,
            0});
            this.numSpeed2.ValueChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // openOCDScriptSelector1
            // 
            this.openOCDScriptSelector1.Dock = System.Windows.Forms.DockStyle.Top;
            this.openOCDScriptSelector1.Location = new System.Drawing.Point(0, 28);
            this.openOCDScriptSelector1.Name = "openOCDScriptSelector1";
            this.openOCDScriptSelector1.Padding = new System.Windows.Forms.Padding(125, 0, 0, 0);
            this.openOCDScriptSelector1.Size = new System.Drawing.Size(359, 22);
            this.openOCDScriptSelector1.SubdirectoryName = null;
            this.openOCDScriptSelector1.TabIndex = 3;
            this.openOCDScriptSelector1.Visible = false;
            this.openOCDScriptSelector1.ValueChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // OpenOCDDebugConfigurator
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.panel2);
            this.Controls.Add(this.panel3);
            this.Controls.Add(this.openOCDScriptSelector1);
            this.Controls.Add(this.panel1);
            this.Name = "OpenOCDDebugConfigurator";
            this.Size = new System.Drawing.Size(359, 297);
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            this.pnlFLASH.ResumeLayout(false);
            this.pnlFLASH.PerformLayout();
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.panel3.ResumeLayout(false);
            this.panel3.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numSpeed2)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.ComboBox cbProgramMode;
        private System.Windows.Forms.ComboBox cbResetMode;
        private System.Windows.Forms.TableLayoutPanel pnlFLASH;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.ComboBox comboBox6;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.ComboBox comboBox5;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.ComboBox comboBox4;
        private System.Windows.Forms.ComboBox comboBox3;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.ComboBox comboBox2;
        private System.Windows.Forms.Panel panel1;
        private System.Windows.Forms.Button btnDetectIface;
        private System.Windows.Forms.ComboBox cbQuickInterface;
        private System.Windows.Forms.Label label1;
        private OpenOCDPackage.OpenOCDScriptSelector openOCDScriptSelector1;
        private System.Windows.Forms.Panel panel3;
        private System.Windows.Forms.CheckBox cbQuickSpeed;
        private System.Windows.Forms.NumericUpDown numSpeed2;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox txtExtraArgs;
        private System.Windows.Forms.CheckBox cbFeedWatchdog;
        private System.Windows.Forms.CheckBox cbSuppressInterrupts;
        private System.Windows.Forms.CheckBox cbCmdline;
    }
}
