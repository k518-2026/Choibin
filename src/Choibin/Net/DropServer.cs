using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Choibin.Core;
using Choibin.Models;

namespace Choibin.Net
{
    public class FileEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    public class TransferHeader
    {
        [JsonPropertyName("sender")] public string Sender { get; set; }
        [JsonPropertyName("files")] public List<FileEntry> Files { get; set; }
    }

    /// <summary>
    /// 他のChoibinからの送信を受け付けるTCPサーバー。
    /// 接続直後にECDHで鍵を交換し、以降はAES-256-GCMで暗号化された通信になります。
    /// その内側の手順: 4バイトのヘッダー長 → JSONヘッダー → 1バイトの応答 → ファイル本体を連結して受信。
    /// </summary>
    public class DropServer : IDisposable
    {
        private readonly AppSettings _settings;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        /// <summary>受け入れ確認。UI側でダイアログを出して true / false を返します。</summary>
        public Func<string, List<FileEntry>, string, bool> AcceptRequested;

        public Action<TransferItem> TransferAdded;
        public Action<TransferItem> TransferFinished;
        public Action<string> ErrorOccurred;

        public DropServer(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, AppSettings.TransferPort);
            _listener.Start();
            Task.Run(() => AcceptLoop(_cts.Token));
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch { continue; }

                TcpClient captured = client;
                var _ = Task.Run(() => HandleClient(captured));
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream raw = client.GetStream())
                {
                    client.ReceiveTimeout = 120000;
                    client.SendTimeout = 120000;

                    using (SecureChannel stream = SecureChannel.Establish(raw, false))
                    {
                    string code = stream.VerificationCode;

                    byte[] lenBuf = new byte[4];
                    if (!ReadExact(stream, lenBuf, 4)) return;
                    int headerLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                    if (headerLen <= 0 || headerLen > 1024 * 1024) return;

                    byte[] headerBuf = new byte[headerLen];
                    if (!ReadExact(stream, headerBuf, headerLen)) return;

                    TransferHeader header = JsonSerializer.Deserialize<TransferHeader>(
                        Encoding.UTF8.GetString(headerBuf));
                    if (header == null || header.Files == null || header.Files.Count == 0) return;

                    string sender = string.IsNullOrWhiteSpace(header.Sender) ? "不明な相手" : header.Sender;

                    bool accept = _settings.AutoAccept;
                    if (!accept)
                    {
                        Func<string, List<FileEntry>, string, bool> ask = AcceptRequested;
                        accept = ask != null && ask(sender, header.Files, code);
                    }

                    stream.WriteByte(accept ? (byte)1 : (byte)0);
                    stream.Flush();
                    if (!accept) return;

                    Directory.CreateDirectory(_settings.SaveFolder);

                    foreach (FileEntry entry in header.Files)
                    {
                        string safeName = NetUtil.SanitizeFileName(entry.Name);
                        string target = NetUtil.UniquePath(_settings.SaveFolder, safeName);

                        var item = new TransferItem
                        {
                            IsIncoming = true,
                            PeerName = sender,
                            FileName = safeName,
                            TotalBytes = entry.Size,
                            State = TransferState.Running,
                            StartedAt = DateTime.Now,
                            SavedPath = target,
                            IsEncrypted = true,
                            SecurityCode = code
                        };
                        Action<TransferItem> added = TransferAdded;
                        if (added != null) added(item);

                        try
                        {
                            using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                            {
                                byte[] buffer = new byte[81920];
                                long remaining = entry.Size;
                                while (remaining > 0)
                                {
                                    int want = (int)Math.Min(buffer.Length, remaining);
                                    int read = stream.Read(buffer, 0, want);
                                    if (read <= 0) throw new IOException("接続が切断されました。");
                                    fs.Write(buffer, 0, read);
                                    remaining -= read;
                                    item.DoneBytes = entry.Size - remaining;
                                }
                            }
                            item.State = TransferState.Done;
                            item.Detail = target;
                        }
                        catch (Exception ex)
                        {
                            item.State = TransferState.Failed;
                            item.Detail = ex.Message;
                            throw;
                        }
                        finally
                        {
                            Action<TransferItem> fin = TransferFinished;
                            if (fin != null) fin(item);
                        }
                    }
                    }
                }
            }
            catch (Exception ex)
            {
                Action<string> err = ErrorOccurred;
                if (err != null) err(ex.Message);
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }
    }
}
