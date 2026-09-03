using System;
using System.Collections.Generic;
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
    public class Announce
    {
        [JsonPropertyName("app")] public string App { get; set; }
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("platform")] public string Platform { get; set; }
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("reply")] public bool Reply { get; set; }
        [JsonPropertyName("bye")] public bool Bye { get; set; }
    }

    /// <summary>
    /// UDPのブロードキャストとマルチキャストで、同じLAN上のChoibinを探します。
    /// </summary>
    public class PeerDiscovery : IDisposable
    {
        private readonly AppSettings _settings;
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly List<IPAddress> _broadcasts = new List<IPAddress>();
        private IPAddress _multicast;

        public string SelfId { get; private set; }

        public event Action<Peer> PeerSeen;
        public event Action<string> PeerGone;

        public PeerDiscovery(AppSettings settings)
        {
            _settings = settings;
            SelfId = Guid.NewGuid().ToString("N");
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _multicast = IPAddress.Parse(AppSettings.MulticastGroup);

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { _udp.ExclusiveAddressUse = false; }
            catch { /* 既定のまま使います */ }
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, AppSettings.DiscoveryPort));
            _udp.EnableBroadcast = true;

            try { _udp.JoinMulticastGroup(_multicast); }
            catch { /* マルチキャスト非対応の環境ではブロードキャストのみ使います */ }

            _broadcasts.Clear();
            _broadcasts.AddRange(NetUtil.GetBroadcastAddresses());

            Task.Run(() => ReceiveLoop(_cts.Token));
            Task.Run(() => AnnounceLoop(_cts.Token));
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    string json = Encoding.UTF8.GetString(result.Buffer);
                    Announce a = null;
                    try { a = JsonSerializer.Deserialize<Announce>(json); }
                    catch { continue; }

                    if (a == null || a.App != "choibin" || string.IsNullOrEmpty(a.Id)) continue;
                    if (a.Id == SelfId) continue;

                    if (a.Bye)
                    {
                        Action<string> gone = PeerGone;
                        if (gone != null) gone(a.Id);
                        continue;
                    }

                    var peer = new Peer
                    {
                        Id = a.Id,
                        Name = string.IsNullOrWhiteSpace(a.Name) ? result.RemoteEndPoint.Address.ToString() : a.Name,
                        Address = result.RemoteEndPoint.Address.ToString(),
                        Port = a.Port <= 0 ? AppSettings.TransferPort : a.Port,
                        Platform = a.Platform,
                        LastSeen = DateTime.Now
                    };

                    Action<Peer> seen = PeerSeen;
                    if (seen != null) seen(peer);

                    // 相手からの新規通知には、こちらの存在を直接返します
                    if (a.Reply)
                        SendTo(new IPEndPoint(result.RemoteEndPoint.Address, AppSettings.DiscoveryPort), false, false);
                }
                catch (ObjectDisposedException) { return; }
                catch (OperationCanceledException) { return; }
                catch
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        private async Task AnnounceLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Broadcast(true);
                try { await Task.Delay(4000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>存在通知をLAN全体へ送ります。</summary>
        public void Broadcast(bool wantReply)
        {
            byte[] payload = BuildPayload(wantReply, false);

            foreach (IPAddress bc in _broadcasts) TrySend(payload, new IPEndPoint(bc, AppSettings.DiscoveryPort));
            TrySend(payload, new IPEndPoint(_multicast, AppSettings.DiscoveryPort));
        }

        private void SendTo(IPEndPoint endPoint, bool wantReply, bool bye)
        {
            TrySend(BuildPayload(wantReply, bye), endPoint);
        }

        private byte[] BuildPayload(bool wantReply, bool bye)
        {
            var a = new Announce
            {
                App = "choibin",
                Id = SelfId,
                Name = _settings.DeviceName,
                Platform = "Windows",
                Port = AppSettings.TransferPort,
                Reply = wantReply,
                Bye = bye
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(a));
        }

        private void TrySend(byte[] data, IPEndPoint endPoint)
        {
            try
            {
                if (_udp != null) _udp.Send(data, data.Length, endPoint);
            }
            catch
            {
                // 送れないインターフェースは無視します
            }
        }

        public void Dispose()
        {
            try
            {
                byte[] bye = BuildPayload(false, true);
                foreach (IPAddress bc in _broadcasts) TrySend(bye, new IPEndPoint(bc, AppSettings.DiscoveryPort));
                if (_multicast != null) TrySend(bye, new IPEndPoint(_multicast, AppSettings.DiscoveryPort));
            }
            catch { }

            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
        }
    }
}
