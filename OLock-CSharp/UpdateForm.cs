using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 检查更新与程序内一键更新窗口入口（单实例持窗）
    internal static class UpdateForm
    {
        private static UpdateWindow? activeWindow;

        internal static void ShowUpdateWindow(UpdateCheckResult? prefetched = null)
        {
            if (activeWindow != null && !activeWindow.IsDisposed)
            {
                if (prefetched != null)
                    activeWindow.ApplyPrefetchedIfIdle(prefetched);
                if (activeWindow.WindowState == FormWindowState.Minimized)
                    activeWindow.WindowState = FormWindowState.Normal;
                activeWindow.Show();
                activeWindow.Activate();
                activeWindow.BringToFront();
                return;
            }

            activeWindow = new UpdateWindow(prefetched);
            activeWindow.FormClosed += (_, _) => activeWindow = null;
            activeWindow.Show();
            activeWindow.Activate();
        }
    }

    internal sealed class UpdateWindow : Form
    {
        private static readonly Color ColorSuccess = Color.FromArgb(0, 140, 0);
        private static readonly Color ColorHighlight = Color.FromArgb(0, 102, 204);
        private static readonly Color ColorError = Color.FromArgb(190, 0, 0);

        private readonly Label currentVerLabel = new Label();
        private readonly Label latestVerLabel = new Label();
        private readonly Button recheckOrCancelButton = new Button { UseMnemonic = false };
        private readonly Label statusLabel = new Label();
        private readonly Label notesHeaderLabel = new Label();
        private readonly TextBox notesBox = new TextBox();
        private readonly ProgressBar progressBar = new ProgressBar();
        private readonly Label progressLabel = new Label();
        private readonly Button installButton = new Button { UseMnemonic = false };
        private readonly Button openPageButton = new Button { UseMnemonic = false };
        private readonly Button closeButton = new Button { UseMnemonic = false };

        private UpdateCheckResult? currentResult;
        private CancellationTokenSource? checkCts;
        private CancellationTokenSource? downloadCts;
        private bool isCheckingUpdate;
        private bool isDownloading;

        public UpdateWindow(UpdateCheckResult? prefetched)
        {
            currentResult = prefetched;

            Text = $"{Tr("update_title")} - {APP_NAME} {AppVersion}";
            ClientSize = new Size(520, 362);
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            string variant = UpdateService.GetCurrentPackageVariant();

            currentVerLabel.Location = new Point(16, 16);
            currentVerLabel.Size = new Size(368, 20);
            currentVerLabel.Text = Tr("update_current_version", AppVersion, variant);

            latestVerLabel.Location = new Point(16, 40);
            latestVerLabel.Size = new Size(368, 20);
            latestVerLabel.Text = Tr("update_latest_version", "—");

            recheckOrCancelButton.Location = new Point(394, 14);
            recheckOrCancelButton.Size = new Size(110, 28);
            recheckOrCancelButton.Text = Tr("update_btn_recheck");
            recheckOrCancelButton.Click += (_, _) => OnRecheckOrCancelClicked();

            statusLabel.Location = new Point(16, 66);
            statusLabel.Size = new Size(488, 36);
            statusLabel.Text = Tr("update_checking");

            notesHeaderLabel.Location = new Point(16, 108);
            notesHeaderLabel.Size = new Size(300, 18);
            notesHeaderLabel.Text = Tr("update_notes_label");

            notesBox.Location = new Point(16, 128);
            notesBox.Size = new Size(488, 136);
            notesBox.Multiline = true;
            notesBox.ReadOnly = true;
            notesBox.ScrollBars = ScrollBars.Vertical;
            notesBox.WordWrap = true;
            notesBox.BackColor = SystemColors.Window;

            progressBar.Location = new Point(16, 272);
            progressBar.Size = new Size(488, 18);
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Visible = false;

            progressLabel.Location = new Point(16, 293);
            progressLabel.Size = new Size(488, 18);
            progressLabel.ForeColor = SystemColors.GrayText;
            progressLabel.Visible = false;

            installButton.Location = new Point(16, 320);
            installButton.Size = new Size(148, 28);
            installButton.Text = Tr("update_btn_install");
            installButton.Enabled = false;
            installButton.Click += async (_, _) => await StartDownloadAndInstallAsync();

            openPageButton.Location = new Point(292, 320);
            openPageButton.Size = new Size(106, 28);
            openPageButton.Text = Tr("update_btn_open_page");
            openPageButton.Click += (_, _) => OpenReleasePage();

            closeButton.Location = new Point(406, 320);
            closeButton.Size = new Size(98, 28);
            closeButton.Text = Tr("update_btn_close");
            closeButton.Click += (_, _) => Close();

            Controls.Add(currentVerLabel);
            Controls.Add(latestVerLabel);
            Controls.Add(recheckOrCancelButton);
            Controls.Add(statusLabel);
            Controls.Add(notesHeaderLabel);
            Controls.Add(notesBox);
            Controls.Add(progressBar);
            Controls.Add(progressLabel);
            Controls.Add(installButton);
            Controls.Add(openPageButton);
            Controls.Add(closeButton);

            Shown += async (_, _) =>
            {
                if (currentResult != null && currentResult.Status == UpdateCheckStatus.UpdateAvailable)
                    RenderCheckResult(currentResult);
                else
                    await RunCheckAsync();
            };

            FormClosing += (_, _) =>
            {
                checkCts?.Cancel();
                downloadCts?.Cancel();
            };
        }

        internal void ApplyPrefetchedIfIdle(UpdateCheckResult result)
        {
            if (isCheckingUpdate || isDownloading)
                return;
            currentResult = result;
            RenderCheckResult(result);
        }

        private void OnRecheckOrCancelClicked()
        {
            if (isDownloading)
            {
                downloadCts?.Cancel();
                recheckOrCancelButton.Enabled = false;
                return;
            }
            if (!isCheckingUpdate)
            {
                _ = RunCheckAsync();
            }
        }

        private async Task RunCheckAsync()
        {
            if (isCheckingUpdate || isDownloading)
                return;

            isCheckingUpdate = true;
            checkCts?.Dispose();
            checkCts = new CancellationTokenSource();

            recheckOrCancelButton.Enabled = false;
            installButton.Enabled = false;
            progressBar.Visible = false;
            progressLabel.Visible = false;
            statusLabel.ForeColor = SystemColors.ControlText;
            statusLabel.Text = Tr("update_checking");

            try
            {
                var result = await UpdateService.CheckForUpdateAsync(checkCts.Token);
                if (IsDisposed) return;

                currentResult = result;
                TrayUI.SetCachedAvailableUpdate(result);
                RenderCheckResult(result);
            }
            finally
            {
                if (!IsDisposed)
                {
                    isCheckingUpdate = false;
                    recheckOrCancelButton.Enabled = true;
                }
            }
        }

        private void RenderCheckResult(UpdateCheckResult result)
        {
            latestVerLabel.Text = string.IsNullOrEmpty(result.LatestVersion)
                ? Tr("update_latest_version", "—")
                : Tr("update_latest_version", $"v{result.LatestVersion}");

            string normalizedNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? Tr("update_notes_empty")
                : result.ReleaseNotes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim();
            notesBox.Text = normalizedNotes;
            notesBox.SelectionStart = 0;
            notesBox.SelectionLength = 0;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    statusLabel.ForeColor = ColorSuccess;
                    statusLabel.Text = Tr("update_up_to_date");
                    installButton.Enabled = false;
                    break;

                case UpdateCheckStatus.UpdateAvailable:
                    bool canAutoInstall = !string.IsNullOrWhiteSpace(result.DownloadUrl);
                    statusLabel.ForeColor = ColorHighlight;
                    statusLabel.Text = canAutoInstall
                        ? Tr("update_available", result.LatestVersion, result.AssetName ?? result.PackageVariant)
                        : Tr("update_available_no_asset", result.LatestVersion);
                    installButton.Enabled = canAutoInstall;
                    break;

                case UpdateCheckStatus.Failed:
                default:
                    statusLabel.ForeColor = ColorError;
                    statusLabel.Text = Tr("update_failed", result.ErrorMessage ?? "Unknown error");
                    installButton.Enabled = false;
                    break;
            }
        }

        private async Task StartDownloadAndInstallAsync()
        {
            if (currentResult == null || string.IsNullOrWhiteSpace(currentResult.DownloadUrl) || isDownloading)
                return;

            isDownloading = true;
            downloadCts?.Dispose();
            downloadCts = new CancellationTokenSource();

            installButton.Enabled = false;
            closeButton.Enabled = false;
            recheckOrCancelButton.Text = Tr("update_btn_cancel");
            recheckOrCancelButton.Enabled = true;

            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;
            progressBar.Visible = true;
            progressLabel.Text = "";
            progressLabel.Visible = true;

            statusLabel.ForeColor = SystemColors.ControlText;
            statusLabel.Text = Tr("update_downloading", currentResult.AssetName ?? "update.zip", "0%");

            var progress = new Progress<UpdateDownloadProgress>(OnDownloadProgress);

            try
            {
                await UpdateService.DownloadAndApplyUpdateAsync(currentResult, progress, downloadCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed)
                {
                    statusLabel.ForeColor = SystemColors.ControlText;
                    statusLabel.Text = Tr("update_cancelled");
                    ResetButtonsAfterDownload(canRetryInstall: true);
                }
            }
            catch (Exception ex)
            {
                LogError("Update", $"Failed to download/apply update: {Describe(ex)}");
                if (!IsDisposed)
                {
                    statusLabel.ForeColor = ColorError;
                    statusLabel.Text = Tr("update_apply_failed", ex.Message);
                    ResetButtonsAfterDownload(canRetryInstall: true);
                }
            }
        }

        private void OnDownloadProgress(UpdateDownloadProgress p)
        {
            if (IsDisposed || !isDownloading)
                return;

            string assetLabel = currentResult?.AssetName ?? "update.zip";
            if (p.TotalBytes > 0)
            {
                int pct = p.Percentage;
                if (p.BytesReceived >= p.TotalBytes)
                {
                    progressBar.Style = ProgressBarStyle.Marquee;
                    statusLabel.Text = Tr("update_applying");
                    progressLabel.Text = $"{FormatBytes(p.BytesReceived)} / {FormatBytes(p.TotalBytes)} (100%)";
                }
                else
                {
                    if (progressBar.Style != ProgressBarStyle.Blocks)
                        progressBar.Style = ProgressBarStyle.Blocks;
                    progressBar.Value = Math.Clamp(pct, 0, 100);
                    string detail = $"{FormatBytes(p.BytesReceived)} / {FormatBytes(p.TotalBytes)} ({pct}%)";
                    statusLabel.Text = Tr("update_downloading", assetLabel, $"{pct}%");
                    progressLabel.Text = detail;
                }
            }
            else
            {
                if (progressBar.Style != ProgressBarStyle.Marquee)
                    progressBar.Style = ProgressBarStyle.Marquee;
                string detail = FormatBytes(p.BytesReceived);
                statusLabel.Text = Tr("update_downloading", assetLabel, detail);
                progressLabel.Text = detail;
            }
        }

        private void ResetButtonsAfterDownload(bool canRetryInstall)
        {
            isDownloading = false;
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Visible = false;
            progressLabel.Visible = false;
            recheckOrCancelButton.Text = Tr("update_btn_recheck");
            recheckOrCancelButton.Enabled = true;
            closeButton.Enabled = true;
            installButton.Enabled = canRetryInstall && !string.IsNullOrWhiteSpace(currentResult?.DownloadUrl);
        }

        private void OpenReleasePage()
        {
            string url = currentResult?.HtmlUrl ?? UpdateService.ReleasesPageUrl;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = UpdateService.ReleasesPageUrl;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogError("Update", $"Failed to open release page: {Describe(ex)}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}
