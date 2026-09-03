using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Choibin.Core
{
    /// <summary>
    /// PC間の転送を暗号化するためのストリーム。
    ///
    /// 手順:
    ///   1. 双方がその場かぎりの ECDH (P-256) 鍵ペアを作り、公開鍵を交換する
    ///   2. 共有秘密から HKDF-SHA256 で方向ごとの AES-256 鍵と IV を導出する
    ///   3. 以降のデータはすべて AES-256-GCM のレコードとして送受信する
    ///
    /// 鍵は接続のたびに作り直すため、過去の通信を後から復号することはできません
    /// （前方秘匿性）。レコードごとに連番を混ぜた nonce を使うので、
    /// 並べ替え・欠落・再送はすべて復号エラーとして検出されます。
    ///
    /// 中間者攻撃に備えて、共有秘密から6桁の確認コードを導出します。
    /// 送信側と受信側で同じ数字が出ていれば、間に第三者はいません。
    /// </summary>
    public sealed class SecureChannel : Stream
    {
        private const int MaxRecord = 64 * 1024;
        private const int TagSize = 16;
        private static readonly byte[] Magic = { (byte)'W', (byte)'D', (byte)'S', 1 };

        private readonly Stream _inner;
        private readonly AesGcm _encryptor;
        private readonly AesGcm _decryptor;
        private readonly byte[] _sendIvPrefix;
        private readonly byte[] _recvIvPrefix;
        private readonly byte[] _nonce = new byte[12];
        private readonly byte[] _header = new byte[4];

        private ulong _sendSeq;
        private ulong _recvSeq;

        private byte[] _plainBuffer = new byte[0];
        private int _plainOffset;
        private int _plainLength;
        private bool _disposed;

        /// <summary>送信側と受信側の画面に出す6桁の確認コード。</summary>
        public string VerificationCode { get; private set; }

        /// <summary>使っている暗号方式の説明（画面表示用）。</summary>
        public static string AlgorithmName
        {
            get { return "ECDH P-256 + AES-256-GCM"; }
        }

        private SecureChannel(Stream inner, byte[] sendKey, byte[] sendIv,
                              byte[] recvKey, byte[] recvIv, string code)
        {
            _inner = inner;
            _encryptor = new AesGcm(sendKey, TagSize);
            _decryptor = new AesGcm(recvKey, TagSize);
            _sendIvPrefix = sendIv;
            _recvIvPrefix = recvIv;
            VerificationCode = code;
        }

        /// <summary>鍵交換を行い、暗号化されたストリームを返します。</summary>
        /// <param name="inner">接続済みのTCPストリーム。</param>
        /// <param name="isClient">送信側なら true、受信側なら false。</param>
        public static SecureChannel Establish(Stream inner, bool isClient)
        {
            using (ECDiffieHellman mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] myPublic = mine.PublicKey.ExportSubjectPublicKeyInfo();
                byte[] peerPublic;

                if (isClient)
                {
                    SendPublicKey(inner, myPublic);
                    peerPublic = ReceivePublicKey(inner);
                }
                else
                {
                    peerPublic = ReceivePublicKey(inner);
                    SendPublicKey(inner, myPublic);
                }

                byte[] secret;
                using (ECDiffieHellman peer = ECDiffieHellman.Create())
                {
                    int consumed;
                    peer.ImportSubjectPublicKeyInfo(peerPublic, out consumed);
                    secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
                }

                // salt は「送信側の公開鍵 + 受信側の公開鍵」。双方で同じ順序になります。
                byte[] clientPub = isClient ? myPublic : peerPublic;
                byte[] serverPub = isClient ? peerPublic : myPublic;
                byte[] salt = Concat(clientPub, serverPub);

                byte[] c2sKey = Derive(secret, salt, "Choibin/1 key c2s", 32);
                byte[] s2cKey = Derive(secret, salt, "Choibin/1 key s2c", 32);
                byte[] c2sIv = Derive(secret, salt, "Choibin/1 iv c2s", 4);
                byte[] s2cIv = Derive(secret, salt, "Choibin/1 iv s2c", 4);
                byte[] confirm = Derive(secret, salt, "Choibin/1 confirm", 4);

                CryptographicOperations.ZeroMemory(secret);

                uint value = ((uint)confirm[0] << 24) | ((uint)confirm[1] << 16) |
                             ((uint)confirm[2] << 8) | confirm[3];
                string code = (value % 1000000u).ToString("D6");

                return isClient
                    ? new SecureChannel(inner, c2sKey, c2sIv, s2cKey, s2cIv, code)
                    : new SecureChannel(inner, s2cKey, s2cIv, c2sKey, c2sIv, code);
            }
        }

        private static byte[] Derive(byte[] secret, byte[] salt, string info, int length)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, length, salt,
                                  Encoding.UTF8.GetBytes(info));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static void SendPublicKey(Stream stream, byte[] publicKey)
        {
            var head = new byte[6];
            Buffer.BlockCopy(Magic, 0, head, 0, 4);
            head[4] = (byte)(publicKey.Length >> 8);
            head[5] = (byte)publicKey.Length;
            stream.Write(head, 0, head.Length);
            stream.Write(publicKey, 0, publicKey.Length);
            stream.Flush();
        }

        private static byte[] ReceivePublicKey(Stream stream)
        {
            var head = new byte[6];
            ReadExact(stream, head, head.Length);

            for (int i = 0; i < 4; i++)
            {
                if (head[i] != Magic[i])
                    throw new IOException(
                        "相手が暗号化に対応していません。両方の「ちょい便」を最新版にしてください。");
            }

            int length = (head[4] << 8) | head[5];
            if (length <= 0 || length > 4096)
                throw new IOException("鍵の交換に失敗しました（データが壊れています）。");

            var key = new byte[length];
            ReadExact(stream, key, length);
            return key;
        }

        private static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("接続が切断されました。");
                offset += read;
            }
        }

        private void FillNonce(byte[] prefix, ulong sequence)
        {
            Buffer.BlockCopy(prefix, 0, _nonce, 0, 4);
            for (int i = 0; i < 8; i++)
                _nonce[11 - i] = (byte)(sequence >> (8 * i));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");

            while (count > 0)
            {
                int chunk = Math.Min(count, MaxRecord);

                _header[0] = (byte)(chunk >> 24);
                _header[1] = (byte)(chunk >> 16);
                _header[2] = (byte)(chunk >> 8);
                _header[3] = (byte)chunk;

                var cipher = new byte[chunk];
                var tag = new byte[TagSize];

                FillNonce(_sendIvPrefix, _sendSeq);
                // 長さヘッダーを追加認証データにすることで、途中で書き換えられたら復号に失敗します。
                _encryptor.Encrypt(_nonce,
                                   new ReadOnlySpan<byte>(buffer, offset, chunk),
                                   cipher, tag, _header);
                _sendSeq++;

                _inner.Write(_header, 0, 4);
                _inner.Write(cipher, 0, cipher.Length);
                _inner.Write(tag, 0, tag.Length);

                offset += chunk;
                count -= chunk;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (count <= 0) return 0;

            if (_plainOffset >= _plainLength)
            {
                if (!ReadRecord()) return 0;
            }

            int available = _plainLength - _plainOffset;
            int take = Math.Min(available, count);
            Buffer.BlockCopy(_plainBuffer, _plainOffset, buffer, offset, take);
            _plainOffset += take;
            return take;
        }

        private bool ReadRecord()
        {
            int got = 0;
            while (got < 4)
            {
                int read = _inner.Read(_header, got, 4 - got);
                if (read <= 0)
                {
                    if (got == 0) return false;   // 相手が正常に閉じた
                    throw new IOException("接続が切断されました。");
                }
                got += read;
            }

            int length = (_header[0] << 24) | (_header[1] << 16) | (_header[2] << 8) | _header[3];
            if (length < 0 || length > MaxRecord)
                throw new CryptographicException("受信データが壊れています。");

            var cipher = new byte[length];
            var tag = new byte[TagSize];
            ReadExact(_inner, cipher, length);
            ReadExact(_inner, tag, TagSize);

            if (_plainBuffer.Length < length) _plainBuffer = new byte[Math.Max(length, 4096)];

            FillNonce(_recvIvPrefix, _recvSeq);
            try
            {
                _decryptor.Decrypt(_nonce, cipher, tag,
                                   new Span<byte>(_plainBuffer, 0, length), _header);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException(
                    "受信データの検証に失敗しました。通信が改ざんされたか、途中で壊れています。");
            }
            _recvSeq++;

            _plainOffset = 0;
            _plainLength = length;
            return length > 0 || ReadRecord();
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                try { _encryptor.Dispose(); } catch { }
                try { _decryptor.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
