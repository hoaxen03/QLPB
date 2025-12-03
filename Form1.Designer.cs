namespace ReadFileFTP
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.TextBox txtRemote;
        private System.Windows.Forms.Button btnList;

        private System.Windows.Forms.ListView lvFiles;

        private System.Windows.Forms.Label lblLocal;
        private System.Windows.Forms.TextBox txtLocal;
        private System.Windows.Forms.Button btnBrowseLocal;

        private System.Windows.Forms.Button btnDownload;

        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.TextBox txtLog;

        /// <summary>
        ///  Clean up any resources being used.
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

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            btnConnect = new Button();
            txtRemote = new TextBox();
            btnList = new Button();
            lvFiles = new ListView();
            columnHeader1 = new ColumnHeader();
            columnHeader2 = new ColumnHeader();
            columnHeader3 = new ColumnHeader();
            _smallIcons = new ImageList(components);
            lblLocal = new Label();
            txtLocal = new TextBox();
            btnBrowseLocal = new Button();
            btnDownload = new Button();
            progressBar = new ProgressBar();
            txtLog = new TextBox();
            button1 = new Button();
            lblSpeed2 = new Label();
            btnCancel = new Button();
            maintoolStripStatusLabelStatus = new StatusStrip();
            toolStripStatusLabelStatus = new ToolStripStatusLabel();
            button2 = new Button();
            maintoolStripStatusLabelStatus.SuspendLayout();
            SuspendLayout();
            // 
            // btnConnect
            // 
            btnConnect.Location = new Point(552, 85);
            btnConnect.Name = "btnConnect";
            btnConnect.Size = new Size(75, 24);
            btnConnect.TabIndex = 6;
            btnConnect.Text = "Kết nối";
            btnConnect.UseVisualStyleBackColor = true;
            btnConnect.Click += BtnConnect_Click;
            // 
            // txtRemote
            // 
            txtRemote.Location = new Point(12, 87);
            txtRemote.Name = "txtRemote";
            txtRemote.Size = new Size(357, 23);
            txtRemote.TabIndex = 8;
            txtRemote.Text = "/";
            // 
            // btnList
            // 
            btnList.Location = new Point(476, 85);
            btnList.Name = "btnList";
            btnList.Size = new Size(75, 24);
            btnList.TabIndex = 9;
            btnList.Text = "Làm mới";
            btnList.UseVisualStyleBackColor = true;
            btnList.Click += BtnList_Click;
            // 
            // lvFiles
            // 
            lvFiles.Columns.AddRange(new ColumnHeader[] { columnHeader1, columnHeader2, columnHeader3 });
            lvFiles.FullRowSelect = true;
            lvFiles.GridLines = true;
            lvFiles.Location = new Point(10, 126);
            lvFiles.Name = "lvFiles";
            lvFiles.Size = new Size(617, 369);
            lvFiles.TabIndex = 10;
            lvFiles.UseCompatibleStateImageBehavior = false;
            lvFiles.View = View.Details;
            lvFiles.DoubleClick += LvFiles_DoubleClick;
            // 
            // columnHeader1
            // 
            columnHeader1.Text = "Tên file";
            columnHeader1.Width = 250;
            // 
            // columnHeader2
            // 
            columnHeader2.Text = "Kích thước (byte)";
            columnHeader2.Width = 110;
            // 
            // columnHeader3
            // 
            columnHeader3.Text = "Ngày sửa";
            columnHeader3.Width = 150;
            // 
            // _smallIcons
            // 
            _smallIcons.ColorDepth = ColorDepth.Depth32Bit;
            _smallIcons.ImageSize = new Size(16, 16);
            _smallIcons.TransparentColor = Color.Transparent;
            // 
            // lblLocal
            // 
            lblLocal.AutoSize = true;
            lblLocal.Location = new Point(10, 504);
            lblLocal.Name = "lblLocal";
            lblLocal.Size = new Size(128, 15);
            lblLocal.TabIndex = 11;
            lblLocal.Text = "Đường dẫn app (local):";
            // 
            // txtLocal
            // 
            txtLocal.Location = new Point(140, 501);
            txtLocal.Name = "txtLocal";
            txtLocal.Size = new Size(374, 23);
            txtLocal.TabIndex = 12;
            txtLocal.Text = "C:\\Users\\trung\\Downloads";
            // 
            // btnBrowseLocal
            // 
            btnBrowseLocal.Location = new Point(547, 500);
            btnBrowseLocal.Name = "btnBrowseLocal";
            btnBrowseLocal.Size = new Size(80, 24);
            btnBrowseLocal.TabIndex = 13;
            btnBrowseLocal.Text = "Chọn...";
            btnBrowseLocal.UseVisualStyleBackColor = true;
            btnBrowseLocal.Click += BtnBrowseLocal_Click;
            // 
            // btnDownload
            // 
            btnDownload.Location = new Point(10, 40);
            btnDownload.Name = "btnDownload";
            btnDownload.Size = new Size(142, 28);
            btnDownload.TabIndex = 14;
            btnDownload.Text = "Tải xuống";
            btnDownload.UseVisualStyleBackColor = true;
            btnDownload.Click += BtnDownload_Click;
            // 
            // progressBar
            // 
            progressBar.Location = new Point(10, 12);
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(617, 22);
            progressBar.TabIndex = 17;
            // 
            // txtLog
            // 
            txtLog.Location = new Point(10, 530);
            txtLog.Multiline = true;
            txtLog.Name = "txtLog";
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Size = new Size(617, 96);
            txtLog.TabIndex = 18;
            // 
            // button1
            // 
            button1.Location = new Point(400, 86);
            button1.Name = "button1";
            button1.Size = new Size(75, 23);
            button1.TabIndex = 19;
            button1.Text = "Trở về";
            button1.UseVisualStyleBackColor = true;
            button1.Click += BtnUp_Click;
            // 
            // lblSpeed2
            // 
            lblSpeed2.AutoSize = true;
            lblSpeed2.Location = new Point(475, 47);
            lblSpeed2.Name = "lblSpeed2";
            lblSpeed2.Size = new Size(0, 15);
            lblSpeed2.TabIndex = 23;
            // 
            // btnCancel
            // 
            btnCancel.Location = new Point(294, 40);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(138, 28);
            btnCancel.TabIndex = 24;
            btnCancel.Text = "Hủy tải xuống";
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.Click += BtnCancel_Click;
            // 
            // maintoolStripStatusLabelStatus
            // 
            maintoolStripStatusLabelStatus.Items.AddRange(new ToolStripItem[] { toolStripStatusLabelStatus });
            maintoolStripStatusLabelStatus.Location = new Point(0, 633);
            maintoolStripStatusLabelStatus.Name = "maintoolStripStatusLabelStatus";
            maintoolStripStatusLabelStatus.Size = new Size(645, 22);
            maintoolStripStatusLabelStatus.TabIndex = 25;
            maintoolStripStatusLabelStatus.Text = "statusStrip1";
            // 
            // toolStripStatusLabelStatus
            // 
            toolStripStatusLabelStatus.Name = "toolStripStatusLabelStatus";
            toolStripStatusLabelStatus.RightToLeft = RightToLeft.No;
            toolStripStatusLabelStatus.Size = new Size(74, 17);
            toolStripStatusLabelStatus.Text = "Chưa kết nối";
            // 
            // button2
            // 
            button2.Location = new Point(158, 40);
            button2.Name = "button2";
            button2.Size = new Size(121, 28);
            button2.TabIndex = 26;
            button2.Text = "Cập nhật";
            button2.UseVisualStyleBackColor = true;
            button2.Click += button2_Click;
            // 
            // Form1
            // 
            ClientSize = new Size(645, 655);
            Controls.Add(button2);
            Controls.Add(maintoolStripStatusLabelStatus);
            Controls.Add(btnCancel);
            Controls.Add(lblSpeed2);
            Controls.Add(button1);
            Controls.Add(btnConnect);
            Controls.Add(txtRemote);
            Controls.Add(btnList);
            Controls.Add(lvFiles);
            Controls.Add(lblLocal);
            Controls.Add(txtLocal);
            Controls.Add(btnBrowseLocal);
            Controls.Add(btnDownload);
            Controls.Add(progressBar);
            Controls.Add(txtLog);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "FTP File Manager ";
            Load += Form1_Load;
            maintoolStripStatusLabelStatus.ResumeLayout(false);
            maintoolStripStatusLabelStatus.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }
        #endregion

        private ColumnHeader columnHeader1;
        private ColumnHeader columnHeader2;
        private ColumnHeader columnHeader3;
        private ImageList _smallIcons;
        private Button button1;
        private Label lblSpeed2;
        private Button btnCancel;
        private StatusStrip maintoolStripStatusLabelStatus;
        private ToolStripStatusLabel toolStripStatusLabelStatus;
        private Button button2;
    }
}
