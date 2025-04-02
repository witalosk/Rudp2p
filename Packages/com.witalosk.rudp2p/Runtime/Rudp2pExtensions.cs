using System;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

namespace Rudp2p
{
    public static class Rudp2pExtensions
    {
        /// <summary>
        /// Sends data to the target endpoint asynchronously without awaiting the result (Fire and Forget).
        /// **Warning:** Successful delivery is not guaranteed or confirmed by this method.
        /// Consider using 'await SendAsync' directly if reliability or error handling is required.
        /// </summary>
        /// <param name="client">The RUDP client instance.</param>
        /// <param name="target">The target endpoint.</param>
        /// <param name="key">A user-defined key to identify the data.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="isReliable">Whether to use reliable transmission (passed to the underlying SendAsync).</param>
        public static void SendAndForgetAsync(this Rudp2pClient client, IPEndPoint target, int key, ReadOnlyMemory<byte> data, bool isReliable = true)
        {
            Task.Run(() => client.SendAsync(target, key, data, isReliable))
                .ContinueWith(t =>
                {
                    // Log t.Exception if t.IsFaulted
                    if (t.IsFaulted)
                    {
                        Debug.Log($"Failed to send data: {t.Exception}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}