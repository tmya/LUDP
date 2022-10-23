using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LUDP
{
    /// <summary>
    /// UDPのサーバとクライアント実装
    /// </summary>
    public class UDP
    {
        /// <summary>
        /// サーバの場合
        /// StartServerMode(Int32 port) → UDP.ReceiveEventHandler += [関数]
        /// サーバ側は送信する前に一度クライアントからデータを受信しないとデータを送信できない。
        /// （クライアントのアドレスを知らないため。最初に受信したクライアントとのみ通信可能。）
        /// 
        /// クライアントの場合
        /// StartClientMode(String serverIP, Int32 port) → UDP.ReceiveEventHander += [関数]
        /// 
        /// データ送信は
        /// UDP.SendData()
        /// 
        /// データ受信は
        /// UDP.ReceiveEventHanderのイベントハンドラを登録すればデータ受信時に呼ばれる。
        /// </summary>
        private UdpClient _udpClient { get; set; }
        //private IPEndPoint _remoteEP { get; set; }
        private IPEndPoint _localEP { get; set; }

        public UDP() { }
        ~UDP() { }

        public void CreateSocket(Int32 port)
        {
            if (this._udpClient != null) return;
           
            this._localEP = new IPEndPoint(IPAddress.Any, port);

            this._udpClient = new UdpClient(this._localEP);
            this._udpClient.BeginReceive(new AsyncCallback(ReceiveCallback), this._udpClient);
        }

        public async void SendData(String message, IPEndPoint remoteEP)
        {
            byte[] sendByte = Encoding.UTF8.GetBytes(message);
            await this._udpClient?.SendAsync(sendByte, sendByte.Length, remoteEP);
        }
        public async void SendData(Byte[] data, IPEndPoint remoteEP)
        {
            await this._udpClient?.SendAsync(data, data.Length, remoteEP);
        }

        //Form側でUIを操作しようとするとスレッドが違うのでエラーになるからForm側でInvokeすること
        public event Action<Object, Byte[], IPEndPoint> ReceiveUdpDataEvent;
        /// <summary>
        /// Use Action<Object, Byte[]> ReceiveEventHander</Object>
        /// </summary>
        /// <param name="ar">UdpClient</param>
        private void ReceiveCallback(IAsyncResult ar)
        {
            UdpClient u = (UdpClient)(ar.AsyncState);
            try
            {
                IPEndPoint e = null;
                byte[] data = u.EndReceive(ar, ref e);

                this.ReceiveUdpDataEvent?.Invoke(this, data, e);
            }
            ///
            /// このエラー処理どうするか要検討
            ///
            catch(Exception e)
            {
                Debug.WriteLine(e.Message);
                this._udpClient?.Dispose();
                this._udpClient = null;
                return;
            }
            this._udpClient.BeginReceive(new AsyncCallback(ReceiveCallback), u);
        }
    }
}
