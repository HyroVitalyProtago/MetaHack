﻿

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.WebRTC;
using UnityEngine;
using WebSocketSharp;

public class WebRTCBackend : NetworkBackend {

  static Queue _actions = new Queue();
  
  RTCOfferOptions OfferOptions = new RTCOfferOptions {
    iceRestart = false,
    offerToReceiveAudio = false,
    offerToReceiveVideo = false
  };

  RTCAnswerOptions AnswerOptions = new RTCAnswerOptions {
    iceRestart = false,
  };
  
  RTCConfiguration GetSelectedSdpSemantics() {
    RTCConfiguration config = default;
    config.iceServers = new RTCIceServer[] {
      new RTCIceServer { urls = new string[] { "stun:stun.l.google.com:19302"} },
      new RTCIceServer { urls = new string[] { "stun:stun1.l.google.com:19302"} },
      new RTCIceServer { urls = new string[] { "stun:stun2.l.google.com:19302"} },
      new RTCIceServer { urls = new string[] { "stun:stun3.l.google.com:19302"} },
    };

    return config;
  }
  
  int _userId;
  WebSocket _webSocket;
  Dictionary<int, RTCPeerConnection> _connections = new Dictionary<int, RTCPeerConnection>();
  Dictionary<int, RTCDataChannel> _channels = new Dictionary<int, RTCDataChannel>();
  public Action<string, int> OnConnectionStateChange;

  public override void Init(string host) {
    Debug.Log("WebRTC Initialize...");
    WebRTC.Initialize();
    
    // if youd need ws for development: https://stackoverflow.com/questions/11768221/firefox-websocket-security-issue
    // firefox about:config enable network.websocket.allowInsecureFromHTTPS during insecure devs
    _webSocket = new WebSocket($"wss://{host}/wrtc");
    _webSocket.OnMessage += OnSignalReceive;
    _webSocket.OnOpen += (sender, args) => Debug.Log(args);
    _webSocket.OnError += (sender, args) => Debug.Log(args.Message);
    _webSocket.OnClose += (sender, args) => Debug.Log(args);
    _webSocket.Connect();
  }

  IEnumerator InitConnection(int userId, bool hosting) {
    Debug.Log("InitConnection");
    var configuration = GetSelectedSdpSemantics();
    var co = new RTCPeerConnection(ref configuration);
    _connections[userId] = co;
    co.OnConnectionStateChange += delegate(RTCPeerConnectionState state) {
      OnConnectionStateChange?.Invoke(state.ToString(), userId);
      switch (state) {
        case RTCPeerConnectionState.New:
          Debug.Log($"New {userId}");
          break;
        case RTCPeerConnectionState.Connecting:
          Debug.Log($"Connecting {userId}");
          break;
        case RTCPeerConnectionState.Connected:
          //OnOpen?.Invoke(userId);
          Debug.Log($"Connected {userId}");
          break;
        case RTCPeerConnectionState.Disconnected:
          Debug.Log($"Disconnected {userId}");
          break;
        case RTCPeerConnectionState.Failed:
          OnError?.Invoke(userId);
          Debug.Log($"Failed {userId}");
          break;
        case RTCPeerConnectionState.Closed:
          OnClose?.Invoke(userId);
          Debug.Log($"Closed {userId}");
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(state), state, null);
      }
    };
    co.OnIceCandidate += delegate(RTCIceCandidate candidate) {
      SignalSend(new IceCandidateMsg { to=userId, candidate=candidate }); // Send the candidate to the remote peer
    };
    co.OnIceConnectionChange += state =>  OnIceConnectionChange(state, userId);
    if (hosting) { // Create the data channel and establish its event listeners
      Debug.Log("host");
      RTCDataChannelInit conf = new RTCDataChannelInit();
      
      RTCDataChannel channel = co.CreateDataChannel("sync", conf);
      _channels[userId] = channel;
      channel.OnOpen = () => {
        OnOpen?.Invoke(userId);
        OnConnectionStateChange?.Invoke("connected", userId);
      };
      channel.OnMessage = (msg) => ReceiveCallback(msg, userId);
      channel.OnClose = () => OnConnectionStateChange?.Invoke("close", userId);

      var op1 = co.CreateOffer(ref OfferOptions);
      yield return op1;
      if (!op1.IsError) {
        var desc = op1.Desc;
        var op2 = co.SetLocalDescription(ref desc);
        yield return op2;
        if (!op2.IsError) {
          Debug.Log($"SetLocalDescription complete");
          SignalSend(new IceHostMsg { to=userId, hostDescription=new RTCSessionDescriptionJson(co.LocalDescription) });
        } else {
          var error = op2.Error;
          Debug.LogError(error);
          //OnSetSessionDescriptionError(ref error);
        }
      } else {
        Debug.LogError(op1.Error);
        //OnCreateSessionDescriptionError(op1.Error);
      }
    } else {
      _connections[userId].OnDataChannel += delegate(RTCDataChannel channel) {
        _channels[userId] = channel;
        channel.OnOpen = () => {
          OnOpen?.Invoke(userId);
          OnConnectionStateChange?.Invoke("connected", userId);
        };
        // FIX OnOpen isn't call when the channel is already opened
        if (channel.ReadyState == RTCDataChannelState.Open) {
          OnOpen?.Invoke(userId);
          OnConnectionStateChange?.Invoke("connected", userId);
        }
        channel.OnMessage = (msg) => ReceiveCallback(msg, userId);
        channel.OnClose = () => OnConnectionStateChange?.Invoke("close", userId);
      };
    }
  }
  
  void OnIceConnectionChange(RTCIceConnectionState state, int userId) {
    switch (state) {
      case RTCIceConnectionState.New:
        Debug.Log($"IceConnectionState: New");
        break;
      case RTCIceConnectionState.Checking:
        Debug.Log($"IceConnectionState: Checking");
        break;
      case RTCIceConnectionState.Closed:
        Debug.Log($"IceConnectionState: Closed");
        break;
      case RTCIceConnectionState.Completed:
        Debug.Log($"IceConnectionState: Completed");
        break;
      case RTCIceConnectionState.Connected:
        Debug.Log($"IceConnectionState: Connected");
        //_webSocket.Send("{\"to\":"+userId+",\"candidate\":{\"candidate\": \"\", \"sdpMLineIndex\": 0, \"sdpMid\": \"0\"}}"); // Send the empty candidate to the remote peer
        break;
      case RTCIceConnectionState.Disconnected:
        Debug.Log($"IceConnectionState: Disconnected");
        break;
      case RTCIceConnectionState.Failed:
        Debug.Log($"IceConnectionState: Failed");
        break;
      case RTCIceConnectionState.Max:
        Debug.Log($"IceConnectionState: Max");
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(state), state, null);
    }
  }

  void SignalSend(IceMsg iceCandidateMsg) {
    _webSocket.Send(JsonConvert.SerializeObject(iceCandidateMsg));
  }
  
  void OnSignalReceive(object o, MessageEventArgs args) {
    Call(() => StartCoroutine(OnSignalReceiveCr(o, args)));
  }
  
  Dictionary<int, bool> _haveReceivedAllCandidates = new Dictionary<int, bool>();
  Dictionary<int, Func<IEnumerator>> _callbackReceivedAllCandidates = new Dictionary<int, Func<IEnumerator>>();

  void HandleAllCandidatesReceived(int userId) {
    _haveReceivedAllCandidates.Add(userId, true);
    if (_callbackReceivedAllCandidates.ContainsKey(userId)) {
      StartCoroutine(_callbackReceivedAllCandidates[userId].Invoke());
    }
  }

  void AddAllCandidatesCallback(int userId, Func<IEnumerator> callback) {
    _callbackReceivedAllCandidates.Add(userId, callback);
    if (_haveReceivedAllCandidates.ContainsKey(userId)) {
      StartCoroutine(callback.Invoke());
    }
  }
  
  IEnumerator OnSignalReceiveCr(object o, MessageEventArgs args) {
    //Debug.Log(args.Data);
    JObject evt = JObject.Parse(args.Data);
    
    // when a new user is connected to the signalling server
    var newConnection = evt["newConnection"];
    if (newConnection != null) {
      yield return InitConnection(newConnection.Value<int>(), true);
    } else {
      // as a remote, there is a need to create the connection before receiving messages
      if (evt["from"] != null) {
        int from = evt["from"].Value<int>();
        if (!_connections.ContainsKey(from)) {
          yield return InitConnection(from, false);
        }
    
        var co = _connections[from];
        if (evt["candidate"] != null) { // candidate exchange
          if (!string.IsNullOrEmpty(evt["candidate"].Value<string>("candidate"))) {
            co.AddIceCandidate(IceCandidateFromJson(evt["candidate"]));
          } else {
            //co.AddIceCandidate(null);
            Debug.Log("All candidates have been received");
            // https://github.com/Unity-Technologies/com.unity.webrtc/issues/539
            // trickle ICE not supported by unity => call offer after receiving all ice candidate
            HandleAllCandidatesReceived(from);
          }
        } else if (evt.GetValue("remoteDescription") != null) { // host get the remote description
          var desc = SessionDescriptionFromJson(evt["remoteDescription"]);
          co.SetRemoteDescription(ref desc);
        } else if (evt.GetValue("hostDescription") != null) { // remote get the host description
          IEnumerator Callback() {
            //Debug.Log("SetRemoteDescription");
            var desc1 = SessionDescriptionFromJson(evt["hostDescription"]);
            var op1 = co.SetRemoteDescription(ref desc1);
            yield return op1;
            if (!op1.IsError) {
              //Debug.Log("CreateAnswer");
              var op2 = co.CreateAnswer(ref AnswerOptions);
              yield return op2;
              if (!op2.IsError) {
                //Debug.Log("SetLocalDescription");
                var desc2 = op2.Desc;
                var op3 = co.SetLocalDescription(ref desc2);
                yield return op3;
                if (!op3.IsError) {
                  SignalSend(new IceRemoteMsg {to = @from, remoteDescription = new RTCSessionDescriptionJson(co.LocalDescription)});
                } else {
                  Debug.LogError(op3.Error);
                  //OnCreateSessionDescriptionError(op1.Error);
                }
              } else {
                Debug.LogError(op2.Error);
                //OnCreateSessionDescriptionError(op1.Error);
              }
            } else {
              Debug.LogError(op1.Error);
              //OnCreateSessionDescriptionError(op1.Error);
            }
          }

          AddAllCandidatesCallback(from, Callback);
        } 
      }
    }
  }

  Dictionary<string, Action<JObject, int>> _listeners = new Dictionary<string, Action<JObject, int>>();
  void ReceiveCallback(byte[] bytes, int userId) {
    string msg = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
    //Debug.Log(msg);
    //JObject json = JObject.Parse(msg);
    //json.userID = userId;

    try {
      JObject evt = JObject.Parse(msg);
      if (evt["evt"] != null) {
        var name = evt["evt"].Value<string>();
        if (_listeners.ContainsKey(name)) {
          _listeners[name].Invoke(evt, userId);
        } else {
          Debug.LogWarning($"event {name} not listened...");
        }
      }
    } catch (Exception) {
      // ignored
    }

    OnMessage?.Invoke(msg, userId);
  }

  public override void On(string name, Action<JObject, int> callback) {
    if (_listeners.ContainsKey(name)) {
      _listeners[name] += callback;
    } else {
      _listeners.Add(name, callback);
    }
  }

  public override void Send(string message, int? toid = null) {
    if (toid != null) {
      Debug.Log(_channels.Count);
      if (!_channels.ContainsKey(toid.Value)) {
        // TODO send when I can
        Debug.LogWarning("message sended too early, channel not ready");
        return;
      }
      var channel = _channels[toid.Value];
      if (channel.ReadyState == RTCDataChannelState.Open) channel.Send(message);
      return;
    }
    foreach (var channel in _channels.Values) {
      Debug.Log($"Broadcast {message}");
      Debug.Log(channel.ReadyState);
      if (channel.ReadyState == RTCDataChannelState.Open) channel.Send(message);
    }
  }

  public override void Close() {
    foreach (var channel in _channels.Values) { channel.Close(); } // close channels
    foreach (var connection in _connections.Values) { connection.Close(); } // close connections
    _webSocket.Close();
    WebRTC.Dispose();
  }
  
  // MainThreadCalls
  void Start() {
    StartCoroutine(CallInMainThread());
  }

  public static void Call(Action action) {
    lock (_actions.SyncRoot) {
      _actions.Enqueue(action);
    }
  }

  IEnumerator CallInMainThread() {
    while (true) {
      yield return new WaitUntil(() => {
        lock (_actions.SyncRoot) {
          return _actions.Count > 0;
        }
      });
      lock (_actions.SyncRoot) {
        while (_actions.Count > 0)
          ((Action)_actions.Dequeue()).Invoke();
      }
    }
  }
  
  // ICE Helpers
  /*
   * {"to":22,"candidate":{"candidate":"candidate:1 1 UDP 2122252543 e75a59e7-0861-4c46-8f2a-93b1648a1159.local 54204 typ host","sdpMid":"0","sdpMLineIndex":0,"usernameFragment":"2b8dac69"},"from":19}
   */
  RTCIceCandidate IceCandidateFromJson([CanBeNull] JToken token) {
    var candidateInfo = new RTCIceCandidateInit() {
      candidate = token["candidate"].Value<string>(),
      sdpMid = token["sdpMid"].Value<string>(),
    };
    if (token["sdpMLineIndex"] != null) {
      candidateInfo.sdpMLineIndex = token["sdpMLineIndex"].Value<int>();
    }
    return new RTCIceCandidate(candidateInfo);
  }

  RTCSdpType RTCSdpTypeFromString(string str) {
    switch (str) {
      case "offer": return RTCSdpType.Offer;
      case "pranswer": return RTCSdpType.Pranswer;
      case "answer": return RTCSdpType.Answer;
      case "rollback": return RTCSdpType.Rollback;
      default: throw new ArgumentOutOfRangeException(nameof(str), str, null);
    }
  }
  
  RTCSessionDescription SessionDescriptionFromJson([CanBeNull] JToken token) {
    return new RTCSessionDescription {
      sdp = token["sdp"].Value<string>(),
      type = RTCSdpTypeFromString(token["type"].Value<string>())
    };
  }
}