namespace Watson.Clients
{
    using System.Threading;

    /// <summary>
    /// Connection statistics for a Watson WebSocket client.
    /// </summary>
    public class WebSocketClientStatistics
    {
        private long _MessagesReceived = 0;
        private long _MessagesSent = 0;
        private long _BytesReceived = 0;
        private long _BytesSent = 0;

        /// <summary>
        /// Number of messages received.
        /// </summary>
        public long MessagesReceived => Interlocked.Read(ref _MessagesReceived);

        /// <summary>
        /// Number of messages sent.
        /// </summary>
        public long MessagesSent => Interlocked.Read(ref _MessagesSent);

        /// <summary>
        /// Number of payload bytes received.
        /// </summary>
        public long BytesReceived => Interlocked.Read(ref _BytesReceived);

        /// <summary>
        /// Number of payload bytes sent.
        /// </summary>
        public long BytesSent => Interlocked.Read(ref _BytesSent);

        /// <summary>
        /// Create a copy of the current counters.
        /// </summary>
        /// <returns>Snapshot.</returns>
        public WebSocketClientStatistics Snapshot()
        {
            WebSocketClientStatistics snapshot = new WebSocketClientStatistics();
            Interlocked.Exchange(ref snapshot._MessagesReceived, MessagesReceived);
            Interlocked.Exchange(ref snapshot._MessagesSent, MessagesSent);
            Interlocked.Exchange(ref snapshot._BytesReceived, BytesReceived);
            Interlocked.Exchange(ref snapshot._BytesSent, BytesSent);
            return snapshot;
        }

        internal void IncrementReceived(long bytes)
        {
            Interlocked.Increment(ref _MessagesReceived);
            Interlocked.Add(ref _BytesReceived, bytes);
        }

        internal void IncrementSent(long bytes)
        {
            Interlocked.Increment(ref _MessagesSent);
            Interlocked.Add(ref _BytesSent, bytes);
        }
    }
}
