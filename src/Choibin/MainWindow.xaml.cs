using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Choibin.Core;
using Choibin.Models;
using Choibin.Net;

namespace Choibin
{
    public class StagedFile
    {
        public string Path { get; set; }
        public string Display { get; set; }

        public override string ToString()
        {
            return Display;
        }
    }

    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly ObservableCollection<Peer> _peers = new ObservableCollection<Peer>();
        private readonly ObservableCollection<StagedFile> _staged = new ObservableCollection<StagedFile>();
        private readonly ObservableCollection<TransferItem> _transfers = new ObservableCollection<TransferItem>();

        private PeerDiscovery _discovery;
        private DropServer _dropServer;
        private WebServer _webServer;
        private DispatcherTimer _pruneTimer;
        private string _phoneUrl;

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();

            PeerList.ItemsSource = _peers;
            StagedList.ItemsSource = _staged;
            TransferList.ItemsSource = _transfers;

            DeviceNameBox.Text = _settings.DeviceName;
            AutoAcceptCheck.IsChecked = _settings.AutoAccept;
            PhoneAccessCheck.IsChecked = _settings.PhoneAccessEnabled;
            HttpsCheck.IsChecked = _settings.PhoneUseHttps;
            VerifyCodeCheck.IsChecked = _settings.VerifyCode;
            SaveFolderText.Text = _settings.SaveFolder;

            _transfers.CollectionChanged += (s, e) =>
            {
                EmptyHistoryText.Visibility = _transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartServices();

            _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _pruneTimer.Tick += PruneTimer_Tick;
            _pruneTimer.Start();

            UpdatePeerCount();
        }

        // ---------- サービスの起動 / 停止 ----------

        private void StartServices()
        {
            try
            {
                _dropServer = new DropServer(_settings);
                _dropServer.AcceptRequested = OnAcceptRequested;
                _dropServer.TransferAdded = item => Dispatcher.Invoke(() => _transfers.Insert(0, item));
                _dropServer.TransferFinished = item => { };
                _dropServer.ErrorOccurred = msg => { };
                _dropServer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "受信用のポート " + AppSettings.TransferPort + " を開けませんでした。\n\n" + ex.Message,
                    "ちょい便", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            try
            {
                _discovery = new PeerDiscovery(_settings);
                _discovery.PeerSeen += OnPeerSeen;
                _discovery.PeerGone += OnPeerGone;
                _discovery.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "デバイスの検出を開始できませんでした。\n\n" + ex.Message,
                    "ちょい便", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _webServer = new WebServer(_settings);
            _webServer.TransferAdded = item => Dispatcher.Invoke(() => _transfers.Insert(0, item));
            _webServer.TransferFinished = item => { };
            _webServer.ErrorOccurred = msg => { };

            ApplyPhoneAccess();
        }

        private void ApplyPhoneAccess()
        {
            if (_settings.PhoneAccessEnabled)
            {
                try
                {
                    _webServer.Start();
                    string scheme = _webServer.IsSecure ? "https" : "http";
                    _phoneUrl = scheme + "://" + NetUtil.GetLocalAddress() + ":" +
                                AppSettings.WebPort + "/?k=" + _webServer.AccessCode;
                    UrlText.Text = _phoneUrl;
                    ShareStatusText.Text = _webServer.IsSecure
                        ? "受付中です。通信はTLSで暗号化されます。"
                        : "受付中です。通信は暗号化されていません。";
                    QrImage.Source = RenderQr(QrEncoder.Encode(_phoneUrl), 5, 3);
                }
                catch (Exception ex)
                {
                    UrlText.Text = "起動できませんでした";
                    ShareStatusText.Text = ex.Message;
                    QrImage.Source = null;
                }
            }
            else
            {
                _webServer.Stop();
                _phoneUrl = null;
                UrlText.Text = "停止中";
                ShareStatusText.Text = "チェックを入れると受け付けます。";
                QrImage.Source = null;
            }
        }

        // ---------- デバイスの検出 ----------

        private void OnPeerSeen(Peer peer)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (Peer p in _peers)
                {
                    if (p.Id == peer.Id)
                    {
                        p.Name = peer.Name;
                        p.Address = peer.Address;
                        p.Port = peer.Port;
                        p.LastSeen = peer.LastSeen;
                        return;
                    }
                }
                _peers.Add(peer);
                UpdatePeerCount();
            });
        }

        private void OnPeerGone(string id)
        {
            Dispatcher.Invoke(() =>
            {
                for (int i = _peers.Count - 1; i >= 0; i--)
                {
                    if (_peers[i].Id == id) _peers.RemoveAt(i);
                }
                UpdatePeerCount();
            });
        }

        private void PruneTimer_Tick(object sender, EventArgs e)
        {
            DateTime limit = DateTime.Now.AddSeconds(-16);
            bool changed = false;
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                if (_peers[i].LastSeen < limit)
                {
                    _peers.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed) UpdatePeerCount();
        }

        private void UpdatePeerCount()
        {
            PeerCountText.Text = _peers.Count == 0
                ? "まだ見つかっていません。相手のPCでも「ちょい便」を開いてください。"
                : _peers.Count + " 台が見つかりました。";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _peers.Clear();
            UpdatePeerCount();
            if (_discovery != null) _discovery.Broadcast(true);
        }

        // ---------- 受け取りの確認 ----------

        private bool OnAcceptRequested(string sender, List<FileEntry> files, string code)
        {
            return Dispatcher.Invoke(() =>
            {
                long total = 0;
                foreach (FileEntry f in files) total += f.Size;

                string list = string.Empty;
                int shown = 0;
                foreach (FileEntry f in files)
                {
                    if (shown >= 5) { list += "\n… ほか " + (files.Count - shown) + " 件"; break; }
                    list += "\n・" + f.Name + "（" + NetUtil.FormatBytes(f.Size) + "）";
                    shown++;
                }

                MessageBoxResult r = MessageBox.Show(this,
                    sender + " から " + files.Count + " 件（合計 " + NetUtil.FormatBytes(total) + "）が届いています。受け取りますか？"
                    + list + "\n\n保存先: " + _settings.SaveFolder
                    + "\n\n通信は暗号化されています（" + SecureChannel.AlgorithmName + "）。"
                    + "\n確認コード: " + code
                    + "\n送信側の画面と同じ数字なら、間に第三者はいません。",
                    "ちょい便 — 受信の確認", MessageBoxButton.YesNo, MessageBoxImage.Question);

                return r == MessageBoxResult.Yes;
            });
        }

        // ---------- ファイルの受け渡し ----------

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok)
            {
                DropRect.Stroke = (Brush)FindResource("AccentBrush");
                DropText.Text = "ここに離してください";
            }
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZone();
        }

        private void ResetDropZone()
        {
            DropRect.Stroke = (Brush)FindResource("EdgeBrush");
            DropText.Text = "ファイルをドロップ";
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            ResetDropZone();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddFiles(paths);
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "送るファイルを選ぶ"
            };
            if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(p)) AddSingle(f);
                    }
                    catch { }
                    continue;
                }
                AddSingle(p);
            }
        }

        private void AddSingle(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            foreach (StagedFile s in _staged)
                if (string.Equals(s.Path, info.FullName, StringComparison.OrdinalIgnoreCase)) return;

            _staged.Add(new StagedFile
            {
                Path = info.FullName,
                Display = info.Name + "  —  " + NetUtil.FormatBytes(info.Length)
            });
        }

        private void ClearStaged_Click(object sender, RoutedEventArgs e)
        {
            _staged.Clear();
            if (_webServer != null) _webServer.ClearOutbox();
            UpdateShareStatus();
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var peer = PeerList.SelectedItem as Peer;
            if (peer == null)
            {
                MessageBox.Show(this, "左のリストから送る相手を選んでください。", "ちょい便",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_staged.Count == 0)
            {
                MessageBox.Show(this, "送るファイルがありません。", "ちょい便",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paths = new List<string>();
            foreach (StagedFile s in _staged) paths.Add(s.Path);

            SendButton.IsEnabled = false;
            try
            {
                await DropClient.SendAsync(peer, paths, _settings.DeviceName,
                    path =>
                    {
                        var info = new FileInfo(path);
                        var item = new TransferItem
                        {
                            IsIncoming = false,
                            PeerName = peer.Name,
                            FileName = info.Name,
                            TotalBytes = info.Length,
                            State = TransferState.Waiting,
                            StartedAt = DateTime.Now
                        };
                        Dispatcher.Invoke(() => _transfers.Insert(0, item));
                        return item;
                    },
                    ShowSendError,
                    ConfirmSecurityCode);
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        /// <summary>設定がオンのとき、送信前に確認コードを見比べてもらいます。</summary>
        private bool ConfirmSecurityCode(Peer peer, string code)
        {
            if (!_settings.VerifyCode) return true;

            return Dispatcher.Invoke(() =>
            {
                MessageBoxResult r = MessageBox.Show(this,
                    peer.Name + " と鍵を交換しました。\n\n確認コード: " + code
                    + "\n\n相手の画面にも同じ数字が出ていますか？\n"
                    + "違う数字が出ている場合は、間に第三者がいる可能性があります。",
                    "ちょい便 — 送信前の確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return r == MessageBoxResult.Yes;
            });
        }

        private void ShowSendError(string message)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                MessageBox.Show(this, "送信できませんでした。\n\n" + message, "ちょい便",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }));
        }

        private void Share_Click(object sender, RoutedEventArgs e)
        {
            if (_webServer == null || !_settings.PhoneAccessEnabled)
            {
                MessageBox.Show(this, "先に「スマホからの接続を受け付ける」を有効にしてください。", "ちょい便",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_staged.Count == 0)
            {
                MessageBox.Show(this, "公開するファイルがありません。", "ちょい便",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paths = new List<string>();
            foreach (StagedFile s in _staged) paths.Add(s.Path);
            _webServer.SetOutbox(paths);
            UpdateShareStatus();
        }

        private void UpdateShareStatus()
        {
            if (_webServer == null || !_settings.PhoneAccessEnabled) return;
            int n = _webServer.OutboxCount;
            ShareStatusText.Text = n == 0 ? "受付中です。" : n + " 件を公開中です。";
        }

        // ---------- 設定 ----------

        private void DeviceNameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string name = DeviceNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = Environment.MachineName;
            if (name.Length > 40) name = name.Substring(0, 40);

            DeviceNameBox.Text = name;
            _settings.DeviceName = name;
            _settings.Save();

            if (_discovery != null) _discovery.Broadcast(false);
        }

        private void AutoAccept_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.AutoAccept = AutoAcceptCheck.IsChecked == true;
            _settings.Save();
        }

        private void Https_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _webServer == null) return;
            _settings.PhoneUseHttps = HttpsCheck.IsChecked == true;
            _settings.Save();

            // 待ち受け方を変えるので、いったん止めてから作り直します。
            _webServer.Stop();
            ApplyPhoneAccess();
        }

        private void VerifyCode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.VerifyCode = VerifyCodeCheck.IsChecked == true;
            _settings.Save();
        }

        private void PhoneAccess_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _webServer == null) return;
            _settings.PhoneAccessEnabled = PhoneAccessCheck.IsChecked == true;
            _settings.Save();
            ApplyPhoneAccess();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _settings.SaveFolder + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ちょい便", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "受信フォルダを選ぶ",
                InitialDirectory = _settings.SaveFolder
            };
            if (dialog.ShowDialog(this) == true)
            {
                _settings.SaveFolder = dialog.FolderName;
                _settings.Save();
                SaveFolderText.Text = _settings.SaveFolder;
            }
        }

        // ---------- QRコードの描画 ----------

        private static BitmapSource RenderQr(bool[,] modules, int scale, int quiet)
        {
            int n = modules.GetLength(0);
            int side = (n + quiet * 2) * scale;
            var pixels = new byte[side * side * 4];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }

            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    if (!modules[r, c]) continue;
                    int x0 = (c + quiet) * scale;
                    int y0 = (r + quiet) * scale;
                    for (int y = y0; y < y0 + scale; y++)
                    {
                        int rowStart = y * side * 4;
                        for (int x = x0; x < x0 + scale; x++)
                        {
                            int idx = rowStart + x * 4;
                            pixels[idx] = 0x1F;      // B
                            pixels[idx + 1] = 0x14;  // G
                            pixels[idx + 2] = 0x17;  // R
                            pixels[idx + 3] = 255;   // A
                        }
                    }
                }
            }

            var bitmap = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, side, side), pixels, side * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        // ---------- 終了処理 ----------

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_pruneTimer != null) _pruneTimer.Stop();
            if (_discovery != null) _discovery.Dispose();
            if (_dropServer != null) _dropServer.Dispose();
            if (_webServer != null) _webServer.Dispose();
            _settings.Save();
        }
    }
}
