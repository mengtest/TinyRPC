using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using zFramework.TinyRPC.DataModel;

namespace zFramework.TinyRPC
{
    //一个简单的 TCP 服务器
    // 对外：Start 、Stop  、Send
    // 对内：AcceptAsync 、Receive
    // 事件：当握手完成，当用户断线 、当服务器断线
    // 消息结构：size + body（type+content） 4 + 1 + body , type =  0 代表 ping , 1 代表常规 message ， 2 代表 rpc message
    // 会话：Session = TcpClient + lastSendTime + lastReceiveTime 
    public class TCPServer
    {
        internal TcpListener listener;
        internal readonly List<Session> sessions = new List<Session>();
        readonly SynchronizationContext context;
        public event Action<Session> OnClientEstablished;
        public event Action<Session> OnClientDisconnected;
        public event Action<string> OnServerClosed;

        CancellationTokenSource source;

        #region Field Ping
        internal float pingInterval = 2f;
        // 如果pingTimeout秒内没有收到客户端的消息，则断开连接
        // 在 interval = 2 时代表 retry 了 5 次
        internal float pingTimeout = 10f;
        Timer timer;
        #endregion
        public TCPServer(int port)
        {
            context = SynchronizationContext.Current;
            listener = new TcpListener(IPAddress.Any, port);
        }

        public void Start()
        {
            listener.Start();
            source = new CancellationTokenSource();
            Task.Run(() => AcceptAsync(source));
        }
        public void Stop()
        {
            //停服前先断开 Session
            foreach (var session in sessions)
            {
                session?.Close();
            }

            timer?.Dispose();
            source?.Cancel();
            listener?.Stop();
            sessions.Clear();
            OnServerClosed?.Invoke("服务器已关闭");
        }


        private async void AcceptAsync(CancellationTokenSource token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var session = new Session(client, context, true);
                    sessions.Add(session);
                    OnClientEstablished?.Invoke(session);
                    try
                    {
                        _ = Task.Run(session.ReceiveAsync);
                    }
                    catch (Exception e)
                    {
                        session.Close();
                        sessions.Remove(session);
                        OnClientDisconnected?.Invoke(session);
                        Debug.Log($"{nameof(TCPServer)}:  Session is disconnected! \n{session}\n{e}");
                    }
                }
                catch (Exception e)
                {
                    Debug.Log(e);
                }
            }
        }

        public void Boardcast(Message message)
        {
            foreach (var session in sessions)
            {
                Send(session, message);
            }
        }

        public void Send(Session session, Message message)
        {
            try
            {
                session.Send(message);
            }
            catch (Exception)
            {
                //如果消息发送失败，说明客户端已经断开连接，需要移除
                session.Close();
                sessions.Remove(session);
                OnClientDisconnected?.Invoke(session);
            }
        }

        #region Ping Message Handler
        [MessageHandler(MessageType.RPC)]
        private static async Task OnPingRecevied(Session session, Ping request, Ping response)
        {
            response.Id = request.Id;
            response.time = ServerTime;
            await Task.Yield();
        }
        #endregion

        private static long ServerTime => DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    }
}