using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Choibin.Core
{
    public static class NetUtil
    {
        /// <summary>LAN内で他の端末から到達できるIPv4アドレスを返します。</summary>
        public static IPAddress GetLocalAddress()
        {
            IPAddress fallback = null;

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                {
                    if (g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !g.Address.Equals(IPAddress.Any))
                    {
                        hasGateway = true;
                        break;
                    }
                }

                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // リンクローカルは除外

                    if (hasGateway) return ua.Address;
                    if (fallback == null) fallback = ua.Address;
                }
            }

            return fallback ?? IPAddress.Loopback;
        }

        /// <summary>このPCが持つIPv4アドレスをすべて返します（証明書のSANに使います）。</summary>
        public static List<IPAddress> GetLocalAddresses()
        {
            var list = new List<IPAddress>();

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    if (!list.Contains(ua.Address)) list.Add(ua.Address);
                }
            }

            if (list.Count == 0) list.Add(IPAddress.Loopback);
            return list;
        }

        /// <summary>各インターフェースのサブネットブロードキャストアドレス一覧。</summary>
        public static List<IPAddress> GetBroadcastAddresses()
        {
            var list = new List<IPAddress>();

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;

                    byte[] addr = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;

                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(addr[i] | (byte)~mask[i]);
                    var ip = new IPAddress(bc);
                    if (!list.Contains(ip)) list.Add(ip);
                }
            }

            if (!list.Contains(IPAddress.Broadcast)) list.Add(IPAddress.Broadcast);
            return list;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do
            {
                v /= 1024.0;
                i++;
            } while (v >= 1024.0 && i < units.Length - 1);
            return v.ToString(v >= 100 ? "0" : "0.0") + " " + units[i];
        }

        /// <summary>受信したファイル名から危険な文字とパス区切りを取り除きます。</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "received.bin";
            name = name.Replace("\\", "/");
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);

            var sb = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                bool bad = false;
                foreach (char iv in invalid) if (c == iv) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }

            string result = sb.ToString().Trim().TrimStart('.');
            if (result.Length == 0) return "received.bin";
            if (result.Length > 180) result = result.Substring(0, 180);
            return result;
        }

        /// <summary>同名ファイルがある場合に「名前 (2).ext」へ退避します。</summary>
        public static string UniquePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return path;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 2; i < 10000; i++)
            {
                path = Path.Combine(folder, stem + " (" + i + ")" + ext);
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(folder, Guid.NewGuid().ToString("N") + ext);
        }

        public static string GuessContentType(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".heic": return "image/heic";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain; charset=utf-8";
                case ".csv": return "text/csv; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".mp4": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".mp3": return "audio/mpeg";
                case ".zip": return "application/zip";
                default: return "application/octet-stream";
            }
        }
    }
}
