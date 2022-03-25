using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

// https://docs.unity3d.com/Packages/com.unity.webrtc@2.3/manual/index.html
[RequireComponent(typeof(NetworkBackend))]
public class MetaHack : MonoBehaviour {

    NetworkBackend _network;
    
    void Awake() {
        DontDestroyOnLoad(gameObject);
        
        _network = GetComponent<NetworkBackend>();
        if (_network == null) {
            _network = gameObject.AddComponent<WebRTCBackend>();
        }
        _network.OnOpen += (userId) => {
            //Debug.Log("OnOpen");
            _network.Send("{\"evt\":\"test\", \"str\":\"hello\"}", userId);
        };
        _network.On("hello", (obj, userId) => {
            Debug.Log($"receive hello from {userId}");
        });
        _network.Init("phone-tracker.glitch.me");
    }

    void OnDestroy() {
        _network.Close();
    }
}
