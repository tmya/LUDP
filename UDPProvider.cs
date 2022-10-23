using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LUDP
{
    public class UDPProvider
    {
        protected UDP _udp { get; set; }
        private List<ConnectionManager> _ludpPeerStruct = new List<ConnectionManager>();
        public Boolean IsServer { get; set; }

        // Client mode
        public UDPProvider()
        {
            _udp = new UDP();
            _udp.CreateSocket(0);
            this.IsServer = false;

            this.UDPProviderInit();
        }
        // Server mode
        public UDPProvider(Int32 port)
        {
            _udp = new UDP();
            _udp.CreateSocket(port);
            this.IsServer = true;

            this.UDPProviderInit();
        }
        private void UDPProviderInit()
        {
            _udp.ReceiveUdpDataEvent += this.OnReceivePacketEvent;
        }

        /// <summary>
        /// Set server's ip address.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        public void SetServer(String ipAddress, Int32 port)
        {
            ConnectionManager server = new ConnectionManager(this, IPAddress.Parse(ipAddress), port);
            server.SendHelloPacket();

            server.ConnectionManager_OnRetreveStreamDataEvent += this.OnRetreveStreamDataEvent;
            server.ConnectionManager_OnRetreveEventDataEvent += this.OnRetreveEventDataEvent;

            this._ludpPeerStruct.Add(server);
        }
        /// <summary>
        /// Send data to all peers.
        /// </summary>
        /// <param name="data"></param>
        public void SendData(Byte[] data)
        {
            foreach(ConnectionManager v in this._ludpPeerStruct)
            {
                this._udp.SendData(data, v.Peer); 
            }
        }
        /// <summary>
        /// Send data to selected peer.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="peer"></param>
        public void SendData(Byte[] data, IPEndPoint peer)
        {
            this._udp.SendData(data, peer);
        }

        public void SetStreamData(String message)
        {
            foreach(ConnectionManager pr in this._ludpPeerStruct)
            {
                pr.SetStreamData(message);
            }
        }
        public void SetEventData(String message)
        {
            foreach(ConnectionManager pr in this._ludpPeerStruct)
            {
                pr.SetEventData(message);
            }
        }

        public void OnReceivePacketEvent(Object sender, Byte[] data, IPEndPoint peer)
        {
            ConnectionManager dummy = new ConnectionManager(this);
            LUDPPacket pkt = dummy.ByteToLUDPPacket(data);

            ConnectionManager pr = this._ludpPeerStruct.Find(v => v.CommandChannel.SessionID == pkt.SessionID);

            if(pr is null)
            {
                if(pkt.ChannelID == (Byte)LUDP_PACKET_HEADER_CHANNEL.COMMANDS && pkt.Flags == (Byte)LUDP_PACKET_HEADER_FLAGS.HELLO)
                {
                    ConnectionManager client = new ConnectionManager(this, peer.Address, peer.Port);
                    client.ReceivePacketParser(pkt);

                    client.ConnectionManager_OnRetreveEventDataEvent += this.OnRetreveEventDataEvent;

                    this._ludpPeerStruct.Add(client);
                }
            }
            else
            {
                pr.ReceivePacketParser(pkt);
            }
        }

        public event Action<Object, String> UDPProvider_OnRetreveStreamDataEvent;
        public void OnRetreveStreamDataEvent(Object sender, String message)
        {
            this.UDPProvider_OnRetreveStreamDataEvent?.Invoke(sender, message);
        }

        public event Action<Object, String> UDPProvider_OnRetreveEventDataEvent;
        public void OnRetreveEventDataEvent(Object sender, String message)
        {
            this.UDPProvider_OnRetreveEventDataEvent?.Invoke(sender, message);
        }


        class ConnectionManager
        {
            private Boolean _isDebug = false;

            public IPEndPoint Peer { get; private set; }
            public PacketManager CommandChannel { get; set; } = new PacketManager(LUDP_PACKET_HEADER_CHANNEL.COMMANDS);
            public PacketManager StreamChannel { get; set; } = new PacketManager(LUDP_PACKET_HEADER_CHANNEL.STREAM);
            public PacketManager EventChannel { get; set; } = new PacketManager(LUDP_PACKET_HEADER_CHANNEL.EVENT);
            public PACKET_MANAGEMENT_STATE State { get; set; } = PACKET_MANAGEMENT_STATE.NONE;

            private UDPProvider _udpProvider { get; set; }
            private Int32 _ludpPacketHeaderSizeCache { get; set; }
            private Int32 _ludpDatagramHeaderSizeCache { get; set; }
            private Int32 _continuousTransmissionThreshold { get; set; } = 5;
            private Timer _timer { get; set; }
            private Int32 _loopInterval { get; } = 100;
            private Int64 _maxTransferInterval { get; } = 3000;
            private Boolean _isServer { get; set; }

            private String REQUESTSTREAM = "REQUESTSTREAM";

            public ConnectionManager(UDPProvider udpProvider, IPAddress ip, Int32 port)
            {
                this._udpProvider = udpProvider;
                this.Peer = new IPEndPoint(ip, port);

                this.ConnectionManagerInit();
            }
            public ConnectionManager(UDPProvider udpProvider)
            {
                this._udpProvider = udpProvider;

                this.ConnectionManagerInit();
            }

            private void ConnectionManagerInit()
            {
                this._ludpDatagramHeaderSizeCache = this.LUDPDatagramHeaderSize;
                this._ludpPacketHeaderSizeCache = this.LUDPPacketHeaderSize;
            }
            private void SendPacket(LUDPPacket packet)
            {
                Byte[] data = this.LUDPPacketToByte(packet);
                this._udpProvider?.SendData(data);
            }
            private void SendPacket(LUDPPacket packet, IPEndPoint peer)
            {
                Byte[] data = this.LUDPPacketToByte(packet);
                this._udpProvider?.SendData(data, peer);
            }
            private void SetSessionID(Byte sessionID)
            {
                this.CommandChannel.SessionID = sessionID;
                this.StreamChannel.SessionID = sessionID;
                this.EventChannel.SessionID = sessionID;
            }


            private void SendPacketLoop()
            {
                TimerCallback timerDelegate = state =>
                {                  
                    if (this.StreamChannel.GetElapsedAfterTransmissionByMilliseconds() > this._maxTransferInterval)
                    {
                        this.SendBufferedStreamDataPacket();
                        this.StreamChannel.RestartStopwatch();
                    }
                    if (this.EventChannel.GetElapsedAfterTransmissionByMilliseconds() > this._maxTransferInterval)
                    {
                        this.SendBufferedEventDataPacket();
                        this.EventChannel.RestartStopwatch();
                    }
                };
                this._timer = new Timer(timerDelegate, null, 0, this._loopInterval);
            }
            private void StopPacketLoop()
            {
                this._timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            private void SendBufferedStreamDataPacket()
            {
                // Stream data
                if(this._isServer)
                {
                    this.SendPacket(this.StreamChannel.Packet);
                    Debug.WriteLineIf(this._isDebug, "Sended last sequence no : " + this.StreamChannel.Packet.Data.Last().SequenceNo);
                    this.StreamChannel.SetSendedSequenceNo(this.StreamChannel.Packet.Data.Last().SequenceNo);
                    this.StreamChannel.RestartStopwatch();
                }
            }
            private void SendBufferedEventDataPacket()
            {
                // Event data
                this.SendPacket(this.EventChannel.Packet);
                this.EventChannel.SetSendedSequenceNo(this.EventChannel.Packet.Data.Last().SequenceNo);
                this.EventChannel.RestartStopwatch();
            }

            public void SetStreamData(String message)
            {
                this.StreamChannel.SetDatagram(Encoding.UTF8.GetBytes(message));

                if (this.StreamChannel.SendedSequenceNo + this._continuousTransmissionThreshold < this.StreamChannel.Packet.Data.Last().SequenceNo)
                {
                    this.SendBufferedStreamDataPacket();
                    this.StreamChannel.RestartStopwatch();
                }
            }
            public void SetEventData(String message)
            {
                this.EventChannel.SetDatagram(Encoding.UTF8.GetBytes(message));
                this.SendBufferedEventDataPacket();
                this.EventChannel.RestartStopwatch();
            }

            

            public event Action<Object, String> ConnectionManager_OnRetreveStreamDataEvent;
            public event Action<Object, String> ConnectionManager_OnRetreveEventDataEvent;
            public void ReceivePacketParser(LUDPPacket receivePacket)
            {
                switch (receivePacket.ChannelID)
                {
                    case (Byte)LUDP_PACKET_HEADER_CHANNEL.COMMANDS:
                        switch (receivePacket.Flags)
                        {
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.HELLO:
                                // Receive new connection request 
                                Debug.WriteLineIf(this._isDebug, "Receive request new connection");
                                this.SetSessionID(receivePacket.SessionID);

                                // Send ack
                                Debug.WriteLineIf(this._isDebug, "Send Hello ACK");
                                this.SendHelloAckPacket();

                                this.State = PACKET_MANAGEMENT_STATE.CONNECTED;
                                this._isServer = true;

                                break;

                            case (Byte)LUDP_PACKET_HEADER_FLAGS.DATA:
                                // It'll be disconnect or stream data requested
                                LUDPDatagram[] nDatagram = this.CommandChannel.RetreveNewDatagram(receivePacket);

                                Debug.WriteLineIf(this._isDebug, receivePacket.DataCount.ToString() + " / New datagram(commands) : " + nDatagram.Length.ToString());

                                for (int i = 0; i < nDatagram.Length; i++)
                                {
                                    Debug.WriteLineIf(this._isDebug, "Datagram : " + Encoding.UTF8.GetString(nDatagram[i].Data));
                                    if (Encoding.UTF8.GetString(nDatagram[i].Data) == this.REQUESTSTREAM)
                                    {
                                        LUDPPacket ackpkt = this.CommandChannel.CreateAckPacket();
                                        Debug.WriteLineIf(this._isDebug, "Send Command ACK : " + ackpkt.AckNo.ToString());
                                        this.SendPacket(ackpkt, this.Peer);

                                        Debug.WriteLineIf(this._isDebug, "Starting Send packet loop....");
                                        this.SendPacketLoop();
                                    }
                                }

                                break;
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.ACK:
                                // echo ack

                                if (receivePacket.AckNo == this.CommandChannel.SessionID)
                                {
                                    // Receive new connection ok
                                    Debug.WriteLineIf(this._isDebug, "Receive ack new connection. Set state connected.");
                                    this.State = PACKET_MANAGEMENT_STATE.CONNECTED;

                                    // Request stream data
                                    Debug.WriteLineIf(this._isDebug, "Send request stream data.");
                                    this.SendRequestStreamData();
                                }
                                break;
                            default:
                                break;
                        }

                        break;
                    case (Byte)LUDP_PACKET_HEADER_CHANNEL.STREAM:
                        switch (receivePacket.Flags)
                        {
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.HELLO:

                                break;
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.DATA:
                                // Receive stream data with window control
                                Debug.WriteIf(this._isDebug, "Received Stream data count : ");

                                LUDPDatagram[] nDatagram = this.StreamChannel.RetreveNewDatagram(receivePacket);

                                Debug.WriteLineIf(this._isDebug, receivePacket.DataCount.ToString() + " / New datagram : " + nDatagram.Length.ToString());

                                for(int i=0; i < nDatagram.Length; i++)
                                {
                                    Debug.WriteLineIf(this._isDebug, "Datagram : " + Encoding.UTF8.GetString(nDatagram[i].Data));
                                    this.ConnectionManager_OnRetreveStreamDataEvent?.Invoke(this, Encoding.UTF8.GetString(nDatagram[i].Data));
                                }

                                LUDPPacket ackpkt = this.StreamChannel.CreateAckPacket();
                                Debug.WriteLineIf(this._isDebug, "Send Stream data ACK : " + ackpkt.AckNo.ToString());
                                this.SendPacket(ackpkt, this.Peer);

                                break;

                            case (Byte)LUDP_PACKET_HEADER_FLAGS.ACK:
                                // Receive stream data ack
                                Debug.WriteIf(this._isDebug, "Received Stream data ACK : ");
                                Debug.WriteLineIf(this._isDebug, receivePacket.AckNo.ToString());
                                
                                break;
                            default:
                                break;
                        }
                        break;
                    case (Byte)LUDP_PACKET_HEADER_CHANNEL.EVENT:
                        switch (receivePacket.Flags)
                        {
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.HELLO:

                                break;
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.DATA:
                                // Receive event data with window control
                                Debug.WriteIf(this._isDebug, "イベントデータを受信:");

                                LUDPDatagram[] nDatagram = this.EventChannel.RetreveNewDatagram(receivePacket);

                                for (int i = 0; i < nDatagram.Length; i++)
                                {
                                    Debug.WriteIf(this._isDebug, nDatagram[i].SequenceNo.ToString() + " - ");
                                    Debug.WriteLineIf(this._isDebug, Encoding.UTF8.GetString(nDatagram[i].Data));

                                    this.ConnectionManager_OnRetreveEventDataEvent?.Invoke(this, Encoding.UTF8.GetString(nDatagram[i].Data));
                                }

                                LUDPPacket ackpkt = this.EventChannel.CreateAckPacket();
                                this.SendPacket(ackpkt, this.Peer);                                

                                break;
                            case (Byte)LUDP_PACKET_HEADER_FLAGS.ACK:
                                // Receive event data ack
                                Debug.WriteIf(this._isDebug, "Received Stream data ACK : ");
                                Debug.WriteLineIf(this._isDebug, receivePacket.AckNo.ToString());

                                break;
                            default:
                                break;
                        }
                        break;
                    default:
                        break;
                }
            }
            public void SendHelloPacket()
            {
                this.CommandChannel.Packet.Flags = (Byte)LUDP_PACKET_HEADER_FLAGS.HELLO;
                this.CommandChannel.Packet.AckNo = 0;
                RandomNumberGenerator rnd = RandomNumberGenerator.Create();
                Byte[] buf = new byte[1];
                rnd.GetBytes(buf, 0, 1);
                this.CommandChannel.Packet.SessionID = buf[0];
                this.CommandChannel.Packet.ChannelID = (Byte)LUDP_PACKET_HEADER_CHANNEL.COMMANDS;
                this.CommandChannel.Packet.DataCount = 0;

                this.SetSessionID(buf[0]);

                this.SendPacket(this.CommandChannel.Packet, this.Peer);
            }
            private void SendHelloAckPacket()
            {
                this.CommandChannel.Packet.Flags = (Byte)LUDP_PACKET_HEADER_FLAGS.ACK;
                this.CommandChannel.Packet.AckNo = this.CommandChannel.SessionID;
                this.CommandChannel.Packet.SessionID = this.CommandChannel.SessionID;
                this.CommandChannel.Packet.ChannelID = (Byte)LUDP_PACKET_HEADER_CHANNEL.COMMANDS;
                this.CommandChannel.Packet.DataCount = 0;

                this.SetSessionID(this.CommandChannel.SessionID);

                this.SendPacket(this.CommandChannel.Packet, this.Peer);
            }
            private void SendRequestStreamData()
            {
                PacketManager mgr = new PacketManager(LUDP_PACKET_HEADER_CHANNEL.COMMANDS);
                mgr.SessionID = this.CommandChannel.SessionID;
                mgr.SetDatagram(Encoding.UTF8.GetBytes(this.REQUESTSTREAM));

                this.SendPacket(mgr.Packet, this.Peer);
            }
            
            public byte[] LUDPPacketToByte(LUDPPacket data)
            {
                Int32 index = 0;

                //Calc LUDP packet size. Header + Datagram
                Int32 dtSize = this._ludpPacketHeaderSizeCache;
                Int32 gSize = this._ludpDatagramHeaderSizeCache;
                foreach (LUDPDatagram v in data?.Data ?? new LUDPDatagram[0])
                {
                    dtSize += gSize + v.DataSize;
                }

                byte[] buf = new byte[dtSize];

                buf[index++] = data.Flags;
                buf[index++] = data.DataCount;
                buf[index++] = (Byte)((data.AckNo >> 8) & 0xFF);
                buf[index++] = (Byte)(data.AckNo & 0xFF);
                buf[index++] = (Byte)(data.SessionID & 0xFF);
                buf[index++] = (Byte)(data.ChannelID & 0xFF);

                foreach (LUDPDatagram v in data?.Data ?? new LUDPDatagram[0])
                {
                    buf[index++] = (Byte)((v.SequenceNo >> 8) & 0xFF);
                    buf[index++] = (Byte)(v.SequenceNo & 0xFF);
                    buf[index++] = (Byte)((v.DataSize >> 8) & 0xFF);
                    buf[index++] = (Byte)(v.DataSize & 0xFF);
                    for (int i = 0; i < v.Data.Length; i++)
                    {
                        buf[index++] = v.Data[i];
                    }
                }

                return buf;
            }

            public LUDPPacket ByteToLUDPPacket(byte[] buf)
            {
                Int32 index = 0;

                LUDPPacket data = new LUDPPacket();
                data.Flags = buf[index++];
                data.DataCount = buf[index++];
                data.AckNo = (UInt16)(buf[index++] << 8);
                data.AckNo |= (UInt16)buf[index++];
                data.SessionID = (Byte)buf[index++];
                data.ChannelID = (Byte)buf[index++];

                data.Data = new LUDPDatagram[data.DataCount];

                for (int i = 0; i < data.DataCount; i++)
                {
                    LUDPDatagram dt = new LUDPDatagram();
                    dt.SequenceNo = (UInt16)(buf[index++] << 8);
                    dt.SequenceNo |= (UInt16)buf[index++];
                    dt.DataSize = (UInt16)(buf[index++] << 8);
                    dt.DataSize |= (UInt16)buf[index++];
                    byte[] d = new byte[dt.DataSize];
                    for (int j = 0; j < dt.DataSize; j++)
                    {
                        d[j] = buf[index++];
                    }
                    dt.Data = d;
                    data.Data[i] = dt;
                }

                return data;
            }
            private Int32 LUDPPacketHeaderSize
            {
                get
                {
                    Int32 size = 0;
                    Type t = typeof(LUDPPacket);
                    PropertyInfo[] mem = t.GetProperties();
                    foreach (PropertyInfo m in mem)
                    {
                        if (m.PropertyType == typeof(LUDPDatagram[])) break;
                        size += Marshal.SizeOf(m.PropertyType);
                    }
                    return size;
                }
            }
            private Int32 LUDPDatagramHeaderSize
            {
                get
                {
                    Int32 size = 0;
                    Type t = typeof(LUDPDatagram);
                    PropertyInfo[] mem = t.GetProperties();
                    foreach (PropertyInfo m in mem)
                    {
                        if (m.PropertyType == typeof(Byte[])) break;
                        size += Marshal.SizeOf(m.PropertyType);
                    }
                    return size;
                }
            }
        }

        private class PacketManager
        {
            public Byte SessionID { get; set; }

            public LUDPPacket Packet = new LUDPPacket();
            private Queue<LUDPDatagram> DatagramQueue = new Queue<LUDPDatagram>();
            private UInt16 _ackNo { get; set; } = 0;
            private UInt16 _sequenceNo { get; set; } = 0;
            public UInt16 SendedSequenceNo { get; private set; } = 0;
            private UInt16 _receivedSequenceNo { get; set; } = 0;
            private LUDP_PACKET_HEADER_CHANNEL _channelID { get; set; }
            private Int32 _allowableLostThreshold { get; } = 20;


            private Stopwatch _stopwatch = new Stopwatch();

            public PacketManager()
            {
                this.PacketManagerInit();
            }
            public PacketManager(LUDP_PACKET_HEADER_CHANNEL channelID)
            {
                this._channelID = channelID;
                this.PacketManagerInit();
            }

            private void PacketManagerInit()
            {
            }

            public void SetSendedSequenceNo(UInt16 sequenceNo)
            {
                this.SendedSequenceNo = sequenceNo;
            }

            public Int64 GetElapsedAfterTransmissionByMilliseconds()
            {
                return this._stopwatch.ElapsedMilliseconds;
            }
            public void RestartStopwatch()
            {
                this._stopwatch.Restart();
            }

            public UInt16 SetDatagram(Byte[] buf)
            {
                if(this._stopwatch.IsRunning == false) this._stopwatch.Start();

                LUDPDatagram data = new LUDPDatagram();
                data.SequenceNo = ++this._sequenceNo;
                data.Data = buf;
                data.DataSize = (UInt16)buf.Length;
                this.DatagramQueue.Enqueue(data);

                if(this.DatagramQueue.Count > 10) this.DatagramQueue.Dequeue();

                this.Packet.SessionID = this.SessionID;
                this.Packet.Flags = (Byte)LUDP_PACKET_HEADER_FLAGS.DATA;
                this.Packet.ChannelID = (Byte)this._channelID;
                this.Packet.DataCount = (Byte)(this.DatagramQueue.Count & 0xFF);
                try
                {
                    this.Packet.Data = this.DatagramQueue.ToArray();
                }
                catch(Exception ex)
                {
                    Debug.WriteLine(ex.Message + " count:" + this.DatagramQueue.Count.ToString());
                }

                return data.SequenceNo;
            }

            public LUDPPacket CreateAckPacket()
            {
                LUDPPacket packet = new LUDPPacket();
                packet.SessionID = this.SessionID;
                packet.Flags = (Byte)LUDP_PACKET_HEADER_FLAGS.ACK;
                packet.ChannelID = (Byte)this._channelID;
                packet.DataCount = 0;
                packet.AckNo = this._ackNo;

                return packet;
            }

            public LUDPDatagram[] RetreveNewDatagram(LUDPPacket packet)
            { 
                List<LUDPDatagram> newPacket = new List<LUDPDatagram>();
                for(int i= 0; i < packet.DataCount; i++)
                {
                    if(this._receivedSequenceNo < packet.Data[i].SequenceNo || (this._receivedSequenceNo > UInt16.MaxValue - 100 && packet.Data[i].SequenceNo < 100))
                    {
                        this._receivedSequenceNo = packet.Data[i].SequenceNo;
                        this._ackNo = this._receivedSequenceNo;
                        newPacket.Add(packet.Data[i]);
                    }
                }
                return newPacket.ToArray();
            }

        }
    }

    public enum PACKET_MANAGEMENT_STATE
    {
        NONE,
        SERVER,
        CONNECTED,
    }

    public enum LUDP_PACKET_HEADER_FLAGS
    {
        HELLO = 1,
        ACK = 2,
        DATA = 4,
    }
    public enum LUDP_PACKET_HEADER_CHANNEL
    {
        COMMANDS = 1,
        STREAM = 2,
        EVENT = 4,
    }

    public class LUDPPacket
    {
        // Header
        public Byte Flags { get; set; }
        public Byte SessionID { get; set; }
        public Byte ChannelID { get; set; }
        public Byte DataCount { get; set; }
        public UInt16 AckNo { get; set; }
        // Datagram
        public LUDPDatagram[] Data { get; set; }
    }

    public class LUDPDatagram
    {
        // Header
        public UInt16 SequenceNo { get; set; }
        public UInt16 DataSize { get; set; }
        // Datagram
        public Byte[] Data { get; set; }
    }
}
