using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Choibin.Models
{
    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>同じLAN上で見つかった相手のPC。</summary>
    public class Peer : NotifyBase
    {
        private string _name;
        private string _address;
        private int _port;
        private string _platform;
        private DateTime _lastSeen;
        private bool _isSelected;

        public string Id { get; set; }

        public string Name
        {
            get { return _name; }
            set { if (Set(ref _name, value)) Raise("Initial"); }
        }

        public string Address
        {
            get { return _address; }
            set { Set(ref _address, value); }
        }

        public int Port
        {
            get { return _port; }
            set { Set(ref _port, value); }
        }

        public string Platform
        {
            get { return _platform; }
            set { Set(ref _platform, value); }
        }

        public DateTime LastSeen
        {
            get { return _lastSeen; }
            set { Set(ref _lastSeen, value); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value); }
        }

        public string Initial
        {
            get { return string.IsNullOrEmpty(_name) ? "?" : _name.Substring(0, 1).ToUpperInvariant(); }
        }
    }

    public enum TransferState
    {
        Waiting,
        Running,
        Done,
        Failed,
        Rejected,
        Cancelled
    }

    /// <summary>送信・受信1件分の記録。</summary>
    public class TransferItem : NotifyBase
    {
        private string _fileName;
        private long _totalBytes;
        private long _doneBytes;
        private TransferState _state;
        private string _detail;

        public bool IsIncoming { get; set; }
        public string PeerName { get; set; }
        /// <summary>この転送が暗号化されていたか。</summary>
        public bool IsEncrypted { get; set; }
        /// <summary>相手と見比べるための6桁の確認コード。</summary>
        public string SecurityCode { get; set; }
        public string SavedPath { get; set; }
        public DateTime StartedAt { get; set; }

        public string FileName
        {
            get { return _fileName; }
            set { Set(ref _fileName, value); }
        }

        public long TotalBytes
        {
            get { return _totalBytes; }
            set { if (Set(ref _totalBytes, value)) Raise("Percent"); }
        }

        public long DoneBytes
        {
            get { return _doneBytes; }
            set { if (Set(ref _doneBytes, value)) Raise("Percent"); }
        }

        public double Percent
        {
            get { return _totalBytes <= 0 ? 0 : (double)_doneBytes * 100.0 / _totalBytes; }
        }

        public TransferState State
        {
            get { return _state; }
            set { if (Set(ref _state, value)) Raise("StateText"); }
        }

        public string Detail
        {
            get { return _detail; }
            set { Set(ref _detail, value); }
        }

        public string StateText
        {
            get
            {
                switch (_state)
                {
                    case TransferState.Waiting: return "待機中";
                    case TransferState.Running: return "転送中";
                    case TransferState.Done: return "完了";
                    case TransferState.Rejected: return "拒否されました";
                    case TransferState.Cancelled: return "中止しました";
                    default: return "失敗";
                }
            }
        }

        public string Direction
        {
            get { return IsIncoming ? "受信" : "送信"; }
        }

        /// <summary>一覧に出す鍵マーク。</summary>
        public string LockGlyph
        {
            get { return IsEncrypted ? "\U0001F512" : string.Empty; }
        }

        public string SecurityText
        {
            get
            {
                if (!IsEncrypted) return "暗号化なし";
                return string.IsNullOrEmpty(SecurityCode)
                    ? "暗号化済み"
                    : "暗号化済み・確認コード " + SecurityCode;
            }
        }
    }

    /// <summary>スマホ向けページで公開しているファイル。</summary>
    public class SharedFile
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
    }
}
