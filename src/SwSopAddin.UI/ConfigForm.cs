using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.UI
{
    /// <summary>
    /// W1.3.c 壳 — 7 个 tab,每个只读显示对应配置块的当前值。
    /// W2 起在每个 tab 里替换为真正的输入控件(TextBox / NumericUpDown / ComboBox / FolderPicker)。
    /// 全代码构建,不依赖 .Designer.cs,所以无需 VS WinForms 设计器 / ManagedDesktop 工作负载。
    /// </summary>
    public class ConfigForm : Form
    {
        public ConfigForm(ConfigStore config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            SuspendLayout();

            Text = "SOP 生成器 配置 (W1.3.c 壳)";
            ClientSize = new Size(680, 520);
            MinimumSize = new Size(540, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei", 9F);

            // 顶部 tab
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            tabs.TabPages.Add(MakeTab("爆炸",
                "默认分离距离 (mm):  " + config.Explode.DefaultDistanceMm + Environment.NewLine +
                "跳过属性名:        " + config.Explode.SkipPropertyName + Environment.NewLine +
                "跳过名称前缀:      " + (config.Explode.SkipNamePrefixes == null ? "(无)" : string.Join(", ", config.Explode.SkipNamePrefixes)) + Environment.NewLine + Environment.NewLine +
                "[W2 起接入数值/列表控件可编辑]"));

            tabs.TabPages.Add(MakeTab("图纸",
                "模板路径:  " + config.Drawing.TemplatePath + Environment.NewLine +
                "图纸规格:  " + config.Drawing.PaperSize + Environment.NewLine + Environment.NewLine +
                "[W2 起接入文件选择器/下拉框]"));

            tabs.TabPages.Add(MakeTab("球标",
                "样式:        " + config.Balloon.Style + Environment.NewLine +
                "箭头样式:    " + config.Balloon.ArrowStyle + Environment.NewLine + Environment.NewLine +
                "[W3 起接入下拉框]"));

            tabs.TabPages.Add(MakeTab("BOM",
                "BOM 配置 W3 起加入(模板路径/列定义)"));

            tabs.TabPages.Add(MakeTab("PDF",
                "输出目录:  " + config.Pdf.OutputDir + Environment.NewLine +
                "嵌入字体:  " + config.Pdf.EmbedFonts + Environment.NewLine + Environment.NewLine +
                "[W4 起接入文件夹选择器/复选框]"));

            tabs.TabPages.Add(MakeTab("过滤",
                "见 '爆炸' tab 内的 '跳过属性名' 与 '跳过名称前缀'"));

            tabs.TabPages.Add(MakeTab("关于",
                "SwSopAddin v" + Assembly.GetExecutingAssembly().GetName().Version + " (UI 程序集)" + Environment.NewLine +
                Environment.NewLine +
                "配置文件:  " + AppPaths.ConfigJson + Environment.NewLine +
                "日志目录:  " + AppPaths.LogsDir + Environment.NewLine +
                "Schema:    v" + config.SchemaVersion + Environment.NewLine +
                Environment.NewLine +
                "本窗体只读,W2 起接入可编辑控件 + 保存/取消按钮。"));

            Controls.Add(tabs);

            // 底部按钮条
            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 46 };

            var btnClose = new Button
            {
                Text = "关闭",
                Width = 90,
                Height = 28,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            btnClose.Location = new Point(btnPanel.ClientSize.Width - btnClose.Width - 12, 9);
            btnClose.Click += (s, e) => Close();
            btnPanel.Controls.Add(btnClose);

            var lblHint = new Label
            {
                Text = "W1.3.c 阶段为只读壳;改 JSON 后重启 SW 生效",
                AutoSize = true,
                Location = new Point(12, 14),
                ForeColor = Color.Gray
            };
            btnPanel.Controls.Add(lblHint);

            Controls.Add(btnPanel);

            AcceptButton = btnClose;
            CancelButton = btnClose;

            ResumeLayout(false);
        }

        private static TabPage MakeTab(string title, string body)
        {
            var tab = new TabPage(title) { Padding = new Padding(8) };
            var txt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
                BackColor = Color.White,
                Text = body
            };
            tab.Controls.Add(txt);
            return tab;
        }
    }
}
