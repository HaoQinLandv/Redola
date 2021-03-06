﻿using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Logrila.Logging;
using Redola.ActorModel.Framing;

namespace Redola.ActorModel
{
    public class ActorConnectorChannel : IActorChannel
    {
        private ILog _log = Logger.Get<ActorConnectorChannel>();
        private ActorIdentity _localActor;
        private ActorIdentity _remoteActor;
        private ActorTransportConnector _connector;
        private ActorChannelConfiguration _channelConfiguration;

        private readonly SemaphoreSlim _keepAliveLocker = new SemaphoreSlim(1, 1);
        private KeepAliveTracker _keepAliveTracker;
        private Timer _keepAliveTimeoutTimer;

        public ActorConnectorChannel(
            ActorIdentity localActor,
            ActorTransportConnector remoteConnector,
            ActorChannelConfiguration channelConfiguration)
        {
            if (localActor == null)
                throw new ArgumentNullException("localActor");
            if (remoteConnector == null)
                throw new ArgumentNullException("remoteConnector");
            if (channelConfiguration == null)
                throw new ArgumentNullException("channelConfiguration");

            _localActor = localActor;
            _connector = remoteConnector;
            _channelConfiguration = channelConfiguration;

            _keepAliveTracker = KeepAliveTracker.Create(KeepAliveInterval, new TimerCallback((s) => OnKeepAlive()));
            _keepAliveTimeoutTimer = new Timer(new TimerCallback((s) => OnKeepAliveTimeout()), null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool Active
        {
            get
            {
                if (_connector == null)
                    return false;
                else
                    return _connector.IsConnected && IsHandshaked;
            }
        }

        public IPEndPoint ConnectToEndPoint
        {
            get
            {
                return _connector.ConnectToEndPoint;
            }
        }

        public bool IsHandshaked { get; private set; }

        public void Open()
        {
            Open(TimeSpan.FromSeconds(5));
        }

        public void Open(TimeSpan timeout)
        {
            try
            {
                if (_connector.IsConnected)
                    return;

                _connector.Connected += OnConnected;
                _connector.Disconnected += OnDisconnected;

                _connector.Connect(timeout);

                OnOpen();
            }
            catch (TimeoutException)
            {
                _log.ErrorFormat("Connect remote [{0}] timeout with [{1}].", this.ConnectToEndPoint, timeout);
                Close();
            }
        }

        public void Close()
        {
            try
            {
                if (_keepAliveTracker != null)
                {
                    _keepAliveTracker.Dispose();
                }
                if (_keepAliveTimeoutTimer != null)
                {
                    _keepAliveTimeoutTimer.Dispose();
                }

                _connector.Connected -= OnConnected;
                _connector.Disconnected -= OnDisconnected;
                _connector.DataReceived -= OnDataReceived;

                if (_connector.IsConnected)
                {
                    _connector.Disconnect();
                }

                if (Disconnected != null)
                {
                    Disconnected(this, new ActorDisconnectedEventArgs(this.ConnectToEndPoint.ToString(), _remoteActor));
                }

                _log.InfoFormat("Disconnected with remote [{0}], SessionKey[{1}].", _remoteActor, this.ConnectToEndPoint);
            }
            finally
            {
                _remoteActor = null;
                IsHandshaked = false;
                OnClose();
            }
        }

        protected virtual void OnOpen()
        {
        }

        protected virtual void OnClose()
        {
        }

        private void Handshake()
        {
            Handshake(TimeSpan.FromSeconds(5));
        }

        private void Handshake(TimeSpan timeout)
        {
            var actorHandshakeRequestData = _channelConfiguration.FrameBuilder.ControlFrameDataEncoder.EncodeFrameData(_localActor);
            var actorHandshakeRequest = new HelloFrame(actorHandshakeRequestData);
            var actorHandshakeRequestBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorHandshakeRequest);

            ManualResetEventSlim waitingHandshaked = new ManualResetEventSlim(false);
            ActorTransportDataReceivedEventArgs handshakeResponseEvent = null;
            EventHandler<ActorTransportDataReceivedEventArgs> onHandshaked =
                (s, e) =>
                {
                    handshakeResponseEvent = e;
                    waitingHandshaked.Set();
                };

            _connector.DataReceived += onHandshaked;
            _log.InfoFormat("Handshake request from local actor [{0}].", _localActor);
            _connector.BeginSend(actorHandshakeRequestBuffer);

            bool handshaked = waitingHandshaked.Wait(timeout);
            _connector.DataReceived -= onHandshaked;
            waitingHandshaked.Dispose();

            if (handshaked && handshakeResponseEvent != null)
            {
                ActorFrameHeader actorHandshakeResponseFrameHeader = null;
                bool isHeaderDecoded = _channelConfiguration.FrameBuilder.TryDecodeFrameHeader(
                    handshakeResponseEvent.Data, handshakeResponseEvent.DataOffset, handshakeResponseEvent.DataLength,
                    out actorHandshakeResponseFrameHeader);
                if (isHeaderDecoded && actorHandshakeResponseFrameHeader.OpCode == OpCode.Welcome)
                {
                    byte[] payload;
                    int payloadOffset;
                    int payloadCount;
                    _channelConfiguration.FrameBuilder.DecodePayload(
                        handshakeResponseEvent.Data, handshakeResponseEvent.DataOffset, actorHandshakeResponseFrameHeader,
                        out payload, out payloadOffset, out payloadCount);
                    var actorHandshakeResponseData = _channelConfiguration.FrameBuilder.ControlFrameDataDecoder.DecodeFrameData<ActorIdentity>(
                        payload, payloadOffset, payloadCount);

                    _remoteActor = actorHandshakeResponseData;
                    _log.InfoFormat("Handshake response from remote actor [{0}].", _remoteActor);
                }

                if (_remoteActor == null)
                {
                    _log.ErrorFormat("Handshake with remote [{0}] failed, invalid actor description.", this.ConnectToEndPoint);
                    Close();
                }
                else
                {
                    _log.InfoFormat("Handshake with remote [{0}] successfully, RemoteActor[{1}].", this.ConnectToEndPoint, _remoteActor);

                    IsHandshaked = true;
                    if (Connected != null)
                    {
                        Connected(this, new ActorConnectedEventArgs(this.ConnectToEndPoint.ToString(), _remoteActor));
                    }

                    _connector.DataReceived += OnDataReceived;
                    _keepAliveTracker.StartTimer();
                }
            }
            else
            {
                _log.ErrorFormat("Handshake with remote [{0}] timeout [{1}].", this.ConnectToEndPoint, timeout);
                Close();
            }
        }

        protected virtual void OnConnected(object sender, ActorTransportConnectedEventArgs e)
        {
            Task.Factory.StartNew(() => { Handshake(); }, TaskCreationOptions.PreferFairness);
        }

        protected virtual void OnDisconnected(object sender, ActorTransportDisconnectedEventArgs e)
        {
            Close();
        }

        protected virtual void OnDataReceived(object sender, ActorTransportDataReceivedEventArgs e)
        {
            _keepAliveTracker.OnDataReceived();

            ActorFrameHeader actorKeepAliveRequestFrameHeader = null;
            bool isHeaderDecoded = _channelConfiguration.FrameBuilder.TryDecodeFrameHeader(
                e.Data, e.DataOffset, e.DataLength,
                out actorKeepAliveRequestFrameHeader);
            if (isHeaderDecoded && actorKeepAliveRequestFrameHeader.OpCode == OpCode.Ping)
            {
                _log.DebugFormat("KeepAlive receive request from remote actor [{0}] to local actor [{1}].", _remoteActor, _localActor);

                var actorKeepAliveResponse = new PongFrame();
                var actorKeepAliveResponseBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorKeepAliveResponse);

                _log.DebugFormat("KeepAlive send response from local actor [{0}] to remote actor [{1}].", _localActor, _remoteActor);
                _connector.BeginSend(actorKeepAliveResponseBuffer);
            }
            else if (isHeaderDecoded && actorKeepAliveRequestFrameHeader.OpCode == OpCode.Pong)
            {
                _log.DebugFormat("KeepAlive receive response from remote actor [{0}] to local actor [{1}].", _remoteActor, _localActor);
                StopKeepAliveTimeoutTimer();
            }
            else
            {
                if (DataReceived != null)
                {
                    DataReceived(this, new ActorDataReceivedEventArgs(this.ConnectToEndPoint.ToString(), _remoteActor, e.Data, e.DataOffset, e.DataLength));
                }
            }
        }

        public event EventHandler<ActorConnectedEventArgs> Connected;
        public event EventHandler<ActorDisconnectedEventArgs> Disconnected;
        public event EventHandler<ActorDataReceivedEventArgs> DataReceived;

        public void Send(string actorType, string actorName, byte[] data)
        {
            Send(actorType, actorName, data, 0, data.Length);
        }

        public void Send(string actorType, string actorName, byte[] data, int offset, int count)
        {
            var actorKey = ActorIdentity.GetKey(actorType, actorName);

            if (_remoteActor == null)
                throw new InvalidOperationException(
                    string.Format("The remote actor has not been connected, Type[{0}], Name[{1}].", actorType, actorName));
            if (_remoteActor.GetKey() != actorKey)
                throw new InvalidOperationException(
                    string.Format("Remote actor key not matched, [{0}]:[{1}].", _remoteActor.GetKey(), actorKey));

            _connector.Send(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public void BeginSend(string actorType, string actorName, byte[] data)
        {
            BeginSend(actorType, actorName, data, 0, data.Length);
        }

        public void BeginSend(string actorType, string actorName, byte[] data, int offset, int count)
        {
            var actorKey = ActorIdentity.GetKey(actorType, actorName);

            if (_remoteActor == null)
                throw new InvalidOperationException(
                    string.Format("The remote actor has not been connected, Type[{0}], Name[{1}].", actorType, actorName));
            if (_remoteActor.GetKey() != actorKey)
                throw new InvalidOperationException(
                    string.Format("Remote actor key not matched, [{0}]:[{1}].", _remoteActor.GetKey(), actorKey));

            _connector.BeginSend(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public void Send(string actorType, byte[] data)
        {
            Send(actorType, data, 0, data.Length);
        }

        public void Send(string actorType, byte[] data, int offset, int count)
        {
            if (_remoteActor == null)
                throw new InvalidOperationException(
                    string.Format("The remote actor has not been connected, Type[{0}].", actorType));
            if (_remoteActor.Type != actorType)
                throw new InvalidOperationException(
                    string.Format("Remote actor type not matched, [{0}]:[{1}].", _remoteActor.Type, actorType));

            _connector.Send(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public void BeginSend(string actorType, byte[] data)
        {
            BeginSend(actorType, data, 0, data.Length);
        }

        public void BeginSend(string actorType, byte[] data, int offset, int count)
        {
            if (_remoteActor == null)
                throw new InvalidOperationException(
                    string.Format("The remote actor has not been connected, Type[{0}].", actorType));
            if (_remoteActor.Type != actorType)
                throw new InvalidOperationException(
                    string.Format("Remote actor type not matched, [{0}]:[{1}].", _remoteActor.Type, actorType));

            _connector.BeginSend(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public IAsyncResult BeginSend(string actorType, string actorName, byte[] data, AsyncCallback callback, object state)
        {
            return BeginSend(actorType, actorName, data, 0, data.Length, callback, state);
        }

        public IAsyncResult BeginSend(string actorType, string actorName, byte[] data, int offset, int count, AsyncCallback callback, object state)
        {
            var actorKey = ActorIdentity.GetKey(actorType, actorName);

            if (_remoteActor == null)
                throw new InvalidOperationException(
                    string.Format("The remote actor has not been connected, Type[{0}], Name[{1}].", actorType, actorName));
            if (_remoteActor.GetKey() != actorKey)
                throw new InvalidOperationException(
                    string.Format("Remote actor key not matched, [{0}]:[{1}].", _remoteActor.GetKey(), actorKey));

            var ar = _connector.BeginSend(data, offset, count, callback, state);
            _keepAliveTracker.OnDataSent();

            return ar;
        }

        public void EndSend(string actorType, string actorName, IAsyncResult asyncResult)
        {
            _connector.EndSend(asyncResult);
        }

        #region Keep Alive

        public TimeSpan KeepAliveInterval { get { return _channelConfiguration.KeepAliveInterval; } }

        public TimeSpan KeepAliveTimeout { get { return _channelConfiguration.KeepAliveTimeout; } }

        private void StartKeepAliveTimeoutTimer()
        {
            _keepAliveTimeoutTimer.Change((int)KeepAliveTimeout.TotalMilliseconds, Timeout.Infinite);
        }

        private void StopKeepAliveTimeoutTimer()
        {
            _keepAliveTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnKeepAliveTimeout()
        {
            _log.ErrorFormat("Keep-alive timer timeout [{0}].", KeepAliveTimeout);
            Close();
        }

        private void OnKeepAlive()
        {
            if (_keepAliveLocker.Wait(0))
            {
                try
                {
                    if (!Active)
                        return;

                    if (_localActor.Type == _remoteActor.Type
                        && _localActor.Name == _remoteActor.Name)
                        return;

                    if (_keepAliveTracker.ShouldSendKeepAlive())
                    {
                        var actorKeepAliveRequest = new PingFrame();
                        var actorKeepAliveRequestBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorKeepAliveRequest);

                        _log.DebugFormat("KeepAlive send request from local actor [{0}] to remote actor [{1}].", _localActor, _remoteActor);

                        _connector.BeginSend(actorKeepAliveRequestBuffer);
                        StartKeepAliveTimeoutTimer();

                        _keepAliveTracker.ResetTimer();
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex.Message, ex);
                    Close();
                }
                finally
                {
                    _keepAliveLocker.Release();
                }
            }
        }

        #endregion
    }
}
