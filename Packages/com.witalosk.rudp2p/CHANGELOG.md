# Changelog

## [1.2.0] - 2026-07-27
### Breaking changes
- **Wire format changed**: packet headers now start with a magic number and a packet type field.
  Peers running 1.1.x and 1.2.0 cannot communicate with each other — update all peers together.
- `SendAsync` now throws `Rudp2pSendException` when a reliable send is not acknowledged within
  the retry limits (previously the failure was silently logged).
- `SendAsync` now throws `InvalidOperationException` when called before `Start()`.
- `Close()` now waits (up to 1 second) for the internal loops to finish.

### Added
- `CancellationToken` support for `SendAsync` / `SendAndForgetAsync`. `Close()` also cancels all in-flight sends.
- `Rudp2pConfig.SendWindowSize` (default: 64): caps the number of in-flight fragments to avoid
  flooding the network when sending large payloads.
- Adaptive retransmission timeout (RFC 6298 SRTT/RTTVAR with exponential backoff) instead of the
  fixed retry interval. `ReliableRetryInterval` is now the initial RTO used until RTT samples are collected.
- Editor tests (unit tests + loopback integration tests) under `Tests/Editor`.

### Fixed
- Reliable delivery could be silently broken: the ACK flag array was rented from `ArrayPool`
  without clearing, so leftover flags could mark fragments as acknowledged and skip retransmission.
- Data corruption when multiple peers happened to use the same packet id: reassembly and
  duplicate detection are now keyed by (sender endpoint, packet id).
- Packet id collisions from per-send `new Random()`: ids now come from a shared atomic counter.
- Thread-safety of ACK tracking and callback registration (plain `Dictionary` was shared across threads).
- Incomplete packet mergers leaked pooled buffers forever; they are now evicted after 5 seconds of inactivity.
- ACKs are now only accepted from the endpoint the packet was sent to (prevents third parties
  from suppressing retransmissions).
- Sequence numbers from the network are now bounds-checked before use.
- The receive loop could spin on a dead socket because a `break` only exited the `switch`.
- `Close()` followed by `Start()` could let the old receive loop observe the new socket.
- Pending sends now get cancelled on `Dispose()` instead of hanging forever when the send queue is enabled.
- Unrelated UDP datagrams arriving on the same port are no longer parsed as packets (magic number validation).
- `PacketMerger` treated a duplicated empty fragment as a new one.
- Removed the invalid finalizer that touched managed objects.

### Changed
- Removed all polling loops: ACK waits, the send queue, and the token bucket are now event-driven.
  The token bucket no longer occupies a thread and refills continuously (more accurate rate limiting).
- ACKs are sent asynchronously so the receive loop is never blocked.
- Unreliable sends no longer trigger ACK replies, saving return bandwidth.
- Reduced GC allocations on the receive hot path.
- Documented limitations (no keep-alive, fixed MTU) and the received-data lifetime in the readme.

## [1.1.4] - 2025-12-16
- Changed the socket error handling logic in Receive loop.

## [1.1.3] - 2025-11-10
- Added the configs to toggle the bucket algorithm for sending.

## [1.1.2] - 2025-05-21
- Packet merging bug fix.
- Added the configs to customize the sending bucket size and its refill rate.

## [1.1.1] - 2025-04-11
- Packet merging bug fix.

## [1.1.0] - 2025-04-02
- Optimize the performance of the library.
- Change some API to be more user-friendly and the performance is better.
- Reduce the GC allocation.
- Prevent the duplicate callback call.
- Added the config class to customize the library.

## [1.0.0] - 2025-03-12
### This is the first release of *Rudp2p*.
- Added support for Unity Package Manager.
