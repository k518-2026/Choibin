using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Choibin.Core;
using Choibin.Models;

namespace Choibin.Net
{
    /// <summary>選んだ相手へファイルを送ります。</summary>
    public static class DropClient
    {
        public static async Task SendAsync(
            Peer peer,
            IList<string> paths,
            string myName,
            Func<string, TransferItem> createItem,
            Action<string> onError,
            Func<Peer, string, bool> confirmCode)
        {
            var entries = new List<FileEntry>();
            var valid = new List<string>();
            foreach (string p in paths)
            {
                var info = new FileInfo(p);
                if (!info.Exists) continue;
                entries.Add(new FileEntry { Name = info.Name, Size = info.Length });
                valid.Add(p);
            }
            if (valid.Count == 0) return;

            var items = new List<TransferItem>();
            foreach (string p in valid) items.Add(createItem(p));

            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(peer.Address, peer.Port);
                    var timeout = Task.Delay(8000);
                    if (await Task.WhenAny(connect, timeout).ConfigureAwait(false) == timeout)
                        throw new IOException("相手に接続できませんでした（応答なし）。");
                    await connect.ConfigureAwait(false);

                    client.SendTimeout = 120000;
                    client.ReceiveTimeout = 120000;

                    using (NetworkStream raw = client.GetStream())
                    using (SecureChannel stream = SecureChannel.Establish(raw, true))
                    {
                        foreach (TransferItem it in items)
                        {
                            it.IsEncrypted = true;
                            it.SecurityCode = stream.VerificationCode;
                            it.Detail = "確認コード " + stream.VerificationCode + " ・相手の応答を待っています。";
                        }

                        if (confirmCode != null && !confirmCode(peer, stream.VerificationCode))
                        {
                            foreach (TransferItem it in items)
                            {
                                it.State = TransferState.Cancelled;
                                it.Detail = "送信を中止しました。";
                            }
                            return;
                        }

                        var header = new TransferHeader { Sender = myName, Files = entries };
                        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header));
                        byte[] len =
                        {
                            (byte)(json.Length >> 24), (byte)(json.Length >> 16),
                            (byte)(json.Length >> 8), (byte)json.Length
                        };
                        await stream.WriteAsync(len, 0, 4).ConfigureAwait(false);
                        await stream.WriteAsync(json, 0, json.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);

                        int answer = stream.ReadByte();
                        if (answer != 1)
                        {
                            foreach (TransferItem it in items)
                            {
                                it.State = TransferState.Rejected;
                                it.Detail = "相手が受け取りを断りました。";
                            }
                            return;
                        }

                        var buffer = new byte[81920];
                        for (int i = 0; i < valid.Count; i++)
                        {
                            TransferItem item = items[i];
                            item.State = TransferState.Running;

                            using (var fs = new FileStream(valid[i], FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                            {
                                long sent = 0;
                                int read;
                                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                                {
                                    await stream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                    sent += read;
                                    item.DoneBytes = sent;
                                }
                            }

                            item.State = TransferState.Done;
                            item.Detail = peer.Name + " へ暗号化して送信しました（確認コード "
                                          + stream.VerificationCode + "）。";
                        }

                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (TransferItem it in items)
                {
                    if (it.State != TransferState.Done)
                    {
                        it.State = TransferState.Failed;
                        it.Detail = ex.Message;
                    }
                }
                if (onError != null) onError(ex.Message);
            }
        }
    }
}
