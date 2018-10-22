﻿using System;
using System.Net;
using MumbleProto;
using Version = MumbleProto.Version;
using UnityEngine;
using System.Collections.Generic;

namespace Mumble
{
    public enum SpeakerCreationMode
    {
        ALL, // All users on the server
        IN_ROOM, // All users in the same room
        IN_ROOM_NOT_SERVER_MUTED, // All users in the same room that are not server muted
        IN_ROOM_NO_MUTE // All users in the same room that are not muted by the server and not self muted
    };
    public delegate void UpdateOcbServerNonce(byte[] cryptSetup);
    [Serializable]
    public class DebugValues {
        [Header("Whether to add a random number to the end of the username")]
        public bool UseRandomUsername;
        [Header("Whether to stream the audio back to Unity directly")]
        public bool UseLocalLoopback;
        [Header("Whether to use a synthetic audio source of an audio sample")]
        public bool UseSyntheticSource;
        [Header("Create a graph in the editor displaying the IO")]
        public bool EnableEditorIOGraph;
    }

    public class MumbleClient
    {
        // This sets if we're going to listen to our own audio
        public bool UseLocalLoopBack { get { return _debugValues.UseLocalLoopback; } }
        // This sets if we send synthetic audio instead of a mic audio
        public bool UseSyntheticSource { get { return _debugValues.UseSyntheticSource; } }
        public int NumUDPPacketsSent { get { return _udpConnection.NumPacketsSent; } }
        public int NumUDPPacketsReceieved { get { return _udpConnection.NumPacketsRecv; } }
        public long NumUDPPacketsLost {
            get {
                long numLost = 0;
                foreach (var pair in _audioDecodingBuffers)
                    numLost += pair.Value.NumPacketsLost;
                return numLost;
            }
        }
        public bool ReadyToConnect { get; private set; }
        public bool ConnectionSetupFinished { get; private set; }
        /// <summary>
        /// Methods to create or delete the Unity audio players
        /// These are NOT! Multithread safe, as they can call Unity functions
        /// </summary>
        /// <returns></returns>
        public delegate MumbleAudioPlayer AudioPlayerCreatorMethod(string username, uint session);
        public delegate void AudioPlayerRemoverMethod(uint session, MumbleAudioPlayer audioPlayerToRemove);
        public delegate void OtherUserStateChangedMethod(uint session, UserState updatedDeltaState, UserState fullUserState);
        /// <summary>
        /// Delegate called whenever Mumble changes channels, either by joining a room or
        /// by being moved
        /// </summary>
        /// <param name="newChannelName"></param>
        /// <param name="newChannelID"></param>
        public delegate void OnChannelChangedMethod(Channel channelWereNowIn);

        public OnChannelChangedMethod OnChannelChanged;
        private MumbleTcpConnection _tcpConnection;
        private MumbleUdpConnection _udpConnection;
        private DecodingBufferPool _decodingBufferPool;
        private readonly string _hostName;
        private readonly int _port;
        private ManageAudioSendBuffer _manageSendBuffer;
        private MumbleMicrophone _mumbleMic;
        private readonly AudioPlayerCreatorMethod _audioPlayerCreator;
        private readonly AudioPlayerRemoverMethod _audioPlayerDestroyer;
        private readonly OtherUserStateChangedMethod _otherUserStateChange;
        private readonly int _outputSampleRate;
        private readonly int _outputChannelCount;
        private readonly SpeakerCreationMode _speakerCreationMode;
        // The mute that we're waiting to set
        // Either null, true, or false
        private bool? _pendingMute = null;

        private DebugValues _debugValues;
        // TODO there are data structures that would be a lot faster here
        private readonly Dictionary<uint, UserState> AllUsers = new Dictionary<uint, UserState>();
        private readonly Dictionary<uint, Channel> Channels = new Dictionary<uint, Channel>();
        private readonly Dictionary<UInt32, AudioDecodingBuffer> _audioDecodingBuffers = new Dictionary<uint, AudioDecodingBuffer>();
        private readonly Dictionary<UInt32, MumbleAudioPlayer> _mumbleAudioPlayers = new Dictionary<uint, MumbleAudioPlayer>();

        internal UserState OurUserState { get; private set; }
        internal Version RemoteVersion { get; set; }
        internal CryptSetup CryptSetup { get; set; }
        internal ServerSync ServerSync { get; private set; }
        internal CodecVersion CodecVersion { get; set; }
        internal PermissionQuery PermissionQuery { get; set; }
        internal ServerConfig ServerConfig { get; set; }

        internal int EncoderSampleRate { get; private set; }
        internal int NumSamplesPerOutgoingPacket { get; private set; }

        //The Mumble version of this integration
        public const string ReleaseName = "MumbleUnity";
        public const uint Major = 1;
        public const uint Minor = 2;
        public const uint Patch = 8;

        public MumbleClient(string hostName, int port, AudioPlayerCreatorMethod createMumbleAudioPlayerMethod, AudioPlayerRemoverMethod removeMumbleAudioPlayerMethod, OtherUserStateChangedMethod otherChangeMethod=null, bool async=false, SpeakerCreationMode speakerCreationMode=SpeakerCreationMode.ALL, DebugValues debugVals=null)
        {
            _hostName = hostName;
            _port = port;
            _audioPlayerCreator = createMumbleAudioPlayerMethod;
            _audioPlayerDestroyer = removeMumbleAudioPlayerMethod;
            _speakerCreationMode = speakerCreationMode;
            _otherUserStateChange = otherChangeMethod;

            switch (AudioSettings.outputSampleRate)
            {
                case 8000:
                case 12000:
                case 16000:
                case 24000:
                case 48000:
                    _outputSampleRate = AudioSettings.outputSampleRate;
                    break;
                default:
                    Debug.LogError("Incorrect sample rate of:" + AudioSettings.outputSampleRate + ". It should be 48000 please set this in Edit->Audio->SystemSampleRate");
                    _outputSampleRate = 48000;
                    break;
            }
            Debug.Log("Using output sample rate: " + _outputSampleRate);

            switch (AudioSettings.speakerMode)
            {
                case AudioSpeakerMode.Mono:
                    // TODO sometimes, even though the speaker mode is mono,
                    // on audiofilterread wants two channels
                    _outputChannelCount = 1;
                    break;
                case AudioSpeakerMode.Stereo:
                    _outputChannelCount = 2;
                    break;
                default:
                    Debug.LogError("Unsupported speaker mode " + AudioSettings.speakerMode + " please set this in Edit->Audio->DefaultSpeakerMode to either Mono or Stereo");
                    _outputChannelCount = 2;
                    break;
            }
            Debug.Log("Using output channel count of: " + _outputChannelCount);

            if (debugVals == null)
                debugVals = new DebugValues();
            _debugValues = debugVals;

            if (async)
                Dns.BeginGetHostAddresses(hostName, OnHostRecv, null);
            else
            {
                IPAddress[] addresses = Dns.GetHostAddresses(hostName);
                Init(addresses);
            }
        }
        private void Init(IPAddress[] addresses)
        {
            //Debug.Log("Host addresses recv");
            if (addresses == null || addresses.Length == 0)
            {
                Debug.LogError("Failed to get addresses!");
                throw new ArgumentException(
                    "Unable to retrieve address from specified host name.",
                    _hostName
                    );
            }
            var endpoint = new IPEndPoint(addresses[0], _port);
            _decodingBufferPool = new DecodingBufferPool(_outputSampleRate, _outputChannelCount);
            _udpConnection = new MumbleUdpConnection(endpoint, this);
            _tcpConnection = new MumbleTcpConnection(endpoint, _hostName, _udpConnection.UpdateOcbServerNonce, _udpConnection, this);
            _udpConnection.SetTcpConnection(_tcpConnection);
            _manageSendBuffer = new ManageAudioSendBuffer(_udpConnection, this);
            ReadyToConnect = true;
        }
        private void OnHostRecv(IAsyncResult result)
        {
            IPAddress[] addresses = Dns.EndGetHostAddresses(result);
            Init(addresses);
        }
        internal void AddMumbleMic(MumbleMicrophone newMic)
        {
            _mumbleMic = newMic;
            _mumbleMic.Initialize(this);
            EncoderSampleRate = _mumbleMic.GetCurrentMicSampleRate();

            if (EncoderSampleRate == -1)
                return;
            
            NumSamplesPerOutgoingPacket = MumbleConstants.NUM_FRAMES_PER_OUTGOING_PACKET * EncoderSampleRate / 100;
            _manageSendBuffer.InitForSampleRate(EncoderSampleRate);
        }
        internal PcmArray GetAvailablePcmArray()
        {
            return _manageSendBuffer.GetAvailablePcmArray();
        }
        internal void ReleasePcmArray(PcmArray pcmArray)
        {
            _manageSendBuffer.ReleasePcmArray(pcmArray);
        }
        internal UserState GetUserFromSession(uint session)
        {
            UserState state = null;
            if(!AllUsers.TryGetValue(session, out state))
                Debug.LogWarning("User state not found for session " + session);
            return state;
        }
        internal void AddOrUpdateUser(UserState newUserState)
        {
            bool isOurUser = OurUserState != null && newUserState.Session == OurUserState.Session;

            UserState userState;
            if (!AllUsers.TryGetValue(newUserState.Session, out userState))
            {
                Debug.Log("New audio buffer with session: " + newUserState.Session);
                AllUsers[newUserState.Session] = newUserState;
                userState = newUserState;
            }
            else
            {
                // Copy over the things that have changed
                if (newUserState.ShouldSerializeActor())
                    userState.Actor = newUserState.Actor;
                if(newUserState.ShouldSerializeName())
                    userState.Name = newUserState.Name;
                if (newUserState.ShouldSerializeMute())
                    userState.Mute = newUserState.Mute;
                if (newUserState.ShouldSerializeDeaf())
                    userState.Deaf = newUserState.Deaf;
                if (newUserState.ShouldSerializeSuppress())
                    userState.Suppress = newUserState.Suppress;
                if (newUserState.ShouldSerializeSelfMute())
                    userState.SelfMute = newUserState.SelfMute;
                if (newUserState.ShouldSerializeSelfDeaf())
                    userState.SelfDeaf = newUserState.SelfDeaf;
                if (newUserState.ShouldSerializeComment())
                    userState.Comment = newUserState.Comment;
                if (newUserState.ShouldSerializeChannelId())
                    userState.ChannelId = newUserState.ChannelId;

                //if (newUserState.ShouldSerializeMute() && userState.Mute)
                //Debug.Log("User " + userState.Name + " has been muted");

                // If this is us, and it's signaling that we've changed channels, notify the delegate on the main thread
                if (isOurUser && newUserState.ShouldSerializeChannelId())
                {
                    Debug.Log("Our Channel changed! #" + newUserState.ChannelId);
                    //AllUsers[newUserState.Session].ChannelId = newUserState.ChannelId;
                    EventProcessor.Instance.QueueEvent(() =>
                    {
                        if (OnChannelChanged != null)
                            OnChannelChanged(Channels[newUserState.ChannelId]);
                    });

                    // Re-evaluate all users to see if they need decoding buffers
                    ReevaluateAllDecodingBuffers();
                }
            }

            if (OurUserState == null)
                return;

            // Create the audio player if the user is in the same room, and is not muted
            if(ShouldAddAudioPlayerForUser(userState))
            {
                AddDecodingBuffer(userState);
            }else
            {
                // Otherwise remove the audio decoding buffer and audioPlayer if it exists
                TryRemoveDecodingBuffer(userState.Session);
            }

            // If this isn't our user, send out updates
            if (!isOurUser)
            {
                // We check if otherUserStateChange is null multiple times just to
                // be extra safe
                if(_otherUserStateChange != null)
                {
                    EventProcessor.Instance.QueueEvent(() =>
                    {
                        if (_otherUserStateChange != null)
                            _otherUserStateChange(newUserState.Session, newUserState, userState);
                    });
                }
            }
        }
        private bool ShouldAddAudioPlayerForUser(UserState other)
        {
            switch (_speakerCreationMode)
            {
                case SpeakerCreationMode.ALL:
                    return true;
                case SpeakerCreationMode.IN_ROOM:
                    return other.ChannelId == OurUserState.ChannelId
                        || Channels[OurUserState.ChannelId].DoesShareAudio(Channels[other.ChannelId]);
                case SpeakerCreationMode.IN_ROOM_NOT_SERVER_MUTED:
                    return (other.ChannelId == OurUserState.ChannelId
                        || Channels[OurUserState.ChannelId].DoesShareAudio(Channels[other.ChannelId]))
                        && !other.Mute;
                case SpeakerCreationMode.IN_ROOM_NO_MUTE:
                    return (other.ChannelId == OurUserState.ChannelId
                        || Channels[OurUserState.ChannelId].DoesShareAudio(Channels[other.ChannelId]))
                        && !other.Mute
                        && !other.SelfMute;
                default:
                    return false;
            }
        }
        private void AddDecodingBuffer(UserState userState)
        {
            // Make sure we don't double add
            if (_audioDecodingBuffers.ContainsKey(userState.Session))
                return;
            //Debug.Log("Adding : " + userState.Name + " #" + userState.Session);
            //Debug.Log("Adding decoder session #" + userState.Session);
            AudioDecodingBuffer buffer = _decodingBufferPool.GetDecodingBuffer();
            buffer.Init(userState.Name);
            _audioDecodingBuffers.Add(userState.Session, buffer);
            EventProcessor.Instance.QueueEvent(() =>
            {
                //Debug.Log("Adding audioPlayer session #" + userState.Session);
                // We also create a new audio player for the user
                MumbleAudioPlayer newPlayer = _audioPlayerCreator(userState.Name, userState.Session);
                _mumbleAudioPlayers.Add(userState.Session, newPlayer);
                newPlayer.Initialize(this, userState.Session);
            });
        }
        private void TryRemoveDecodingBuffer(UInt32 session)
        {
            AudioDecodingBuffer buffer;
            if(_audioDecodingBuffers.TryGetValue(session, out buffer))
            {
                //Debug.LogWarning("Removing decoder session #" + session);
                _audioDecodingBuffers.Remove(session);
                _decodingBufferPool.ReturnDecodingBuffer(buffer);

                // We have to check/remove the Audio Player on the main thread
                // This is because we add it on the main thread
                EventProcessor.Instance.QueueEvent(() =>
                {
                    //Debug.Log("Removing audioPlayer session #" + session);
                    MumbleAudioPlayer oldAudioPlayer;
                    if(_mumbleAudioPlayers.TryGetValue(session, out oldAudioPlayer))
                    {
                        _mumbleAudioPlayers.Remove(session);
                        _audioPlayerDestroyer(session, oldAudioPlayer);
                    }
                });
            }
        }
        private void ReevaluateAllDecodingBuffers()
        {
            // TODO we should index more intelligently to speed this up
            foreach(KeyValuePair<uint, UserState> user in AllUsers)
            {
                if(ShouldAddAudioPlayerForUser(user.Value))
                    AddDecodingBuffer(user.Value);
                else
                    TryRemoveDecodingBuffer(user.Key);
            }
        }
        internal void SetServerSync(ServerSync sync)
        {
            ServerSync = sync;
            OurUserState = AllUsers[ServerSync.Session];
            // Now that we know who we are, we can determine which users need decoding buffers
            ReevaluateAllDecodingBuffers();
            ConnectionSetupFinished = true;

            // Do the stuff we were waiting to do
            if (_pendingMute.HasValue)
                SetSelfMute(_pendingMute.Value);
            _pendingMute = null;
        }
        internal void RemoveUser(uint removedUserSession)
        {
            AllUsers.Remove(removedUserSession);
            // Try to remove the audio player and decoding buffer if it exists
            TryRemoveDecodingBuffer(removedUserSession);
        }
        public void Connect(string username, string password)
        {
            if (!ReadyToConnect)
            {
                Debug.LogError("We're not ready to connect yet!");
                return;
            }
            _tcpConnection.StartClient(username, password);
        }
        internal void ConnectUdp()
        {
            _udpConnection.Connect();
        }
        public void Close()
        {
            if(_tcpConnection != null)
                _tcpConnection.Close();
            _tcpConnection = null;
            if(_udpConnection != null)
                _udpConnection.Close();
            _udpConnection = null;
            if(_manageSendBuffer != null)
                _manageSendBuffer.Dispose();
            _manageSendBuffer = null;
        }
        public void SendTextMessage(string textMessage)
        {
            if (OurUserState == null)
                return;
            var msg = new TextMessage
            {
                Message = textMessage,
                ChannelIds = new uint[] { OurUserState.ChannelId },
                Actor = ServerSync.Session
            };
            Debug.Log("Now session length = " + msg.Sessions.Length);

            _tcpConnection.SendMessage(MessageType.TextMessage, msg);
        }
        public void SendVoicePacket(PcmArray floatData)
        {
            // Don't send anything out if we're muted
            if (OurUserState == null
                || OurUserState.Mute)
            {
                ReleasePcmArray(floatData);
                return;
            }
            if(_manageSendBuffer != null)
                _manageSendBuffer.SendVoice(floatData, SpeechTarget.Normal, 0);
        }
        public void ReceiveEncodedVoice(UInt32 session, byte[] data, long sequence, bool isLast)
        {
            //Debug.Log("Adding packet for session: " + session);
            AudioDecodingBuffer decodingBuffer;
            if (_audioDecodingBuffers.TryGetValue(session, out decodingBuffer))
                decodingBuffer.AddEncodedPacket(sequence, data, isLast);
            else
            {
                // This is expected if the user joins a room where people are already talking
                // Buffers will be dropped until the decoding buffer has been created
                Debug.LogWarning("No decoding buffer found for session:" + session);
            }
        }
        public bool HasPlayableAudio(UInt32 session)
        {
            AudioDecodingBuffer decodingBuffer;
            if (_audioDecodingBuffers.TryGetValue(session, out decodingBuffer))
                return decodingBuffer.HasFilledInitialBuffer;
            else
                return false;
        }
        public void LoadArrayWithVoiceData(UInt32 session, float[] pcmArray, int offset, int length)
        {
            if (session == ServerSync.Session && !_debugValues.UseLocalLoopback)
                return;
            //Debug.Log("Will decode for " + session);

            //TODO use bool to show if loading worked or not
            AudioDecodingBuffer decodingBuffer;
            if (_audioDecodingBuffers.TryGetValue(session, out decodingBuffer))
                decodingBuffer.Read(pcmArray, offset, length);
            else
                Debug.LogWarning("Decode buffer not found for session " + session);
        }
        public bool JoinChannel(string channelToJoin)
        {
            if (OurUserState == null)
                return false;
            Channel channel;
            if (!TryGetChannelByName(channelToJoin, out channel))
            {
                Debug.LogError("Channel " + channelToJoin + " not found!");
                return false;
            }
            UserState state = new UserState
            {
                ChannelId = channel.ChannelId,
                Actor = OurUserState.Session,
                Session = OurUserState.Session,
                SelfMute = (_pendingMute != null) ? _pendingMute.Value : OurUserState.SelfMute
            };
            _pendingMute = null;

            Debug.Log("Attempting to join channel Id: " + state.ChannelId);
            _tcpConnection.SendMessage<MumbleProto.UserState>(MessageType.UserState, state);
            return true;
        }
        internal void SetSelfMute(bool mute)
        {
            if (OurUserState == null)
            {
                //Debug.Log("Setting pending self mute: " + mute);
                _pendingMute = mute;
                return;
            }
            _pendingMute = null;

            UserState state = new UserState();
            state.SelfMute = mute;
            OurUserState.SelfMute = mute;
            //Debug.Log("Will set our self mute to: " + mute);
            _tcpConnection.SendMessage<MumbleProto.UserState>(MessageType.UserState, state);
        }
        public bool IsSelfMuted()
        {
            // The default self mute is false
            if (OurUserState == null)
                return false;

            if (_pendingMute.HasValue)
            {
                Debug.Log("Pending mute: " + _pendingMute.HasValue);
                return _pendingMute.Value;
            }

            if (!OurUserState.ShouldSerializeSelfMute())
                return false;

            Debug.Log("Our Self Mute is " + OurUserState.SelfMute);
            return OurUserState.SelfMute;
        }
        private bool TryGetChannelByName(string channelName, out Channel channelState)
        {
            foreach(uint key in Channels.Keys)
            {
                if (Channels[key].Name == channelName)
                {
                    channelState = Channels[key];
                    return true;
                }
                //Debug.Log("Not " + Channels[key].name + " == " + channelName);
            }
            channelState = null;
            return false;
        }
        public string GetCurrentChannel()
        {
            if (Channels == null
                || OurUserState == null)
                return null;
            Channel ourChannel;
            if(Channels.TryGetValue(OurUserState.ChannelId, out ourChannel))
                return ourChannel.Name;

            Debug.LogError("Could not get current channel");
            return null;
        }
        public uint GetOurSession()
        {
            if (OurUserState != null)
                return OurUserState.Session;
            return 0;
        }
        internal void AddChannel(ChannelState channelToAdd)
        {
            // If the channel already exists, just copy over the non-null data
            Channel existingChannel;
            if (Channels.TryGetValue(channelToAdd.ChannelId, out existingChannel))
                existingChannel.UpdateFromState(channelToAdd);
            else
                Channels[channelToAdd.ChannelId] = new Channel(channelToAdd);

            // Update all the channel audio sharing settings
            // We can probably do this less, but we're cautious
            foreach(KeyValuePair<uint, Channel> kvp in Channels)
                kvp.Value.UpdateSharedAudioChannels(Channels);
        }
        internal void RemoveChannel(uint channelIdToRemove)
        {
            if (channelIdToRemove == OurUserState.ChannelId)
                Debug.LogWarning("Removed current channel");
            Channels.Remove(channelIdToRemove);

            // Update all the channel audio sharing settings
            // We can probably do this less, but we're cautious
            foreach(KeyValuePair<uint, Channel> kvp in Channels)
                kvp.Value.UpdateSharedAudioChannels(Channels);
        }
        /// <summary>
        /// Tell the encoder to send the last audio packet, then reset the sequence number
        /// </summary>
        internal void StopSendingVoice()
        {
            if(_manageSendBuffer != null)
                _manageSendBuffer.SendVoiceStopSignal();
        }
        public byte[] GetLatestClientNonce()
        {
            if(_udpConnection != null)
                return _udpConnection.GetLatestClientNonce();
            return null;
        }
        public static int GetNearestSupportedSampleRate(int listedRate)
        {
            int currentBest = -1;
            int currentDifference = int.MaxValue;

            for(int i = 0; i < MumbleConstants.SUPPORTED_SAMPLE_RATES.Length; i++)
            {
                if(Math.Abs(listedRate - MumbleConstants.SUPPORTED_SAMPLE_RATES[i]) < currentDifference)
                {
                    currentBest = MumbleConstants.SUPPORTED_SAMPLE_RATES[i];
                    currentDifference = Math.Abs(listedRate - MumbleConstants.SUPPORTED_SAMPLE_RATES[i]);
                }
            }

            return currentBest;
        }
    }
}