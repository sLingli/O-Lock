using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;
using static OLock.TcpConnectionChecker;

namespace OLock
{
    // 连接测试窗口入口：静态持窗，防止带定时器的窗口被重复创建
    internal static class ConnectionTestForm
    {
        private static ConnectionTestWindow? activeWindow;

        internal static void ShowTestWindow()
        {
            if (activeWindow != null && !activeWindow.IsDisposed)
            {
                if (activeWindow.WindowState == FormWindowState.Minimized)
                    activeWindow.WindowState = FormWindowState.Normal;
                activeWindow.Show();
                activeWindow.Activate();
                activeWindow.BringToFront();
                return;
            }
            activeWindow = new ConnectionTestWindow();
            activeWindow.FormClosed += (_, _) => activeWindow = null;
            activeWindow.Show();
            activeWindow.Activate();
        }
    }

    // 连接测试窗口：一键检测 + 三行诊断（进程/连接/原因）+ 断开测试。
    // 断开测试走真实监控链路：暂停自动监控后由本窗口定时器每秒 await TickCoreAsync()，
    // 托盘图标随状态机真实变色；第一次触发锁屏/睡眠指令时执行器已被替换为
    // DryRunLockExecutor，只报告不动作，随后自动恢复监控——全程屏幕不锁、电脑不睡。
    internal sealed class ConnectionTestWindow : Form
    {
        // 状态圆点颜色：与托盘图标同源
        private static readonly Color DotGray = Color.FromArgb(128, 128, 128);
        private static readonly Color DotAmber = Color.FromArgb(255, 200, 0);
        private static readonly Color DotGreen = Color.FromArgb(0, 200, 0);
        private static readonly Color DotRed = Color.FromArgb(200, 0, 0);

        private readonly DotPanel statusDot = new DotPanel();
        private readonly Label statusLabel = new Label();
        private readonly Button checkButton = new Button();
        private readonly Label detailLabel = new Label();
        private readonly Panel guidePanel = new Panel();
        private readonly Label guideLabel = new Label();
        private readonly Button guideButton = new Button();
        private readonly Label footerLabel = new Label();

        // 断开测试驱动：窗口自有定时器，演练期间是监控的唯一驱动源
        private readonly System.Windows.Forms.Timer drillTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private bool drillLockHandled;   // 第一次模拟执行报告即完成，防止 dry-run 下每秒重复 Lock 刷屏
        private bool drillUiActive;      // 窗口侧的测试态标志（按钮/引导条切换用）
        private bool drillTickRunning;   // 防止异步 Tick 重入

        public ConnectionTestWindow()
        {
            Text = $"{Tr("test_title")} - {APP_NAME} {AppVersion}";
            ClientSize = new Size(500, 300);
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            // 状态行：圆点 + 状态文字 + 检测按钮
            statusDot.Location = new Point(16, 20);
            statusLabel.Location = new Point(42, 18);
            statusLabel.Size = new Size(330, 20);
            statusLabel.Text = Tr("test_idle");

            checkButton.Location = new Point(385, 14);
            checkButton.Size = new Size(100, 28);
            checkButton.Text = Tr("test_check_now");
            checkButton.Click += (_, _) => _ = CheckNow();

            // 诊断三行
            detailLabel.Location = new Point(16, 56);
            detailLabel.Size = new Size(468, 64);
            detailLabel.Text = $"{Tr("test_detail_process", "—")}\n{Tr("test_detail_connection", "—")}\n{Tr("test_detail_diagnosis", "—")}";

            // 引导条：已连接 → 断开测试入口；未连接 → 先连接提示；完成态 → 测试报告
            guidePanel.Location = new Point(16, 136);
            guidePanel.Size = new Size(468, 64);
            guidePanel.Visible = false;

            guideLabel.Location = new Point(10, 6);
            guideLabel.Size = new Size(320, 52);
            guideLabel.Text = "";

            guideButton.Location = new Point(340, 14);
            guideButton.Size = new Size(120, 28);
            guideButton.Text = Tr("test_drill_start");
            guideButton.Click += (_, _) => ToggleDrill();

            guidePanel.Controls.Add(guideLabel);
            guidePanel.Controls.Add(guideButton);

            // 底部说明
            footerLabel.Location = new Point(16, 236);
            footerLabel.Size = new Size(468, 48);
            footerLabel.ForeColor = SystemColors.GrayText;
            footerLabel.Text = Tr("test_footer");

            Controls.Add(statusDot);
            Controls.Add(statusLabel);
            Controls.Add(checkButton);
            Controls.Add(detailLabel);
            Controls.Add(guidePanel);
            Controls.Add(footerLabel);

            // 打开窗口时自动检测一次
            Shown += (_, _) => _ = CheckNow();

            drillTimer.Tick += (_, _) => DrillTick();
        }

        // 立即检测：跑完整检测链路并渲染诊断详情
        private async Task CheckNow()
        {
            if (DrillActive || drillUiActive) return;
            checkButton.Enabled = false;
            checkButton.Text = Tr("test_checking");
            SetDot(DotAmber);
            statusLabel.Text = Tr("test_checking");
            try
            {
                var result = await CheckPhoneConnectionDetailAsync();
                if (IsDisposed || !IsHandleCreated || DrillActive || drillUiActive) return;

                LogInfo("Test", $"Manual check: {(result.IsConnected ? "Connected" : "Disconnected")} " +
                    $"(Reason: {result.Reason}, Hit: {result.RemoteIp ?? "-"})");
                Render(result);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && IsHandleCreated && !DrillActive && !drillUiActive)
                {
                    LogError("Test", $"Connection check failed: {Describe(ex)}");
                    SetDot(DotRed);
                    statusLabel.Text = Tr("test_disconnected");
                }
            }
            finally
            {
                if (!IsDisposed && IsHandleCreated && !DrillActive && !drillUiActive)
                {
                    checkButton.Enabled = true;
                    checkButton.Text = Tr("test_recheck");
                }
            }
        }

        private void Render(PhoneConnectionResult result)
        {
            SetDot(result.IsConnected ? DotGreen : DotRed);
            statusLabel.Text = result.IsConnected ? Tr("test_connected") : Tr("test_disconnected");

            string processText = result.AppRunning ? Tr("test_proc_running") : Tr("test_proc_not_running");
            string connectionText = result.IsConnected
                ? Tr("test_conn_hit", result.RemoteIp ?? "-", result.Pid?.ToString() ?? "-")
                : result.EstablishedCount > 0
                    ? Tr("test_conn_established", result.EstablishedCount)
                    : Tr("test_conn_none");

            detailLabel.Text = $"{Tr("test_detail_process", processText)}\n" +
                               $"{Tr("test_detail_connection", connectionText)}\n" +
                               $"{Tr("test_detail_diagnosis", DescribeReason(result))}";

            // 引导条：已连接 → 断开测试入口；未连接 → 先连接提示
            if (result.IsConnected)
            {
                guideLabel.Text = Tr("test_guide_connected");
                guideButton.Text = Tr("test_drill_start");
                guideButton.Visible = true;
                guidePanel.Visible = true;
            }
            else
            {
                guideLabel.Text = Tr("test_guide_disconnected");
                guideButton.Visible = false;
                guidePanel.Visible = true;
            }
        }

        // ==================== 断开测试 ====================

        // 引导条按钮：未在测试中 → 开始；测试中 → 手动结束
        private void ToggleDrill()
        {
            if (drillUiActive)
            {
                StopDisconnectTest(waitReconnect: true);
                if (monitor.WaitReconnectAfterDrill || !monitor.IsOnline)
                {
                    guideLabel.Text = Tr("test_guide_disconnected");
                    guideButton.Visible = false;
                    _ = CheckNow();
                }
                else
                {
                    ShowIdleGuide();
                }
            }
            else
            {
                BeginDisconnectTest();
            }
        }

        internal void BeginDisconnectTest()
        {
            if (DrillActive) return;
            drillLockHandled = false;
            drillUiActive = true;

            // 订阅模拟执行报告；Program.StartDrill 暂停自动监控、替换执行器并强制对齐在线状态
            DrillReported += OnDrillReported;
            StartDrill();

            drillTimer.Start();
            checkButton.Enabled = false;
            guideButton.Text = Tr("test_drill_end");
            SetDot(DotGreen);
            statusLabel.Text = Tr("test_drill_waiting");
            guideLabel.Text = Tr("test_drill_waiting");
        }

        // 每秒异步驱动一次监控内核并同步展示最新状态（托盘图标由 TickCoreAsync 内的 UpdateIcon 同步刷新）
        private async void DrillTick()
        {
            if (!DrillActive || drillTickRunning)
                return;

            drillTickRunning = true;
            try
            {
                await TickCoreAsync();

                // 若本秒已触发模拟锁屏（OnDrillReported 已调用 StopDisconnectTest），不再覆盖完成态文案
                if (!DrillActive || IsDisposed)
                    return;

                // 进程退出 / 屏幕状态变化会让状态机回到等待态：测试到此为止
                if (monitor.IsWaitingForApp || monitor.WasLocked)
                {
                    StopDisconnectTest(waitReconnect: false);
                    ShowInterrupted();
                    return;
                }

                if (monitor.IsOnline)
                {
                    SetDot(DotGreen);
                    guideLabel.Text = Tr("test_drill_online");
                }
                else
                {
                    SetDot(DotRed);
                    guideLabel.Text = Tr("test_drill_countdown", monitor.OfflineSeconds, config.OfflineSeconds);
                }
            }
            finally
            {
                drillTickRunning = false;
            }
        }

        // 第一次模拟执行报告 = 决策链走完，自动结束并展示完成报告
        private void OnDrillReported(string message)
        {
            if (drillLockHandled || IsDisposed) return;
            drillLockHandled = true;
            StopDisconnectTest(waitReconnect: true);

            SetDot(DotAmber);
            statusLabel.Text = Tr("test_drill_done");
            guideLabel.Text = $"{message}\n{Tr("test_drill_conclusion")}";
            guideButton.Text = Tr("test_drill_start");
            LogInfo("Drill", "Disconnect drill completed, monitoring resumed");
        }

        // 恢复初始引导条（保留"已连接"入口状态）
        private void ShowIdleGuide()
        {
            SetDot(DotGreen);
            statusLabel.Text = Tr("test_connected");
            guideButton.Visible = true;
            guideButton.Text = Tr("test_drill_start");
            guideLabel.Text = Tr("test_guide_connected");
        }

        private void ShowInterrupted()
        {
            SetDot(DotGray);
            statusLabel.Text = Tr("test_disconnected");
            guideLabel.Text = Tr("test_drill_interrupted");
            guideButton.Text = Tr("test_drill_start");
        }

        // 窗口侧统一收尾：停表、退订事件、恢复按钮。Program.EndDrill 幂等，重复调用无害。
        private void StopDisconnectTest(bool waitReconnect)
        {
            drillTimer.Stop();
            DrillReported -= OnDrillReported;
            EndDrill(waitReconnect);
            drillUiActive = false;
            if (!IsDisposed)
                checkButton.Enabled = true;
        }

        private string DescribeReason(PhoneConnectionResult result) => result.Reason switch
        {
            ConnectionReason.Connected => Tr("test_reason_connected"),
            ConnectionReason.ProcessNotRunning => Tr("test_reason_process"),
            ConnectionReason.NoEstablishedConnection => Tr("test_reason_no_connection"),
            ConnectionReason.IpNotMatched => Tr("test_reason_ip_mismatch", result.EstablishedCount, string.Join(", ", result.SampleRemoteIps)),
            _ => Tr("test_reason_check_failed")
        };

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (drillUiActive || DrillActive)
            {
                StopDisconnectTest(waitReconnect: true);
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (drillUiActive || DrillActive)
                {
                    StopDisconnectTest(waitReconnect: true);
                }
                drillTimer.Stop();
                drillTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SetDot(Color color)
        {
            statusDot.DotColor = color;
            statusDot.Invalidate();
        }

        // 自绘圆形状态点（颜色与托盘图标同源，开启双缓冲防闪烁）
        private sealed class DotPanel : Panel
        {
            public Color DotColor { get; set; } = DotGray;

            public DotPanel()
            {
                Size = new Size(18, 18);
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(DotColor);
                e.Graphics.FillEllipse(brush, 2, 2, 13, 13);
            }
        }
    }
}
