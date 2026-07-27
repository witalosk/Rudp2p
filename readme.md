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

    private async void SendData()
    {
        await _client.SendAsync(
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 6666), // the destination
            0,  // this is a type parameter to distinguish the contents of the data.
            Encoding.GetEncoding("UTF-8").GetBytes("Happy Coding!"),   // data to send
            true    // if true, the client attempts to retransmit if the transmission fails.
        );
    }
    
    private void SendWithoutWaiting()
    {
        _client.SendAndForgetAsync(
            new IPEndPoint(IPAddress.Parse("127.0.0.1"), 6666),
            0,
            Encoding.GetEncoding("UTF-8").GetBytes("Happy Coding!"),
            true
        );
    }

    private void OnDataReceive(Rudp2pReceiveData data)
    {
        // NOTE: This callback runs on the receive-loop thread.
        // If necessary, switch to the main thread (e.g. via SynchronizationContext).
        // NOTE: data.Data is backed by pooled memory that is reused after this callback returns.
        // Copy it (e.g. data.Data.ToArray()) if you need to keep it beyond the callback.
        Debug.Log(Encoding.GetEncoding("UTF-8").GetString(data.Data));
    }

    private void OnDestroy()
    {
        _client.Close();
    }

}
```

## Error handling
- If a reliable send (`isReliable: true`) is not acknowledged within the retry limits, `SendAsync` throws `Rudp2pSendException`.
- `SendAsync` accepts an optional `CancellationToken`. `Close()` also cancels all in-flight sends.

## Limitations
- **No keep-alive / disconnection detection**: The protocol is connectionless and does not exchange
  keep-alive packets. If you communicate across NAT, the NAT mapping may expire during idle periods
  (often under 30 seconds) — send periodic application-level packets to keep it open, and implement
  your own timeout logic to detect unreachable peers.
- **Fixed MTU (no Path MTU Discovery)**: The packet size is bounded by `Rudp2pConfig.Mtu`
  (default: 1400 bytes), which is safe for most networks. On VPNs or tunneled networks with a smaller
  path MTU, oversized datagrams are silently dropped — lower `Mtu` in that case.
- **Unordered**: Message ordering is not guaranteed, even for reliable sends.
- **No congestion control**: Only a static in-flight window (`Rudp2pConfig.SendWindowSize`) and an
  optional token-bucket rate limit (`EnableSendRateLimitByBucket`) bound the send rate.
