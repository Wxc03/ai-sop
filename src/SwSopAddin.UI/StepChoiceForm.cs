using System;
using System.Drawing;
using System.Windows.Forms;

namespace SwSopAddin.UI
{
    /// <summary>
    /// W7+ 分步执行选择窗 — 7 个按钮对应 7 个 RunStep 步骤,点哪个就 RunStep 哪个。
    /// 用户调试 SOP 流程时不必跑完整 7 步,可以单独跑某步看效果。
    ///
    /// 全代码构建(跟 ConfigForm 一样),不依赖 .Designer.cs。
    /// </summary>
    public class StepChoiceForm : Form
    {
        /// <summary>用户点的是哪个 step(1-7)。0 = 用户取消或关闭窗。</summary>
        public int SelectedStep { get; private set; }

        private const int FormWidth = 460;
        private const int ContentWidth = FormWidth - 24;  // 左右各留 12px 边距

        public StepChoiceForm()
        {
            SuspendLayout();

            Text = "分步执行 — 选一个步骤";
            MinimumSize = new Size(FormWidth, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei", 9F);

            var lblHint = new Label
            {
                Text = "每步会单独跑 RunStep(N),失败状态保留供检查。建议顺序:\r\n" +
                       "自动爆炸: 1 → 2 → (切到新建的工程图窗口) → 3..7\r\n" +
                       "手动爆炸: 保存爆炸视图后，从 2 → (切到工程图窗口) → 3..7\r\n" +
                       "Step 3-7 需要工程图为当前活动窗口,原装配体窗口不要关(不需要是活动窗口)。",
                AutoSize = false,
                Width = ContentWidth,
                Height = 0,  // 下面用 PreferredHeight 算实际高度,允许换行
                Location = new Point(12, 10),
                ForeColor = Color.DimGray,
            };
            lblHint.Height = lblHint.GetPreferredSize(new Size(ContentWidth, 0)).Height;
            Controls.Add(lblHint);

            // 7 个 step 按钮,均匀排开
            int y = lblHint.Bottom + 12;
            int h = 34;
            int gap = 6;
            for (int step = 1; step <= 7; step++)
            {
                int captured = step;  // closure
                var btn = new Button
                {
                    Text = GetStepLabel(step),
                    Width = ContentWidth,
                    Height = h,
                    Location = new Point(12, y),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    FlatStyle = FlatStyle.System,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 0, 0),
                };
                btn.Click += (s, e) =>
                {
                    SelectedStep = captured;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                Controls.Add(btn);
                y += h + gap;
            }

            y += 6;  // 取消按钮上方留白

            ClientSize = new Size(FormWidth, y + 28 + 8);

            // 取消按钮
            var btnCancel = new Button
            {
                Text = "取消",
                Width = 90,
                Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            btnCancel.Location = new Point(ClientSize.Width - btnCancel.Width - 12, y);
            btnCancel.Click += (s, e) =>
            {
                SelectedStep = 0;
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            ResumeLayout(false);
        }

        private static string GetStepLabel(int step)
        {
            switch (step)
            {
                case 1: return "Step 1/7  M2  爆炸";
                case 2: return "Step 2/7  M2.5  建工程图";
                case 3: return "Step 3/7  M4  插爆炸视图";
                case 4: return "Step 4/7  M5  球标";
                case 5: return "Step 5/7  M6  BOM";
                case 6: return "Step 6/7  W5  智能布局";
                case 7: return "Step 7/7  PDF 导出";
                default: return "Step " + step;
            }
        }
    }
}
