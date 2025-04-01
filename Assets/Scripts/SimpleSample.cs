using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Rudp2p
{
    /// <summary>
    /// Send and receive data using Rudp2p
    /// </summary>
    public class SimpleSample : MonoBehaviour
    {
        [SerializeField] private int _port = 12345;
        [SerializeField] private int _port2 = 23456;
        [SerializeField] private string _targetIp = "127.0.0.1";

        private Rudp2pClient _client;
        private Rudp2pClient _client2;
        private Stopwatch _stopwatch = new();
        private Stopwatch _stopwatch2 = new();
        private readonly List<IDisposable> _disposables = new();

        private void Start()
        {
            _client = new Rudp2pClient();
            _client.Start(_port);
            var d1 = _client.RegisterCallback(0, data =>
            {
                _stopwatch2.Stop();
                _receivedText = System.Text.Encoding.UTF8.GetString(data);
                Debug.Log($"Received {_receivedText.Length} char from client2 ({_stopwatch2.ElapsedMilliseconds}ms)");
            });
            _disposables.Add(d1);

            _client2 = new Rudp2pClient();
            _client2.Start(_port2);
            var d2 = _client2.RegisterCallback(0, data =>
            {
                _stopwatch.Stop();
                _receivedText2 = System.Text.Encoding.UTF8.GetString(data);
                Debug.Log($"Received {_receivedText2.Length} char from client1 ({_stopwatch.ElapsedMilliseconds}ms)");
            });
            _disposables.Add(d2);
        }

        private void OnDestroy()
        {
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
            _client.Close();
            _client2.Close();
        }


        private string _sendText = "Hello from client1!";
        private string _sendText2 = "Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat null";
        private string _receivedText = "";
        private string _receivedText2 = "";

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 350, Screen.height - 20), GUI.skin.box);
            GUILayout.Label("CLIENT1");
            GUILayout.Label("Send text");
            _sendText = GUILayout.TextArea(_sendText, GUILayout.MaxHeight(400f), GUILayout.ExpandHeight(false));
            if (GUILayout.Button($"Send : {_sendText.Length} chars"))
            {
                var data = System.Text.Encoding.UTF8.GetBytes(_sendText);
                _client.Send(new IPEndPoint(IPAddress.Parse(_targetIp), _port2), 0, data);
                _stopwatch.Restart();
                Debug.Log($"Sent {data.Length} bytes from client1");
            }
            GUILayout.Label("Received text");
            GUILayout.TextArea(_receivedText, GUILayout.MaxHeight(400f), GUILayout.ExpandHeight(false));
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(400, 10, 350, Screen.height - 20), GUI.skin.box);
            GUILayout.Label("CLIENT2");
            GUILayout.Label("Send text");
            _sendText2 = GUILayout.TextArea(_sendText2, GUILayout.MaxHeight(400f), GUILayout.ExpandHeight(false));
            if (GUILayout.Button($"Send : {_sendText2.Length} chars"))
            {
                var data = System.Text.Encoding.UTF8.GetBytes(_sendText2);
                _client2.Send(new IPEndPoint(IPAddress.Parse(_targetIp), _port), 0, data);
                _stopwatch2.Restart();
                Debug.Log($"Sent {data.Length} bytes from client2");
            }
            GUILayout.Label("Received text");
            GUILayout.TextArea(_receivedText2, GUILayout.MaxHeight(400f), GUILayout.ExpandHeight(false));
            GUILayout.EndArea();
        }
    }

}
