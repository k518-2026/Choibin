using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Choibin.Core
{
    /// <summary>
    /// スマホ向けページをHTTPSで配るための自己署名証明書を用意します。
    ///
    /// 認証局は使えないので（LAN内のプライベートIPには証明書が発行されません）、
    /// 自分で作った証明書を使います。ブラウザーは初回に警告を出しますが、
    /// 通信自体はTLSで暗号化されます。証明書は %AppData%\Choibin に保存し、
    /// PCのIPアドレスが変わったときだけ作り直します。
    /// </summary>
    public static class PhoneCertificate
    {
        private const string Password = "choibin-local";

        public static X509Certificate2 GetOrCreate(IList<IPAddress> addresses)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Choibin");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "phone-" + Fingerprint(addresses) + ".pfx");

            if (File.Exists(path))
            {
                try
                {
                    var existing = new X509Certificate2(
                        File.ReadAllBytes(path), Password,
                        X509KeyStorageFlags.Exportable);
                    if (existing.NotAfter > DateTime.Now.AddDays(7)) return existing;
                }
                catch
                {
                    // 壊れていたら作り直します
                }
            }

            X509Certificate2 created = Create(addresses);
            try { File.WriteAllBytes(path, created.Export(X509ContentType.Pfx, Password)); }
            catch { }
            CleanOldFiles(dir, Path.GetFileName(path));
            return created;
        }

        private static X509Certificate2 Create(IList<IPAddress> addresses)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=Choibin", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                foreach (IPAddress ip in addresses)
                {
                    try { san.AddIpAddress(ip); }
                    catch { }
                }
                request.CertificateExtensions.Add(san.Build());

                DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1);
                DateTimeOffset to = DateTimeOffset.UtcNow.AddYears(2);

                using (X509Certificate2 cert = request.CreateSelfSigned(from, to))
                {
                    // CreateSelfSigned の秘密鍵はそのままではSslStreamで使えないため、
                    // 一度PFXに書き出して読み直します。
                    return new X509Certificate2(
                        cert.Export(X509ContentType.Pfx, Password), Password,
                        X509KeyStorageFlags.Exportable);
                }
            }
        }

        private static string Fingerprint(IList<IPAddress> addresses)
        {
            var list = new List<string>();
            foreach (IPAddress ip in addresses) list.Add(ip.ToString());
            list.Sort(StringComparer.Ordinal);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", list)));
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static void CleanOldFiles(string dir, string keep)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dir, "phone-*.pfx"))
                {
                    if (!string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }
            }
            catch { }
        }
    }
}
