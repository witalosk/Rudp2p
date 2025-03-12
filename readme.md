# Rudp2p
Half-Reliable, unordered, datagram protocol for peer-to-peer communication based on UDP.  
(Currently, for Unity only.)

## Installation
1. Open the Unity Package Manager
2. Click the + button
3. Select "Add package from git URL..."
4. Enter `https://github.com/witalosk/Rudp2p.git?path=Packages/com.witalosk.rudp2p`

## Usage
### Send data
```C#

public class SampleClass : MonoBehaviour
{
    private Rudp2pClient _client;

    private void Start()
    {
        _client = new Rudp2pClient();
        _client.Start(6666);    // specify the port for receiving

        // register the callback to receive
        _client.RegisterCallback(0, OnDataReceive);
    }

    private void SendData()
    {
        _client.Send(
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 6666), // the destination
            0,  // this is a type parameter to distinguish the contents of the data.
            Encoding.GetEncoding("UTF-8").GetBytes("Happy Coding!"),   // data to send
            true    // if true, the client attempts to retransmit if the transmission fails.
        );
    }

    private void OnDataReceive(Action<byte[]> data)
    {
        // if necessary, it should switch to the main thread. (by SynchronizationContext)
        Debug.Log(Encoding.GetEncoding("UTF-8").GetString(data));
    }

    private void OnDestroy()
    {
        _client.Close();
    }

}
