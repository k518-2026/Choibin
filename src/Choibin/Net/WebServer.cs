using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Choibin.Core;
using Choibin.Models;

namespace Choibin.Net
{
    /// <summary>
    /// スマホのブラウザーからファイルをやり取りするための小さなHTTPサーバー。
    /// HttpListenerを使わずTcpListenerで実装しているため、管理者権限もURL予約も不要です。
    ///
    /// 保護は2段構えです。
    ///   ・通信の暗号化: 自己署名証明書によるTLS（設定でHTTPに戻せます）
    ///   ・アクセス制限: 起動ごとに作る8文字のアクセスキー。QRコードに埋め込んであるので、
    ///     読み取った端末だけがページを開けます。LAN上の他人がURLを推測しても入れません。
    /// </summary>
    public class WebServer : IDisposable
    {
        private readonly AppSettings _settings;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private string _pageTemplate;
        private X509Certificate2 _certificate;

        private readonly object _lock = new object();
        private readonly List<SharedFile> _outbox = new List<SharedFile>();

        public Action<TransferItem> TransferAdded;
        public Action<TransferItem> TransferFinished;
        public Action<string> ErrorOccurred;

        public bool IsRunning { get; private set; }

        /// <summary>この起動でのアクセスキー。URLとQRコードに埋め込みます。</summary>
        public string AccessCode { get; private set; }

        /// <summary>TLSで待ち受けているか。</summary>
        public bool IsSecure { get; private set; }

        public WebServer(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            if (IsRunning) return;
            _pageTemplate = LoadPage();
            AccessCode = NewAccessCode();

            _certificate = null;
            IsSecure = false;
            if (_settings.PhoneUseHttps)
            {
                _certificate = PhoneCertificate.GetOrCreate(NetUtil.GetLocalAddresses());
                IsSecure = true;
            }

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, AppSettings.WebPort);
            _listener.Start();
            IsRunning = true;
            Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            _certificate = null;
        }

        private static string NewAccessCode()
        {
            // 紛らわしい文字（0/O/1/l）を避けた8文字。
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
            var bytes = new byte[8];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(8);
            foreach (byte b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        public void SetOutbox(IEnumerable<string> paths)
        {
            lock (_lock)
            {
                _outbox.Clear();
                foreach (string p in paths)
                {
                    var info = new FileInfo(p);
                    if (!info.Exists) continue;
                    _outbox.Add(new SharedFile
                    {
                        Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                        Path = info.FullName,
                        Name = info.Name,
                        Size = info.Length
                    });
                }
            }
        }

        public void ClearOutbox()
        {
            lock (_lock) { _outbox.Clear(); }
        }

        public int OutboxCount
        {
            get { lock (_lock) { return _outbox.Count; } }
        }

        /// <summary>アイコンなどの埋め込みリソースをバイト列で取り出します。</summary>
        private static byte[] LoadAsset(string fileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string candidate in asm.GetManifestResourceNames())
            {
                if (!candidate.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream s = asm.GetManifestResourceStream(candidate))
                {
                    if (s == null) break;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            return new byte[0];
        }

        private static void SendBinary(Stream stream, string contentType, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                SendStatus(stream, 404, "Not Found");
                return;
            }

            var head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            head.Append("Cache-Control: max-age=86400\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static string LoadPage()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string resourceName = null;
            foreach (string candidate in asm.GetManifestResourceNames())
            {
                if (candidate.EndsWith("phone.html", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = candidate;
                    break;
                }
            }
            if (resourceName == null)
                return "<html><body>ページを読み込めませんでした。</body></html>";

            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return "<html><body>ページを読み込めませんでした。</body></html>";
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch { continue; }

                TcpClient captured = client;
                var _ = Task.Run(() => Handle(captured));
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream network = client.GetStream())
                {
                    client.ReceiveTimeout = 300000;
                    client.SendTimeout = 300000;

                    Stream stream = network;
                    SslStream ssl = null;
                    if (_certificate != null)
                    {
                        ssl = new SslStream(network, true);
                        ssl.AuthenticateAsServer(_certificate, false,
                            SslProtocols.Tls12 | SslProtocols.Tls13, false);
                        stream = ssl;
                    }

                    try
                    {
                    string requestLine;
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!ReadHead(stream, out requestLine, headers)) return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0].ToUpperInvariant();
                    string rawUrl = parts[1];

                    string path = rawUrl;
                    string query = string.Empty;
                    int q = rawUrl.IndexOf('?');
                    if (q >= 0)
                    {
                        path = rawUrl.Substring(0, q);
                        query = rawUrl.Substring(q + 1);
                    }

                    string expect;
                    if (headers.TryGetValue("Expect", out expect) &&
                        expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        stream.Write(cont, 0, cont.Length);
                        stream.Flush();
                    }

                    if (method == "GET" && path == "/favicon.ico")
                    {
                        SendBinary(stream, "image/x-icon", LoadAsset("app.ico"));
                        return;
                    }
                    if (method == "GET" && path == "/touch-icon.png")
                    {
                        SendBinary(stream, "image/png", LoadAsset("touch-icon.png"));
                        return;
                    }

                    string cookie;
                    headers.TryGetValue("Cookie", out cookie);
                    bool authorized = IsAuthorized(query, cookie);

                    if (!authorized)
                    {
                        SendText(stream, 403, "text/html; charset=utf-8", DeniedPage());
                    }
                    else if (method == "GET" && (path == "/" || path == "/index.html"))
                    {
                        SendText(stream, 200, "text/html; charset=utf-8", BuildPage(),
                            "Set-Cookie: wdkey=" + AccessCode + "; Path=/; SameSite=Lax");
                    }
                    else if (method == "GET" && path == "/api/files")
                    {
                        SendText(stream, 200, "application/json; charset=utf-8", BuildFileListJson());
                    }
                    else if (method == "GET" && path.StartsWith("/d/", StringComparison.Ordinal))
                    {
                        SendSharedFile(stream, path.Substring(3));
                    }
                    else if (method == "POST" && path == "/u")
                    {
                        ReceiveUpload(stream, headers, query);
                    }
                    else
                    {
                        SendText(stream, 404, "text/plain; charset=utf-8", "見つかりません");
                    }
                    }
                    finally
                    {
                        if (ssl != null) ssl.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Action<string> err = ErrorOccurred;
                if (err != null) err(ex.Message);
            }
        }

        private bool IsAuthorized(string query, string cookieHeader)
        {
            if (string.IsNullOrEmpty(AccessCode)) return true;

            string fromQuery = GetQueryValue(query, "k");
            if (FixedEquals(fromQuery, AccessCode)) return true;

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                foreach (string part in cookieHeader.Split(';'))
                {
                    string trimmed = part.Trim();
                    if (!trimmed.StartsWith("wdkey=", StringComparison.Ordinal)) continue;
                    if (FixedEquals(trimmed.Substring(6), AccessCode)) return true;
                }
            }
            return false;
        }

        /// <summary>総当たりの手がかりを与えないよう、長さに依存しない比較をします。</summary>
        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            return CryptographicOperations.FixedTimeEquals(x, y);
        }

        private static string DeniedPage()
        {
            return "<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\">" +
                   "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                   "<title>Choibin</title></head>" +
                   "<body style=\"font-family:system-ui,sans-serif;padding:40px 24px;" +
                   "text-align:center;color:#1b2430\">" +
                   "<h1 style=\"font-size:20px\">アクセスキーが必要です</h1>" +
                   "<p style=\"color:#5b6675;line-height:1.7\">PCの画面に出ているQRコードを" +
                   "読み取ってください。<br>キーはChoibinを起動するたびに変わります。</p>" +
                   "</body></html>";
        }

        private string BuildPage()
        {
            return _pageTemplate.Replace("{{DEVICE_NAME}}", HtmlEscape(_settings.DeviceName));
        }

        private string BuildFileListJson()
        {
            List<SharedFile> copy;
            lock (_lock) { copy = new List<SharedFile>(_outbox); }

            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < copy.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"id\":").Append(JsonSerializer.Serialize(copy[i].Id)).Append(',');
                sb.Append("\"name\":").Append(JsonSerializer.Serialize(copy[i].Name)).Append(',');
                sb.Append("\"size\":").Append(copy[i].Size).Append(',');
                sb.Append("\"sizeText\":").Append(JsonSerializer.Serialize(NetUtil.FormatBytes(copy[i].Size)));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void SendSharedFile(Stream stream, string id)
        {
            SharedFile target = null;
            lock (_lock)
            {
                foreach (SharedFile f in _outbox)
                    if (f.Id == id) { target = f; break; }
            }

            if (target == null || !File.Exists(target.Path))
            {
                SendText(stream, 404, "text/plain; charset=utf-8", "ファイルがありません");
                return;
            }

            var info = new FileInfo(target.Path);
            string encoded = Uri.EscapeDataString(target.Name);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: ").Append(NetUtil.GuessContentType(target.Name)).Append("\r\n");
            head.Append("Content-Length: ").Append(info.Length).Append("\r\n");
            head.Append("Content-Disposition: attachment; filename*=UTF-8''").Append(encoded).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);

            using (var fs = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    stream.Write(buffer, 0, read);
            }
            stream.Flush();
        }

        private void ReceiveUpload(Stream stream, Dictionary<string, string> headers, string query)
        {
            string name = GetQueryValue(query, "name");
            string safeName = NetUtil.SanitizeFileName(name);

            string lenText;
            long length = 0;
            if (headers.TryGetValue("Content-Length", out lenText)) long.TryParse(lenText, out length);
            if (length <= 0)
            {
                SendText(stream, 411, "text/plain; charset=utf-8", "サイズが不明です");
                return;
            }

            Directory.CreateDirectory(_settings.SaveFolder);
            string target = NetUtil.UniquePath(_settings.SaveFolder, safeName);

            var item = new TransferItem
            {
                IsIncoming = true,
                PeerName = "スマホ",
                FileName = safeName,
                TotalBytes = length,
                State = TransferState.Running,
                StartedAt = DateTime.Now,
                SavedPath = target
            };
            Action<TransferItem> added = TransferAdded;
            if (added != null) added(item);

            try
            {
                using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    var buffer = new byte[81920];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int read = stream.Read(buffer, 0, want);
                        if (read <= 0) throw new IOException("接続が切断されました。");
                        fs.Write(buffer, 0, read);
                        remaining -= read;
                        item.DoneBytes = length - remaining;
                    }
                }
                item.State = TransferState.Done;
                item.Detail = target;
                SendText(stream, 200, "application/json; charset=utf-8", "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                item.State = TransferState.Failed;
                item.Detail = ex.Message;
                try { SendText(stream, 500, "text/plain; charset=utf-8", "受信に失敗しました"); }
                catch { }
            }
            finally
            {
                Action<TransferItem> fin = TransferFinished;
                if (fin != null) fin(item);
            }
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(pair.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", "%20")); }
                catch { return pair.Substring(eq + 1); }
            }
            return null;
        }

        private static bool ReadHead(Stream stream, out string requestLine, Dictionary<string, string> headers)
        {
            requestLine = null;
            var buffer = new List<byte>(2048);
            int b;
            int matched = 0;
            while ((b = stream.ReadByte()) >= 0)
            {
                buffer.Add((byte)b);
                if (b == '\r' && (matched == 0 || matched == 2)) matched++;
                else if (b == '\n' && (matched == 1 || matched == 3)) matched++;
                else matched = 0;
                if (matched == 4) break;
                if (buffer.Count > 64 * 1024) return false;
            }
            if (matched != 4) return false;

            string text = Encoding.UTF8.GetString(buffer.ToArray());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;
            requestLine = lines[0];

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) break;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                headers[key] = value;
            }
            return true;
        }

        private static void SendText(Stream stream, int status, string contentType, string body)
        {
            SendText(stream, status, contentType, body, null);
        }

        private static void SendText(Stream stream, int status, string contentType, string body,
                                     string extraHeader)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            if (!string.IsNullOrEmpty(extraHeader)) head.Append(extraHeader).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static void SendStatus(Stream stream, int status, string text)
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + " " + text + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Flush();
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 411: return "Length Required";
                default: return "Internal Server Error";
            }
        }

        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
