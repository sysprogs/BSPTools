namespace ESP8266DebugPackage
{
    partial class ESP8266DebugConfigurator
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ESP8266DebugConfigurator));
            this.btnOpenScript = new System.Windows.Forms.Button();
            this.txtXtOcd = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.linkLabel1 = new System.Windows.Forms.LinkLabel();
            this.label2 = new System.Windows.Forms.Label();
            this.cbDebugInterface = new System.Windows.Forms.ComboBox();
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
            this.label8 = new System.Windows.Forms.Label();
            this.cbResetMode = new System.Windows.Forms.ComboBox();
            this.openFileDialog1 = new System.Windows.Forms.OpenFileDialog();
            this.panel2 = new System.Windows.Forms.Panel();
            this.label10 = new System.Windows.Forms.Label();
            this.cbProgramMode = new System.Windows.Forms.ComboBox();
            this.panel1 = new System.Windows.Forms.Panel();
            this.pnlDebuggerSettings = new System.Windows.Forms.Panel();
            this.pnlCustom = new System.Windows.Forms.Panel();
            this.label9 = new System.Windows.Forms.Label();
            this.txtTopologyFile = new System.Windows.Forms.TextBox();
            this.button1 = new System.Windows.Forms.Button();
            this.openFileDialog2 = new System.Windows.Forms.OpenFileDialog();
            this.pnlFLASH.SuspendLayout();
            this.panel2.SuspendLayout();
            this.panel1.SuspendLayout();
            this.pnlCustom.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnOpenScript
            // 
            this.btnOpenScript.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOpenScript.Image = ((System.Drawing.Image)(resources.GetObject("btnOpenScript.Image")));
            this.btnOpenScript.Location = new System.Drawing.Point(279, 4);
            this.btnOpenScript.Name = "btnOpenScript";
            this.btnOpenScript.Size = new System.Drawing.Size(23, 23);
            this.btnOpenScript.TabIndex = 2;
            this.btnOpenScript.UseVisualStyleBackColor = true;
            this.btnOpenScript.Click += new System.EventHandler(this.btnOpenScript_Click);
            // 
            // txtXtOcd
            // 
            this.txtXtOcd.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtXtOcd.Location = new System.Drawing.Point(120, 6);
            this.txtXtOcd.Name = "txtXtOcd";
            this.txtXtOcd.Size = new System.Drawing.Size(153, 20);
            this.txtXtOcd.TabIndex = 1;
            this.txtXtOcd.TextChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(3, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(96, 13);
            this.label1.TabIndex = 5;
            this.label1.Text = "Path to xt-ocd.exe:";
            // 
            // linkLabel1
            // 
            this.linkLabel1.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.linkLabel1.AutoSize = true;
            this.linkLabel1.Location = new System.Drawing.Point(308, 9);
            this.linkLabel1.Name = "linkLabel1";
            this.linkLabel1.Size = new System.Drawing.Size(87, 13);
            this.linkLabel1.TabIndex = 3;
            this.linkLabel1.TabStop = true;
            this.linkLabel1.Text = "Download xt-ocd";
            this.linkLabel1.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.linkLabel1_LinkClicked);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(3, 35);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(86, 13);
            this.label2.TabIndex = 5;
            this.label2.Text = "Debug interface:";
            // 
            // cbDebugInterface
            // 
            this.cbDebugInterface.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbDebugInterface.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbDebugInterface.FormattingEnabled = true;
            this.cbDebugInterface.Location = new System.Drawing.Point(120, 32);
            this.cbDebugInterface.Name = "cbDebugInterface";
            this.cbDebugInterface.Size = new System.Drawing.Size(275, 21);
            this.cbDebugInterface.TabIndex = 10;
            this.cbDebugInterface.SelectedIndexChanged += new System.EventHandler(this.cbDebugInterface_SelectedIndexChanged);
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
            this.pnlFLASH.Size = new System.Drawing.Size(352, 83);
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
            this.comboBox6.Size = new System.Drawing.Size(89, 21);
            this.comboBox6.TabIndex = 5;
            this.comboBox6.Tag = "com.sysprogs.esp8266.xt-ocd.flash_mode";
            this.comboBox6.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox6.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(166, 27);
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
            this.comboBox5.Location = new System.Drawing.Point(259, 30);
            this.comboBox5.Name = "comboBox5";
            this.comboBox5.Size = new System.Drawing.Size(90, 21);
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
            this.comboBox4.Size = new System.Drawing.Size(89, 21);
            this.comboBox4.TabIndex = 3;
            this.comboBox4.Tag = "com.sysprogs.esp8266.xt-ocd.flash_freq";
            this.comboBox4.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox4.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // comboBox3
            // 
            this.comboBox3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.comboBox3.FormattingEnabled = true;
            this.comboBox3.Location = new System.Drawing.Point(259, 3);
            this.comboBox3.Name = "comboBox3";
            this.comboBox3.Size = new System.Drawing.Size(90, 21);
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
            this.label4.Location = new System.Drawing.Point(166, 0);
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
            this.comboBox2.Size = new System.Drawing.Size(89, 21);
            this.comboBox2.TabIndex = 1;
            this.comboBox2.Tag = "com.sysprogs.esp8266.xt-ocd.prog_sector_size";
            this.comboBox2.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            this.comboBox2.TextUpdate += new System.EventHandler(this.SettingsChangedHandler);
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
            // cbResetMode
            // 
            this.cbResetMode.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbResetMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbResetMode.FormattingEnabled = true;
            this.cbResetMode.Location = new System.Drawing.Point(120, 3);
            this.cbResetMode.Name = "cbResetMode";
            this.cbResetMode.Size = new System.Drawing.Size(278, 21);
            this.cbResetMode.TabIndex = 30;
            this.cbResetMode.Tag = "com.sysprogs.esp8266.xt-ocd.flash_start_mode";
            this.cbResetMode.SelectedIndexChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // openFileDialog1
            // 
            this.openFileDialog1.DefaultExt = "exe";
            this.openFileDialog1.Filter = "xt-ocd.exe|xt-ocd.exe";
            // 
            // panel2
            // 
            this.panel2.Controls.Add(this.label10);
            this.panel2.Controls.Add(this.label8);
            this.panel2.Controls.Add(this.cbProgramMode);
            this.panel2.Controls.Add(this.cbResetMode);
            this.panel2.Controls.Add(this.pnlFLASH);
            this.panel2.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel2.Location = new System.Drawing.Point(0, 170);
            this.panel2.Name = "panel2";
            this.panel2.Size = new System.Drawing.Size(398, 147);
            this.panel2.TabIndex = 41;
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
            // cbProgramMode
            // 
            this.cbProgramMode.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.cbProgramMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbProgramMode.FormattingEnabled = true;
            this.cbProgramMode.Location = new System.Drawing.Point(120, 28);
            this.cbProgramMode.Name = "cbProgramMode";
            this.cbProgramMode.Size = new System.Drawing.Size(278, 21);
            this.cbProgramMode.TabIndex = 30;
            this.cbProgramMode.Tag = "com.sysprogs.esp8266.xt-ocd.program_flash";
            this.cbProgramMode.SelectedIndexChanged += new System.EventHandler(this.comboBox1_SelectedIndexChanged);
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.label1);
            this.panel1.Controls.Add(this.btnOpenScript);
            this.panel1.Controls.Add(this.cbDebugInterface);
            this.panel1.Controls.Add(this.txtXtOcd);
            this.panel1.Controls.Add(this.linkLabel1);
            this.panel1.Controls.Add(this.label2);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel1.Location = new System.Drawing.Point(0, 0);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(398, 60);
            this.panel1.TabIndex = 42;
            // 
            // pnlDebuggerSettings
            // 
            this.pnlDebuggerSettings.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlDebuggerSettings.Location = new System.Drawing.Point(0, 60);
            this.pnlDebuggerSettings.Name = "pnlDebuggerSettings";
            this.pnlDebuggerSettings.Size = new System.Drawing.Size(398, 75);
            this.pnlDebuggerSettings.TabIndex = 43;
            // 
            // pnlCustom
            // 
            this.pnlCustom.Controls.Add(this.label9);
            this.pnlCustom.Controls.Add(this.txtTopologyFile);
            this.pnlCustom.Controls.Add(this.button1);
            this.pnlCustom.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlCustom.Location = new System.Drawing.Point(0, 135);
            this.pnlCustom.Name = "pnlCustom";
            this.pnlCustom.Size = new System.Drawing.Size(398, 35);
            this.pnlCustom.TabIndex = 44;
            // 
            // label9
            // 
            this.label9.AutoSize = true;
            this.label9.Location = new System.Drawing.Point(3, 9);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(98, 13);
            this.label9.TabIndex = 5;
            this.label9.Text = "xt-ocd topology file:";
            // 
            // txtTopologyFile
            // 
            this.txtTopologyFile.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtTopologyFile.Location = new System.Drawing.Point(120, 6);
            this.txtTopologyFile.Name = "txtTopologyFile";
            this.txtTopologyFile.Size = new System.Drawing.Size(246, 20);
            this.txtTopologyFile.TabIndex = 1;
            this.txtTopologyFile.TextChanged += new System.EventHandler(this.SettingsChangedHandler);
            // 
            // button1
            // 
            this.button1.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.button1.Image = ((System.Drawing.Image)(resources.GetObject("button1.Image")));
            this.button1.Location = new System.Drawing.Point(372, 4);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(23, 23);
            this.button1.TabIndex = 2;
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // openFileDialog2
            // 
            this.openFileDialog2.DefaultExt = "exe";
            this.openFileDialog2.Filter = "XML files|*.xml";
            // 
            // ESP8266DebugConfigurator
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.panel2);
            this.Controls.Add(this.pnlCustom);
            this.Controls.Add(this.pnlDebuggerSettings);
            this.Controls.Add(this.panel1);
            this.Name = "ESP8266DebugConfigurator";
            this.Size = new System.Drawing.Size(398, 326);
            this.pnlFLASH.ResumeLayout(false);
            this.pnlFLASH.PerformLayout();
            this.panel2.ResumeLayout(false);
            this.panel2.PerformLayout();
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.pnlCustom.ResumeLayout(false);
            this.pnlCustom.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Button btnOpenScript;
        private System.Windows.Forms.TextBox txtXtOcd;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.LinkLabel linkLabel1;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.ComboBox cbDebugInterface;
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
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.ComboBox cbResetMode;
        private System.Windows.Forms.OpenFileDialog openFileDialog1;
        private System.Windows.Forms.Panel panel2;
        private System.Windows.Forms.Panel panel1;
        private System.Windows.Forms.Panel pnlDebuggerSettings;
        private System.Windows.Forms.Panel pnlCustom;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.TextBox txtTopologyFile;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.OpenFileDialog openFileDialog2;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.ComboBox cbProgramMode;
    }
}
