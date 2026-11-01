using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using PCL2Unlock.Core;

namespace PCL2Unlock
{
    public partial class MainWindow : Window, ILog
    {
        private readonly AppSettings _settings = AppSettings.Load();
        private PatchAnalysis _analysis;
        private bool _busy;
        private bool _initializing = true;
        private string _lastSignature;

        public MainWindow()
        {
            InitializeComponent();

            var detected = Cli.AutoDetect() ?? _settings.LastPath;
            if (!string.IsNullOrEmpty(detected)) TargetBox.Text = detected;

            Loaded += async (s, e) =>
            {
                await RefreshAsync();
                _initializing = false;
            };
        }

        public void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(message)));
                return;
            }
            LogText.Text += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            LogScroll.ScrollToEnd();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var a = _analysis;
            bool hasTarget = a != null && a.IsSupported;
            ApplyBtn.IsEnabled = !_busy && hasTarget && a.Compatible && !a.AllPatched && !a.Locked;
            RevertBtn.IsEnabled = !_busy && hasTarget && File.Exists(Pcl2Patcher.BackupPath(a.Path));
            OpenFolderBtn.IsEnabled = !_busy && hasTarget;
            RefreshBtn.IsEnabled = !_busy;
        }

        private async Task RefreshAsync()
        {
            var path = (TargetBox.Text ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(path))
            {
                _analysis = null;
                Render();
                return;
            }

            SetBusy(true);
            var analysis = await Task.Run(() => Pcl2Patcher.Analyze(path));
            _analysis = analysis;
            if (analysis.IsSupported && !string.IsNullOrEmpty(path)) _settings.LastPath = path;
            _settings.Save();
            Render();
            SetBusy(false);
            LogSites(analysis);
        }

        private void LogSites(PatchAnalysis a)
        {
            if (a == null || !a.IsSupported) return;
            var sig = a.Path + "|" + (a.AllPatched ? "1" : "0") + "|" + a.Locked + "|" +
                      string.Join(",", a.Sites.ConvertAll(s => s.Id + ":" + (int)s.State));
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            Log("目标：" + a.Path);
            Log("程序集：" + a.AssemblyName + " " + a.Version + "，共 " + a.UsableSites + " 个补丁点");
            foreach (var s in a.Sites)
            {
                string state;
                switch (s.State)
                {
                    case SiteState.Patched: state = "已修补"; break;
                    case SiteState.Missing: state = "不适用（" + s.Detail + "）"; break;
                    default: state = "未修补" + (string.IsNullOrEmpty(s.Detail) ? "" : "（" + s.Detail + "）"); break;
                }
                Log(string.Format("  [{0}] {1}：{2}", s.Id, s.Title, state));
            }
            if (a.AllPatched) Log("状态：下载 / 安装限制已解除，可以直接启动 PCL。");
            else if (a.Locked) Log("警告：PCL 正在运行，请先退出后再应用补丁。");
        }

        private void Render()
        {
            var a = _analysis;
            if (a == null)
            {
                Badge.Background = Brush("#EEF1F6");
                BadgeText.Foreground = Brush("#4B5563");
                BadgeText.Text = "未选择文件";
                BadgeSub.Text = "选择 PCL 主程序后即可检测";
                VersionText.Text = "-"; SizeText.Text = "-"; BackupText.Text = "-"; HashText.Text = "-";
                UpdateButtons();
                return;
            }

            if (!a.IsSupported)
            {
                Badge.Background = Brush("#FDECEC");
                BadgeText.Foreground = Brush("#B91C1C");
                BadgeText.Text = "不是原版 PCL";
                BadgeSub.Text = a.Error ?? "程序集不匹配";
                VersionText.Text = "-"; SizeText.Text = "-";
            }
            else if (a.AllPatched)
            {
                Badge.Background = Brush("#E8F7EF");
                BadgeText.Foreground = Brush("#15803D");
                BadgeText.Text = "已打补丁";
                BadgeSub.Text = a.Locked ? "限制已解除 · PCL 正在运行" : "限制已解除";
                VersionText.Text = a.Version ?? "-";
                SizeText.Text = (a.FileSize / 1024.0 / 1024.0).ToString("0.0") + " MB";
            }
            else if (a.UsableSites == 0)
            {
                Badge.Background = Brush("#FFF5E6");
                BadgeText.Foreground = Brush("#B45309");
                BadgeText.Text = "无可用补丁点";
                BadgeSub.Text = "版本可能不受支持";
                VersionText.Text = a.Version ?? "-";
                SizeText.Text = (a.FileSize / 1024.0 / 1024.0).ToString("0.0") + " MB";
            }
            else if (a.Locked)
            {
                Badge.Background = Brush("#FFF5E6");
                BadgeText.Foreground = Brush("#B45309");
                BadgeText.Text = "PCL 正在运行";
                BadgeSub.Text = "请先退出 PCL 再应用补丁";
                VersionText.Text = a.Version ?? "-";
                SizeText.Text = (a.FileSize / 1024.0 / 1024.0).ToString("0.0") + " MB";
            }
            else
            {
                Badge.Background = Brush("#FFF5E6");
                BadgeText.Foreground = Brush("#B45309");
                BadgeText.Text = "未打补丁";
                BadgeSub.Text = "点击「应用补丁」解除限制";
                VersionText.Text = a.Version ?? "-";
                SizeText.Text = (a.FileSize / 1024.0 / 1024.0).ToString("0.0") + " MB";
            }

            BackupText.Text = File.Exists(Pcl2Patcher.BackupPath(a.Path)) ? "已存在" : "无";
            HashText.Text = a.Sha256 ?? "-";
            UpdateButtons();
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private async void OnTargetChanged(object sender, TextChangedEventArgs e)
        {
            if (_initializing) return;
            var path = (TargetBox.Text ?? "").Trim().Trim('"');
            if (_analysis != null && string.Equals(path, _analysis.Path, StringComparison.OrdinalIgnoreCase)) return;
            await RefreshAsync();
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 PCL 主程序",
                Filter = "可执行文件|*.exe|所有文件|*.*",
                CheckFileExists = true
            };
            if (File.Exists(TargetBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(TargetBox.Text);
            if (!File.Exists(TargetBox.Text) && Directory.Exists(@"D:\minecraft")) dlg.InitialDirectory = @"D:\minecraft";
            if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FileName;
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            _lastSignature = null;
            await RefreshAsync();
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (_analysis == null) return;
            var answer = MessageBox.Show(this,
                "将向以下文件写入 IL 补丁：\n\n" + _analysis.Path + "\n\n原始文件会自动备份为 *.pcl2fix.bak，可随时还原。继续？",
                "确认应用补丁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            var path = _analysis.Path;
            SetBusy(true);
            var report = await Task.Run(() => Pcl2Patcher.Apply(path, Log));
            _lastSignature = null;
            await RefreshAsync();
            MessageBox.Show(this, report.Message, report.Success ? "完成" : "失败",
                MessageBoxButton.OK, report.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void OnRevert(object sender, RoutedEventArgs e)
        {
            if (_analysis == null) return;
            var backup = Pcl2Patcher.BackupPath(_analysis.Path);
            if (!File.Exists(backup)) return;
            var answer = MessageBox.Show(this, "将用备份还原：\n\n" + backup + "\n\n继续？", "确认还原",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            var path = _analysis.Path;
            SetBusy(true);
            var report = await Task.Run(() => Pcl2Patcher.Revert(path, Log));
            _lastSignature = null;
            await RefreshAsync();
            MessageBox.Show(this, report.Message, report.Success ? "完成" : "失败",
                MessageBoxButton.OK, report.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            if (_analysis == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + _analysis.Path + "\""); } catch { }
        }

        private void OnClearLog(object sender, RoutedEventArgs e) => LogText.Text = "";

        private void OnNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0) TargetBox.Text = files[0];
        }
    }

    public interface ILog
    {
        void Log(string message);
    }
}
