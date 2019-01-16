﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net;
using DSharpPlus.Net.Udp;
using DSharpPlus.Net.WebSocket;
using DSharpPlus.VoiceNext.Codec;
using DSharpPlus.VoiceNext.Entities;
using DSharpPlus.VoiceNext.EventArgs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus.VoiceNext
{
    internal delegate void VoiceDisconnectedEventHandler(DiscordGuild guild);

    /// <summary>
    /// VoiceNext connection to a voice channel.
    /// </summary>
    public sealed class VoiceNextConnection : IDisposable
    {
        /// <summary>
        /// Triggered whenever a user speaks in the connected voice channel.
        /// </summary>
        public event AsyncEventHandler<UserSpeakingEventArgs> UserSpeaking
        {
            add { this._userSpeaking.Register(value); }
            remove { this._userSpeaking.Unregister(value); }
        }
        private AsyncEvent<UserSpeakingEventArgs> _userSpeaking;

        /// <summary>
        /// Triggered whenever a user joins voice in the connected guild.
        /// </summary>
        public event AsyncEventHandler<VoiceUserJoinEventArgs> UserJoined
        {
            add { this._userJoined.Register(value); }
            remove { this._userJoined.Unregister(value); }
        }
        private AsyncEvent<VoiceUserJoinEventArgs> _userJoined;

        /// <summary>
        /// Triggered whenever a user leaves voice in the connected guild.
        /// </summary>
        public event AsyncEventHandler<VoiceUserLeaveEventArgs> UserLeft
        {
            add { this._userLeft.Register(value); }
            remove { this._userLeft.Unregister(value); }
        }
        private AsyncEvent<VoiceUserLeaveEventArgs> _userLeft;

#if !NETSTANDARD1_1
        /// <summary>
        /// Triggered whenever voice data is received from the connected voice channel.
        /// </summary>
        public event AsyncEventHandler<VoiceReceiveEventArgs> VoiceReceived
        {
            add { this._voiceReceived.Register(value); }
            remove { this._voiceReceived.Unregister(value); }
        }
        private AsyncEvent<VoiceReceiveEventArgs> _voiceReceived;
#endif

        /// <summary>
        /// Triggered whenever voice WebSocket throws an exception.
        /// </summary>
        public event AsyncEventHandler<SocketErrorEventArgs> VoiceSocketErrored
        {
            add { this._voiceSocketError.Register(value); }
            remove { this._voiceSocketError.Unregister(value); }
        }
        private AsyncEvent<SocketErrorEventArgs> _voiceSocketError;

        internal event VoiceDisconnectedEventHandler VoiceDisconnected;

        private static DateTimeOffset UnixEpoch { get; } = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private DiscordClient Discord { get; }
        private DiscordGuild Guild { get; }
#if !NETSTANDARD1_1
        private ConcurrentDictionary<uint, AudioSender> TransmittingSSRCs { get; }
#endif

        private BaseUdpClient UdpClient { get; }
        private BaseWebSocketClient VoiceWs { get; set; }
        private Task HeartbeatTask { get; set; }
        private int HeartbeatInterval { get; set; }
        private DateTimeOffset LastHeartbeat { get; set; }

        private CancellationTokenSource TokenSource { get; set; }
        private CancellationToken Token
            => this.TokenSource.Token;

        internal VoiceServerUpdatePayload ServerData { get; set; }
        internal VoiceStateUpdatePayload StateData { get; set; }
        internal bool Resume { get; set; }

        private VoiceNextConfiguration Configuration { get; }
        private Opus Opus { get; set; }
        private Sodium Sodium { get; set; }
        private Rtp Rtp { get; set; }
        private EncryptionMode SelectedEncryptionMode { get; set; }
        private uint Nonce { get; set; } = 0;

        private ushort Sequence { get; set; }
        private uint Timestamp { get; set; }
        private uint SSRC { get; set; }
        private byte[] Key { get; set; }
#if !NETSTANDARD1_1
        private IpEndpoint DiscoveredEndpoint { get; set; }
#endif
        internal ConnectionEndpoint ConnectionEndpoint { get; set; }

        private TaskCompletionSource<bool> ReadyWait { get; set; }
        private bool IsInitialized { get; set; }
        private bool IsDisposed { get; set; }

        private TaskCompletionSource<bool> PlayingWait { get; set; }
        
        private ConcurrentQueue<VoicePacket> PacketQueue { get; }
        private VoiceTransmitStream TransmitStream { get; set; }
        private Task SenderTask { get; set; }
        private CancellationTokenSource SenderTokenSource { get; set; }
        private CancellationToken SenderToken
            => this.SenderTokenSource.Token;

#if !NETSTANDARD1_1
        private Task ReceiverTask { get; set; }
        private CancellationTokenSource ReceiverTokenSource { get; set; }
        private CancellationToken ReceiverToken
            => this.ReceiverTokenSource.Token;
#endif

        /// <summary>
        /// Gets the audio format used by the Opus encoder.
        /// </summary>
        public AudioFormat AudioFormat => this.Configuration.AudioFormat;

        /// <summary>
        /// Gets whether this connection is still playing audio.
        /// </summary>
        public bool IsPlaying 
            => this.PlayingWait != null && !this.PlayingWait.Task.IsCompleted;

        /// <summary>
        /// Gets the websocket round-trip time in ms.
        /// </summary>
        public int Ping 
            => Volatile.Read(ref this._ping);

        private int _ping = 0;

        /// <summary>
        /// Gets the channel this voice client is connected to.
        /// </summary>
        public DiscordChannel Channel { get; internal set; }

        internal VoiceNextConnection(DiscordClient client, DiscordGuild guild, DiscordChannel channel, VoiceNextConfiguration config, VoiceServerUpdatePayload server, VoiceStateUpdatePayload state)
        {
            this.Discord = client;
            this.Guild = guild;
            this.Channel = channel;
#if !NETSTANDARD1_1
            this.TransmittingSSRCs = new ConcurrentDictionary<uint, AudioSender>();
#endif

            this._userSpeaking = new AsyncEvent<UserSpeakingEventArgs>(this.Discord.EventErrorHandler, "VNEXT_USER_SPEAKING");
            this._userJoined = new AsyncEvent<VoiceUserJoinEventArgs>(this.Discord.EventErrorHandler, "VNEXT_USER_JOINED");
            this._userLeft = new AsyncEvent<VoiceUserLeaveEventArgs>(this.Discord.EventErrorHandler, "VNEXT_USER_LEFT");
#if !NETSTANDARD1_1
            this._voiceReceived = new AsyncEvent<VoiceReceiveEventArgs>(this.Discord.EventErrorHandler, "VNEXT_VOICE_RECEIVED");
#endif
            this._voiceSocketError = new AsyncEvent<SocketErrorEventArgs>(this.Discord.EventErrorHandler, "VNEXT_WS_ERROR");
            this.TokenSource = new CancellationTokenSource();

            this.Configuration = config;
            this.Opus = new Opus(this.AudioFormat);
            //this.Sodium = new Sodium();
            this.Rtp = new Rtp();

            this.ServerData = server;
            this.StateData = state;

            var eps = this.ServerData.Endpoint;
            var epi = eps.LastIndexOf(':');
            var eph = string.Empty;
            var epp = 80;
            if (epi != -1)
            {
                eph = eps.Substring(0, epi);
                epp = int.Parse(eps.Substring(epi + 1));
            }
            else
            {
                eph = eps;
            }
            this.ConnectionEndpoint = new ConnectionEndpoint { Hostname = eph, Port = epp };

            this.ReadyWait = new TaskCompletionSource<bool>();
            this.IsInitialized = false;
            this.IsDisposed = false;

            this.PlayingWait = null;
            this.PacketQueue = new ConcurrentQueue<VoicePacket>();

            this.UdpClient = this.Discord.Configuration.UdpClientFactory();
            this.VoiceWs = this.Discord.Configuration.WebSocketClientFactory(this.Discord.Configuration.Proxy);
            this.VoiceWs.Disconnected += this.VoiceWS_SocketClosed;
            this.VoiceWs.MessageReceived += this.VoiceWS_SocketMessage;
            this.VoiceWs.Connected += this.VoiceWS_SocketOpened;
            this.VoiceWs.Errored += this.VoiceWs_SocketErrored;
        }

        ~VoiceNextConnection()
        {
            this.Dispose();
        }

        /// <summary>
        /// Connects to the specified voice channel.
        /// </summary>
        /// <returns>A task representing the connection operation.</returns>
        internal Task ConnectAsync()
        {
            var gwuri = new UriBuilder
            {
                Scheme = "wss",
                Host = this.ConnectionEndpoint.Hostname,
                Query = "encoding=json&v=4"
            };

            return this.VoiceWs.ConnectAsync(gwuri.Uri);
        }

        internal Task ReconnectAsync()
            => this.VoiceWs.DisconnectAsync(new SocketCloseEventArgs(this.Discord));

        internal Task StartAsync()
        {
            // Let's announce our intentions to the server
            var vdp = new VoiceDispatch();

            if (!this.Resume)
            {
                vdp.OpCode = 0;
                vdp.Payload = new VoiceIdentifyPayload
                {
                    ServerId = this.ServerData.GuildId,
                    UserId = this.StateData.UserId.Value,
                    SessionId = this.StateData.SessionId,
                    Token = this.ServerData.Token
                };
                this.Resume = true;
            }
            else
            {
                vdp.OpCode = 7;
                vdp.Payload = new VoiceIdentifyPayload
                {
                    ServerId = this.ServerData.GuildId,
                    SessionId = this.StateData.SessionId,
                    Token = this.ServerData.Token
                };
            }
            var vdj = JsonConvert.SerializeObject(vdp, Formatting.None);
            this.VoiceWs.SendMessage(vdj);

            return Task.Delay(0);
        }

        internal Task WaitForReadyAsync() 
            => this.ReadyWait.Task;

        internal void PreparePacket(ReadOnlySpan<byte> pcm, ref Memory<byte> target)
        {
            var audioFormat = this.AudioFormat;

            var packetArray = ArrayPool<byte>.Shared.Rent(this.Rtp.CalculatePacketSize(audioFormat.SampleCountToSampleSize(audioFormat.CalculateMaximumFrameSize()), this.SelectedEncryptionMode));
            var packet = packetArray.AsSpan();

            this.Rtp.EncodeHeader(this.Sequence, this.Timestamp, this.SSRC, packet);
            var opus = packet.Slice(Rtp.HeaderSize, pcm.Length);
            this.Opus.Encode(pcm, ref opus);
            
            this.Sequence++;
            this.Timestamp += (uint)audioFormat.CalculateFrameSize(audioFormat.CalculateSampleDuration(pcm.Length));

            Span<byte> nonce = stackalloc byte[Sodium.NonceSize];
            switch (this.SelectedEncryptionMode)
            {
                case EncryptionMode.XSalsa20_Poly1305:
                    this.Sodium.GenerateNonce(packet.Slice(0, Rtp.HeaderSize), nonce);
                    break;

#if !NETSTANDARD1_1
                case EncryptionMode.XSalsa20_Poly1305_Suffix:
                    this.Sodium.GenerateNonce(nonce);
                    break;
#endif

                case EncryptionMode.XSalsa20_Poly1305_Lite:
                    this.Sodium.GenerateNonce(this.Nonce++, nonce);
                    break;

                default:
                    ArrayPool<byte>.Shared.Return(packetArray);
                    throw new Exception("Unsupported encryption mode.");
            }

            Span<byte> encrypted = stackalloc byte[Sodium.CalculateTargetSize(opus)];
            this.Sodium.Encrypt(opus, encrypted, nonce);
            encrypted.CopyTo(packet.Slice(Rtp.HeaderSize));
            packet = packet.Slice(0, this.Rtp.CalculatePacketSize(encrypted.Length, this.SelectedEncryptionMode));
            this.Sodium.AppendNonce(nonce, packet, this.SelectedEncryptionMode);

            target = target.Slice(0, packet.Length);
            packet.CopyTo(target.Span);
            ArrayPool<byte>.Shared.Return(packetArray);
        }

        internal void EnqueuePacket(VoicePacket packet)
            => this.PacketQueue.Enqueue(packet);

        private async Task VoiceSenderTask()
        {
            var token = this.SenderToken;
            var client = this.UdpClient;
            var queue = this.PacketQueue;

            var synchronizerTicks = (double)Stopwatch.GetTimestamp();
            var synchronizerResolution = (Stopwatch.Frequency * 0.005);
            var tickResolution = 10_000_000.0 / Stopwatch.Frequency;
            this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Timer accuracy: {Stopwatch.Frequency.ToString("#,##0", CultureInfo.InvariantCulture)}/{synchronizerResolution.ToString(CultureInfo.InvariantCulture)} (high resolution? {Stopwatch.IsHighResolution})", DateTime.Now);

            while (!token.IsCancellationRequested)
            {
                var hasPacket = queue.TryDequeue(out var packet);

                byte[] packetArray = null;
                if (hasPacket)
                {
                    if (this.PlayingWait == null || this.PlayingWait.Task.IsCompleted)
                        this.PlayingWait = new TaskCompletionSource<bool>();

                    packetArray = packet.Bytes.ToArray();
                }

                // Provided by Laura#0090 (214796473689178133); this is Python, but adaptable:
                // 
                // delay = max(0, self.delay + ((start_time + self.delay * loops) + - time.time()))
                // 
                // self.delay
                //   sample size
                // start_time
                //   time since streaming started
                // loops
                //   number of samples sent
                // time.time()
                //   DateTime.Now

                var durationModifier = hasPacket ? packet.MillisecondDuration / 5 : 4;
                var cts = Math.Max(Stopwatch.GetTimestamp() - synchronizerTicks, 0);
                if (cts < synchronizerResolution * durationModifier)
                    await Task.Delay(TimeSpan.FromTicks((long)(((synchronizerResolution * durationModifier) - cts) * tickResolution))).ConfigureAwait(false);

                synchronizerTicks += synchronizerResolution * durationModifier;

                if (!hasPacket)
                    continue;
                    
                this.SendSpeaking(true);
                await this.UdpClient.SendAsync(packetArray, packetArray.Length).ConfigureAwait(false);

                if (!packet.IsSilence && queue.Count == 0)
                {
                    var nullpcm = new byte[this.AudioFormat.CalculateSampleSize(20)];
                    for (var i = 0; i < 3; i++)
                    {
                        var nullpacket = new byte[nullpcm.Length];
                        var nullpacketmem = nullpacket.AsMemory();

                        this.PreparePacket(nullpcm, ref nullpacketmem);
                        this.EnqueuePacket(new VoicePacket(nullpacketmem, 20, true));
                    }
                }
                else if (queue.Count == 0)
                {
                    this.SendSpeaking(false);
                    this.PlayingWait?.SetResult(true);
                }
            }
        }

#if !NETSTANDARD1_1
        private async Task VoiceReceiverTask()
        {/*
            var token = this.ReceiverToken;
            var client = this.UdpClient;
            while (!token.IsCancellationRequested)
            {
                if (client.DataAvailable <= 0)
                    continue;

                byte[] data = null, header = null;
                ushort seq = 0;
                uint ts = 0, ssrc = 0;
                try
                {
                    data = await client.ReceiveAsync().ConfigureAwait(false);

                    header = new byte[RtpCodec.SIZE_HEADER];
                    data = this.Rtp.Decode(data, header);

                    var nonce = this.Rtp.MakeNonce(header);
                    data = this.Sodium.Decode(data, nonce, this.Key);

                    // following is thanks to code from Eris
                    // https://github.com/abalabahaha/eris/blob/master/lib/voice/VoiceConnection.js#L623
                    var doff = 0;
                    this.Rtp.Decode(header, out seq, out ts, out ssrc, out var has_ext);
                    if (has_ext)
                    {
                        if (data[0] == 0xBE && data[1] == 0xDE)
                        {
                            // RFC 5285, 4.2 One-Byte header
                            // http://www.rfcreader.com/#rfc5285_line186

                            var hlen = data[2] << 8 | data[3];
                            var i = 4;
                            for (; i < hlen + 4; i++)
                            {
                                var b = data[i];
                                // This is unused(?)
                                //var id = (b >> 4) & 0x0F;
                                var len = (b & 0x0F) + 1;
                                i += len;
                            }
                            while (data[i] == 0)
                                i++;
                            doff = i;
                        }
                        // TODO: consider implementing RFC 5285, 4.3. Two-Byte Header
                    }

                    data = this.Opus.Decode(data, doff, data.Length - doff);
                }
                catch { continue; }

                // TODO: wait for ssrc map?
                DiscordUser user = null;
                if (this.SSRCMap.ContainsKey(ssrc))
                {
                    var id = this.SSRCMap[ssrc];
                    if (this.Guild != null)
                        user = this.Guild._members.FirstOrDefault(xm => xm.Id == id) ?? await this.Guild.GetMemberAsync(id).ConfigureAwait(false);

                    if (user == null)
                        user = this.Discord.InternalGetCachedUser(id);

                    if (user == null)
                        user = new DiscordUser { Discord = this.Discord, Id = id };
                }

                await this._voiceReceived.InvokeAsync(new VoiceReceiveEventArgs(this.Discord)
                {
                    SSRC = ssrc,
                    Voice = new ReadOnlyCollection<byte>(data),
                    VoiceLength = 20,
                    User = user
                }).ConfigureAwait(false);
            }
        */}
#endif

        /// <summary>
        /// Sends a speaking status to the connected voice channel.
        /// </summary>
        /// <param name="speaking">Whether the current user is speaking or not.</param>
        /// <returns>A task representing the sending operation.</returns>
        public void SendSpeaking(bool speaking = true)
        {
            if (!this.IsInitialized)
                throw new InvalidOperationException("The connection is not initialized");

            var pld = new VoiceDispatch
            {
                OpCode = 5,
                Payload = new VoiceSpeakingPayload
                {
                    Speaking = speaking,
                    Delay = 0
                }
            };

            var plj = JsonConvert.SerializeObject(pld, Formatting.None);
            this.VoiceWs.SendMessage(plj);
        }

        /// <summary>
        /// Gets a transmit stream for this connection, optionally specifying a packet size to use with the stream. If a stream is already configured, it will return the existing one.
        /// </summary>
        /// <param name="sampleDuration">Duration, in ms, to use for audio packets.</param>
        /// <returns>Transmit stream.</returns>
        public VoiceTransmitStream GetTransmitStream(int sampleDuration = 20)
        {
            if (!AudioFormat.AllowedSampleDurations.Contains(sampleDuration))
                throw new ArgumentOutOfRangeException(nameof(sampleDuration), "Invalid PCM sample duration specified.");

            if (this.TransmitStream == null)
                this.TransmitStream = new VoiceTransmitStream(this, sampleDuration);

            return this.TransmitStream;
        }

        /// <summary>
        /// Asynchronously waits for playback to be finished. Playback is finished when speaking = false is signalled.
        /// </summary>
        /// <returns>A task representing the waiting operation.</returns>
        public async Task WaitForPlaybackFinishAsync()
        {
            if (this.PlayingWait != null)
                await this.PlayingWait.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Disconnects and disposes this voice connection.
        /// </summary>
        public void Disconnect() 
            => this.Dispose();

        /// <summary>
        /// Disconnects and disposes this voice connection.
        /// </summary>
        public void Dispose()
        {
            if (this.IsDisposed)
                return;

            this.IsDisposed = true;
            this.IsInitialized = false;
            this.TokenSource.Cancel();
            this.SenderTokenSource.Cancel();
#if !NETSTANDARD1_1
            if (this.Configuration.EnableIncoming)
                this.ReceiverTokenSource.Cancel();
#endif

            try
            {
                this.VoiceWs.DisconnectAsync(null).ConfigureAwait(false).GetAwaiter().GetResult();
                this.UdpClient.Close();
            }
            catch (Exception)
            { }

            this.Opus?.Dispose();
            this.Opus = null;
            this.Sodium?.Dispose();
            this.Sodium = null;
            this.Rtp?.Dispose();
            this.Rtp = null;

            if (this.VoiceDisconnected != null)
                this.VoiceDisconnected(this.Guild);
        }

        private async Task Heartbeat()
        {
            await Task.Yield();

            var token = this.Token;
            while (true)
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    var dt = DateTime.Now;
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "Sent heartbeat", dt);

                    var hbd = new VoiceDispatch
                    {
                        OpCode = 3,
                        Payload = UnixTimestamp(dt)
                    };
                    var hbj = JsonConvert.SerializeObject(hbd);
                    this.VoiceWs.SendMessage(hbj);

                    this.LastHeartbeat = dt;
                    await Task.Delay(this.HeartbeatInterval).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task Stage1(VoiceReadyPayload voiceReady)
        {
#if !NETSTANDARD1_1
            // IP Discovery
            this.UdpClient.Setup(this.ConnectionEndpoint);

            var pck = new byte[70];
            PreparePacket(pck);
            await this.UdpClient.SendAsync(pck, pck.Length).ConfigureAwait(false);

            var ipd = await this.UdpClient.ReceiveAsync().ConfigureAwait(false);
            ReadPacket(ipd, out var ip, out var port);
            this.DiscoveredEndpoint = new IpEndpoint
            {
                Address = ip,
                Port = port
            };
            
            void PreparePacket(byte[] packet)
            {
                var ssrc = this.SSRC;
                var packetSpan = packet.AsSpan();
                MemoryMarshal.Write(packetSpan, ref ssrc);
                Helpers.ZeroFill(packetSpan);
            }
            
            void ReadPacket(byte[] packet, out System.Net.IPAddress decodedIp, out ushort decodedPort)
            {
                var packetSpan = packet.AsSpan();

                var ipString = new UTF8Encoding(false).GetString(packet, 4, 64 /* 70 - 6 */).TrimEnd('\0');
                decodedIp = System.Net.IPAddress.Parse(ipString);

                decodedPort = MemoryMarshal.Read<ushort>(packetSpan.Slice(68 /* 70 - 2 */));
            }
#endif

            // Select voice encryption mode
            var selectedEncryptionMode = Sodium.SelectMode(voiceReady.Modes);
            this.SelectedEncryptionMode = selectedEncryptionMode.Value;

            // Ready
            this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Selected encryption mode: {selectedEncryptionMode.Key}", DateTime.Now);
            var vsp = new VoiceDispatch
            {
                OpCode = 1,
                Payload = new VoiceSelectProtocolPayload
                {
                    Protocol = "udp",
                    Data = new VoiceSelectProtocolPayloadData
                    {
#if !NETSTANDARD1_1
                        Address = this.DiscoveredEndpoint.Address.ToString(),
                        Port = (ushort)this.DiscoveredEndpoint.Port,
#else
                        Address = "0.0.0.0",
                        Port = 0,
#endif
                        Mode = selectedEncryptionMode.Key
                    }
                }
            };
            var vsj = JsonConvert.SerializeObject(vsp, Formatting.None);
            this.VoiceWs.SendMessage(vsj);

            this.SenderTokenSource = new CancellationTokenSource();
            this.SenderTask = Task.Run(this.VoiceSenderTask, this.SenderToken);

#if !NETSTANDARD1_1
            if (this.Configuration.EnableIncoming)
            {
                this.ReceiverTokenSource = new CancellationTokenSource();
                this.ReceiverTask = Task.Run(this.VoiceReceiverTask, this.ReceiverToken);
            }
#endif
        }

        private Task Stage2(VoiceSessionDescriptionPayload voiceSessionDescription)
        {
            this.SelectedEncryptionMode = Sodium.SupportedModes[voiceSessionDescription.Mode.ToLowerInvariant()];
            this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Discord updated encryption mode: {this.SelectedEncryptionMode}", DateTime.Now);

            this.IsInitialized = true;
            this.ReadyWait.SetResult(true);

            return Task.Delay(0);
        }

        private async Task HandleDispatch(JObject jo)
        {
            var opc = (int)jo["op"];
            var opp = jo["d"] as JObject;

            switch (opc)
            {
                case 2: // READY
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP2 received", DateTime.Now);
                    var vrp = opp.ToObject<VoiceReadyPayload>();
                    this.SSRC = vrp.SSRC;
                    this.ConnectionEndpoint = new ConnectionEndpoint(this.ConnectionEndpoint.Hostname, vrp.Port);
                    // this is not the valid interval
                    // oh, discord
                    //this.HeartbeatInterval = vrp.HeartbeatInterval;
                    this.HeartbeatTask = Task.Run(this.Heartbeat);
                    await this.Stage1(vrp).ConfigureAwait(false);
                    break;

                case 4: // SESSION_DESCRIPTION
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP4 received", DateTime.Now);
                    var vsd = opp.ToObject<VoiceSessionDescriptionPayload>();
                    this.Key = vsd.SecretKey;
                    this.Sodium = new Sodium(this.Key.AsMemory());
                    await this.Stage2(vsd).ConfigureAwait(false);
                    break;

                case 5: // SPEAKING
                    // Don't spam OP5
                    //this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP5 received", DateTime.Now);
                    var spd = opp.ToObject<VoiceSpeakingPayload>();
                    var spk = new UserSpeakingEventArgs(this.Discord)
                    {
                        Speaking = spd.Speaking,
                        SSRC = spd.SSRC.Value,
                        User = this.Discord.InternalGetCachedUser(spd.UserId.Value)
                    };

#if !NETSTANDARD1_1
                    if (spk.User != null && this.TransmittingSSRCs.TryGetValue(spk.SSRC, out var txssrc5) && txssrc5.Id == 0)
                    {
                        txssrc5.User = spk.User;
                    }
                    else
                    {
                        var opus = this.Opus.CreateDecoder();
                        var vtx = new AudioSender(spk.SSRC, opus)
                        {
                            User = await this.Discord.GetUserAsync(spd.UserId.Value).ConfigureAwait(false)
                        };

                        if (!this.TransmittingSSRCs.TryAdd(spk.SSRC, vtx))
                            this.Opus.DestroyDecoder(opus);
                    }
#endif

                    await this._userSpeaking.InvokeAsync(spk).ConfigureAwait(false);
                    break;
                    
                case 6: // HEARTBEAT ACK
                    var dt = DateTime.Now;
                    var ping = (int)(dt - this.LastHeartbeat).TotalMilliseconds;
                    Volatile.Write(ref this._ping, ping);
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Received voice heartbeat ACK, ping {ping.ToString("#,##0", CultureInfo.InvariantCulture)}ms", dt);
                    this.LastHeartbeat = dt;
                    break;

                case 8: // HELLO
                    // this sends a heartbeat interval that we need to use for heartbeating
                    this.HeartbeatInterval = opp["heartbeat_interval"].ToObject<int>();
                    break;

                case 9: // RESUMED
                    this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", "OP9 received", DateTime.Now);
                    this.HeartbeatTask = Task.Run(this.Heartbeat);
                    break;

                case 12: // CLIENT_CONNECTED
                    var ujpd = opp.ToObject<VoiceUserJoinPayload>();
                    var usrj = await this.Discord.GetUserAsync(ujpd.UserId).ConfigureAwait(false);

#if !NETSTANDARD1_1
                    {
                        var opus = this.Opus.CreateDecoder();
                        var vtx = new AudioSender(ujpd.SSRC, opus)
                        {
                            User = usrj
                        };

                        if (!this.TransmittingSSRCs.TryAdd(vtx.SSRC, vtx))
                            this.Opus.DestroyDecoder(opus);
                    }
#endif

                    await this._userJoined.InvokeAsync(new VoiceUserJoinEventArgs(this.Discord) { User = usrj }).ConfigureAwait(false);
                    break;

                case 13: // CLIENT_DISCONNECTED
                    var ulpd = opp.ToObject<VoiceUserLeavePayload>();

#if !NETSTANDARD1_1
                    var txssrc = this.TransmittingSSRCs.FirstOrDefault(x => x.Value.Id == ulpd.UserId);
                    if (this.TransmittingSSRCs.ContainsKey(txssrc.Key))
                    {
                        this.TransmittingSSRCs.TryRemove(txssrc.Key, out var txssrc13);
                        this.Opus.DestroyDecoder(txssrc13.Decoder);
                    }
#endif

                    var usrl = await this.Discord.GetUserAsync(ulpd.UserId).ConfigureAwait(false);
                    await this._userLeft.InvokeAsync(new VoiceUserLeaveEventArgs(this.Discord) { User = usrl }).ConfigureAwait(false);
                    break;

                default:
                    this.Discord.DebugLogger.LogMessage(LogLevel.Warning, "VoiceNext", $"Unknown opcode received: {opc.ToString(CultureInfo.InvariantCulture)}", DateTime.Now);
                    break;
            }
        }

        private async Task VoiceWS_SocketClosed(SocketCloseEventArgs e)
        {
            this.Discord.DebugLogger.LogMessage(LogLevel.Debug, "VoiceNext", $"Voice socket closed ({e.CloseCode.ToString(CultureInfo.InvariantCulture)}, '{e.CloseMessage}')", DateTime.Now);

            // generally this should not be disposed on all disconnects, only on requested ones
            // or something
            // otherwise problems happen
            //this.Dispose();

            if (e.CloseCode == 4006 || e.CloseCode == 4009)
                this.Resume = false;

            if (!this.IsDisposed)
            {
                this.TokenSource.Cancel();
                this.TokenSource = new CancellationTokenSource();
                this.VoiceWs = this.Discord.Configuration.WebSocketClientFactory(this.Discord.Configuration.Proxy);
                this.VoiceWs.Disconnected += this.VoiceWS_SocketClosed;
                this.VoiceWs.MessageReceived += this.VoiceWS_SocketMessage;
                this.VoiceWs.Connected += this.VoiceWS_SocketOpened;
                await this.ConnectAsync().ConfigureAwait(false);
            }
        }

        private Task VoiceWS_SocketMessage(SocketMessageEventArgs e) 
            => this.HandleDispatch(JObject.Parse(e.Message));

        private Task VoiceWS_SocketOpened() 
            => this.StartAsync();

        private Task VoiceWs_SocketErrored(SocketErrorEventArgs e) 
            => this._voiceSocketError.InvokeAsync(new SocketErrorEventArgs(this.Discord) { Exception = e.Exception });

        private static uint UnixTimestamp(DateTime dt)
        {
            var ts = dt - UnixEpoch;
            var sd = ts.TotalSeconds;
            var si = (uint)sd;
            return si;
        }
    }
}

// Naam you still owe me those noodles :^)
// I remember
// Alexa, how much is shipping to emzi
// NL -> PL is 18.50€ for packages <=2kg it seems (https://www.postnl.nl/en/mail-and-parcels/parcels/international-parcel/)