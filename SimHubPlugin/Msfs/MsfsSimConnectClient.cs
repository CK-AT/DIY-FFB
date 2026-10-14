// Plan 19 - In-process SimConnect client. Talks to MSFS 2024 directly over
// the named pipe \\.\pipe\Microsoft Flight Simulator\SimConnect, registers a
// SimVar data definition, subscribes at SIM_FRAME, and hands each sample to a
// callback for the plugin to publish into latestMsfsPacket.
//
// Plan 23 - The data definition is now DYNAMIC and FAILURE-TOLERANT. It is the
// 36 fixed defaults (MsfsSimVarTable, always valid) followed by zero or more
// graph-declared custom vars (SimVars / LVARs). Custom vars are validated in
// isolation before joining the streaming definition: each is added to a scratch
// definition and probed with PERIOD_ONCE. A bad A: name raises an exception on
// its probe and is dropped (recorded in FailedVars); everything else keeps
// streaming. Registration is decoupled from streaming so a typo can never take
// the whole telemetry stream down.
//
// Lifecycle mirrors the EXE bridge's outer loop: connect -> handshake ->
// register -> subscribe -> read; on QUIT or pipe break, tear down and retry
// every 2 seconds. Safe to Start() before MSFS is running. Custom-var changes
// (SetCustomVars) are picked up by the worker thread and trigger an in-session
// re-registration; the caller never writes to the pipe.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiyFfb.Msfs
{
    // A graph-declared custom variable to register on top of the 36 defaults.
    internal sealed class MsfsCustomVar
    {
        public readonly string Alias;  // graph-facing key (MSFS.<Alias>)
        public readonly string Name;   // raw SimConnect datum, e.g. "L:FOO" / "GENERAL ENG RPM:1"
        public readonly string Units;  // SimConnect unit string

        public MsfsCustomVar(string alias, string name, string units)
        {
            Alias = alias ?? "";
            Name  = name ?? "";
            Units = units ?? "";
        }
    }

    // A custom var whose PERIOD_ONCE probe raised — dropped from the stream.
    internal sealed class MsfsFailedVar
    {
        public readonly string Alias;
        public readonly string Name;
        public readonly uint   ExceptionCode;

        public MsfsFailedVar(string alias, string name, uint code)
        {
            Alias = alias; Name = name; ExceptionCode = code;
        }
    }

    internal sealed class MsfsSimConnectClient : IDisposable
    {
        private const string PipeName = "Microsoft Flight Simulator\\SimConnect";
        private const uint Protocol = SimConnectProtocol.ProtocolSunRise;
        private const uint DefineId  = 1;
        private const uint RequestId = 1;
        // Scratch ids for per-var probing — a separate range so they can never
        // collide with the streaming definition/request.
        private const uint ProbeDefineId  = 1000;
        private const uint ProbeRequestId = 1000;
        private const int  ConnectTimeoutMs   = 1000;
        private const int  HandshakeTimeoutMs = 5000;
        private const int  ReconnectDelayMs   = 2000;
        // A bad name's exception is prompt on a local pipe; a good var yields no
        // PERIOD_ONCE reply while the sim is paused. So the drain rule is
        // invalid <=> exception, and "no exception within this window" = valid.
        private const int  ProbeTimeoutMs = 100;
        // Guardrail on custom-var count (buffer + per-frame cost).
        private const int  MaxCustomVars = 64;

        private readonly int DefaultCount = MsfsSimVarTable.SampleCount;

        private readonly Action<double[], IReadOnlyDictionary<string, double>> _onSample;
        private readonly Action<string> _log;
        private readonly object _stateLock = new object();

        private Thread _worker;
        private CancellationTokenSource _cts;
        private NamedPipeClientStream _pipe;
        private uint _sendIdSeq;
        private volatile bool _connected;
        private DateTime _lastSampleUtc = DateTime.MinValue;

        // Desired custom-var list (set by SetCustomVars, consumed by the worker).
        private List<MsfsCustomVar> _pendingCustoms = new List<MsfsCustomVar>();
        private volatile bool _customsDirty;

        // Survivors currently in the streaming definition, in payload order:
        // DefaultCount defaults first, then surviving customs. Owned by the
        // worker; a snapshot is used per frame. Failed customs recorded aside.
        private List<MsfsCustomVar> _survivingCustoms = new List<MsfsCustomVar>();
        private List<MsfsFailedVar> _failed = new List<MsfsFailedVar>();
        private bool _customSampleLogged;

        // ---------- Plan 24: write path ----------
        // Desired write set (set by SetWritableVars, consumed by the worker). A
        // changed write set forces a Reconfigure via _writablesDirty, independent
        // of _customsDirty — a graph with writes but no reads must still rebuild.
        private List<MsfsCustomVar> _pendingWritables = new List<MsfsCustomVar>();
        private volatile bool _writablesDirty;

        // alias -> write define id, built in Configure for A:/L: writable vars.
        // Worker-owned; drain reads it on the worker thread only.
        private Dictionary<string, uint> _writeDefineByAlias =
            new Dictionary<string, uint>(StringComparer.Ordinal);

        // Plan 24 Phase 2: alias -> resolved input-event hash for B: targets,
        // built by ResolveInputEvents during Configure. Worker-owned.
        private Dictionary<string, ulong> _hashByAlias =
            new Dictionary<string, ulong>(StringComparer.Ordinal);

        // Plan 24: alias -> client-event id for K: key/sim events, built in
        // ConfigureWritables via MapClientEventToSimEvent. Worker-owned. The id
        // is a SIMCONNECT_CLIENT_EVENT_ID (its own namespace) from this band.
        private Dictionary<string, uint> _eventIdByAlias =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private const uint KeyEventIdBase = 3000;

        // Plan 24 Phase 4: alias -> exception code for write targets that failed
        // (unresolved B: hash → 0; a write that raised RecvIdException → its code).
        // Guarded by _writeLock; snapshot exposed to the editor via WriteFailedVars.
        private Dictionary<string, uint> _writeFailed =
            new Dictionary<string, uint>(StringComparer.Ordinal);

        // A var the aircraft has not created yet fails its probe and is then
        // dropped for the rest of the session. An aircraft (re)load publishes its
        // LVARs a beat after SimConnect is ready, so a graph reading them (e.g.
        // the L:FFB_* helicopter API set) can come up permanently blind. Re-probe
        // the rejected ones on a widening schedule after each configure, then give
        // up so an aircraft that genuinely lacks them stops costing reconfigures.
        // A retry is a full Configure and briefly stops the stream, hence few
        // attempts, spread out.
        private static readonly int[] FailedVarRetryDelaysMs = { 4000, 10000, 20000, 40000 };
        private DateTime _lastConfigureUtc = DateTime.MinValue;
        private int _failedVarRetries;
        private const uint InputEventRequestId = 1001;
        private const int  EnumerateTimeoutMs  = 500;
        private const int  MaxEnumeratePages   = 256;

        // Plan 24 Phase 2 circuit-breaker. The enumerate function id is UNVALIDATED;
        // if MSFS rejects the packet it can drop the whole pipe (taking reads down
        // too). If we attempt the enumerate this many times and NEVER get a reply,
        // stop sending it for the rest of the session so the read stream + A:/L:
        // writes stay healthy. B: targets stay pending. Worker-owned.
        private const int EnumerateMaxAttemptsNoReply = 3;
        private int  _enumerateAttempts;
        private bool _enumerateEverReplied;
        private bool _enumerateDisabled;

        // Coalesced pending writes (latest-value-wins between drains). Guarded by
        // _writeLock; the worker snapshots + clears at the top of each ReadLoop
        // iteration. The caller (WriteValue) never touches the pipe.
        private readonly object _writeLock = new object();
        private Dictionary<string, double> _pendingWrites =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private volatile bool _writesDirty;

        // Aliases already logged as "no route" since the last Configure, so an
        // unresolved target is logged once, not every frame. Worker-owned.
        private readonly HashSet<string> _droppedLogged =
            new HashSet<string>(StringComparer.Ordinal);

        // Bounded sendId -> alias attribution for write exceptions. A live axis
        // mints a fresh sendId per frame, so this is an LRU capped at
        // SendIdMapCap entries — never an unbounded map.
        private const int SendIdMapCap = 512;
        private readonly Dictionary<uint, string> _sendIdToAlias =
            new Dictionary<uint, string>();
        private readonly Queue<uint> _sendIdOrder = new Queue<uint>();

        // Bumped once at the end of every Configure (write-define rebuild /
        // reconnect / re-enumerate). The plugin snapshots this each eval and
        // forces a full re-push of A:/L: writes when it changes (plan 24 §4.3).
        private int _writeGeneration;
        public int WriteGeneration => Volatile.Read(ref _writeGeneration);

        // Write define id-space: a dedicated band, one define per writable var,
        // independent of the streaming define (1) and probe define (1000).
        private const uint WriteDefineBase = 2000;

        // Reused buffers. _rxBuffer holds one inbound packet; starts at 4096
        // (ample for 36 defaults + customs) but GROWS to fit a larger packet —
        // e.g. RECV_ENUMERATE_INPUT_EVENTS lists hundreds of 76-byte entries in
        // one packet, well over 4 KB. Growable, not readonly. Capped so a garbage
        // size field can't trigger a huge allocation.
        private byte[] _rxBuffer = new byte[4096];
        private const int MaxInboundPacketBytes = 1 << 20; // 1 MiB hard ceiling
        private readonly byte[] _txBuffer = new byte[SimConnectProtocol.MaxOutboundPacketBytes];
        private double[] _sampleBuffer = new double[MsfsSimVarTable.SampleCount];

        public MsfsSimConnectClient(
            Action<double[], IReadOnlyDictionary<string, double>> onSample,
            Action<string> log)
        {
            _onSample = onSample ?? throw new ArgumentNullException(nameof(onSample));
            _log      = log ?? (_ => { });
        }

        public bool IsConnected => _connected;
        public DateTime LastSampleUtc { get { lock (_stateLock) return _lastSampleUtc; } }

        // Snapshot of custom vars whose probe failed on the current aircraft.
        public IReadOnlyList<MsfsFailedVar> FailedVars
        {
            get { lock (_stateLock) return _failed.ToArray(); }
        }

        // Plan 24: snapshot of write targets that failed (alias -> exception code;
        // 0 = an unresolved B: input event). Consumed by the editor warning surface.
        public IReadOnlyDictionary<string, uint> WriteFailedVars
        {
            get { lock (_writeLock) return new Dictionary<string, uint>(_writeFailed, StringComparer.Ordinal); }
        }

        // Replace the desired custom-var list. Worker-thread ownership: this only
        // stores the pending list and flags it; the worker re-registers at a safe
        // point. Never writes to the pipe from the caller's thread. Excess vars
        // beyond MaxCustomVars are dropped (logged by the worker on apply).
        public void SetCustomVars(IReadOnlyList<MsfsCustomVar> customs)
        {
            var copy = new List<MsfsCustomVar>();
            if (customs != null)
            {
                foreach (var c in customs)
                {
                    if (c == null || string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Alias))
                        continue;
                    copy.Add(c);
                }
            }
            lock (_stateLock) { _pendingCustoms = copy; }
            _customsDirty = true;
        }

        // Plan 24: declare the full set of writable targets (A:/L: vars and, in
        // Phase 2, B: input events). Worker-thread ownership, exactly like
        // SetCustomVars: store + flag; the worker rebuilds the write defines at a
        // safe point. _writablesDirty is a SEPARATE reconfigure input from
        // _customsDirty — a graph with writes but no reads has no read-side dirty.
        public void SetWritableVars(IReadOnlyList<MsfsCustomVar> writables)
        {
            var copy = new List<MsfsCustomVar>();
            if (writables != null)
            {
                foreach (var w in writables)
                {
                    if (w == null || string.IsNullOrEmpty(w.Name) || string.IsNullOrEmpty(w.Alias))
                        continue;
                    copy.Add(w);
                }
            }
            lock (_stateLock) { _pendingWritables = copy; }
            _writablesDirty = true;
        }

        // Plan 24: queue a value to write to the target registered under `alias`.
        // Thread-safe and coalesced (latest-value-wins between drains); never
        // touches the pipe. The worker routes each alias by how it registered.
        public void WriteValue(string alias, double value)
        {
            if (string.IsNullOrEmpty(alias)) return;
            lock (_writeLock) { _pendingWrites[alias] = value; }
            _writesDirty = true;
        }

        // A B:-prefixed target is an Input Event, not a data-definition datum.
        private static bool IsInputEventName(string name) =>
            name != null && name.StartsWith("B:", StringComparison.Ordinal);

        // A K:-prefixed target is a key/sim event (MapClientEventToSimEvent +
        // TransmitClientEvent), not a data-definition datum or an input event.
        private static bool IsKeyEventName(string name) =>
            name != null && name.StartsWith("K:", StringComparison.Ordinal);

        public void Start()
        {
            lock (_stateLock)
            {
                if (_worker != null) return;
                _cts = new CancellationTokenSource();
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "MsfsSimConnectClient",
                };
                _worker.Start();
            }
        }

        public void Stop()
        {
            CancellationTokenSource cts;
            Thread worker;
            NamedPipeClientStream pipe;
            lock (_stateLock)
            {
                cts = _cts; worker = _worker; pipe = _pipe;
                _cts = null; _worker = null;
            }
            if (cts == null) return;

            try { cts.Cancel(); } catch { }
            try { pipe?.Dispose(); } catch { }
            try { worker?.Join(2000); } catch { }
            try { cts.Dispose(); } catch { }
        }

        public void Dispose() => Stop();

        // -----------------------------------------------------------------
        //  Worker thread
        // -----------------------------------------------------------------

        private enum LoopResult { Disconnected, Reconfigure }

        private void WorkerLoop()
        {
            CancellationToken ct;
            lock (_stateLock) { ct = _cts.Token; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(
                        ".", PipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        lock (_stateLock) { _pipe = pipe; }

                        try
                        {
                            pipe.Connect(ConnectTimeoutMs);
                        }
                        catch (TimeoutException)
                        {
                            DelayOrCancel(ReconnectDelayMs, ct);
                            continue;
                        }

                        if (ct.IsCancellationRequested) break;

                        if (!Handshake(pipe, ct))
                        {
                            DelayOrCancel(ReconnectDelayMs, ct);
                            continue;
                        }

                        // (Re)configure the definition in-session until the pipe
                        // breaks or MSFS quits. A custom-var change comes back as
                        // Reconfigure and rebuilds without reconnecting. The first
                        // configure of a fresh session needs no clear (the define
                        // doesn't exist yet); re-registrations do.
                        LoopResult result = LoopResult.Reconfigure;
                        bool firstConfigure = true;
                        DateTime connectedAt = DateTime.UtcNow;
                        while (result == LoopResult.Reconfigure && !ct.IsCancellationRequested)
                        {
                            Configure(pipe, clearFirst: !firstConfigure);
                            firstConfigure = false;
                            _connected = true;
                            result = ReadLoop(pipe, ct);
                        }
                        if (result == LoopResult.Disconnected && !ct.IsCancellationRequested)
                            _log($"[MsfsSimConnect] pipe closed after {(DateTime.UtcNow - connectedAt).TotalMilliseconds:0} ms up (reconnecting).");
                    }
                }
                catch (Exception ex) when (
                    ex is IOException || ex is ObjectDisposedException ||
                    ex is EndOfStreamException || ex is UnauthorizedAccessException)
                {
                    // Expected: pipe broke, MSFS quit, or we got Stop()'d.
                    if (!ct.IsCancellationRequested)
                        _log($"[MsfsSimConnect] pipe I/O ended ({ex.GetType().Name}); reconnecting.");
                }
                catch (Exception ex)
                {
                    _log("[MsfsSimConnect] unexpected: " + ex.Message);
                }
                finally
                {
                    _connected = false;
                    lock (_stateLock) { _pipe = null; }
                }

                DelayOrCancel(ReconnectDelayMs, ct);
            }

            _log("[MsfsSimConnect] worker exiting.");
        }

        // -----------------------------------------------------------------
        //  Handshake
        // -----------------------------------------------------------------

        private bool Handshake(NamedPipeClientStream pipe, CancellationToken ct)
        {
            uint sendId = NextSendId();
            int n = SimConnectProtocol.WriteOpen(_txBuffer, Protocol, "DiyFfbPlugin", sendId);
            pipe.Write(_txBuffer, 0, n);
            pipe.Flush();

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(HandshakeTimeoutMs);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (!ReadOnePacket(pipe, out int totalSize)) return false;
                if (!SimConnectProtocol.TryReadHeader(_rxBuffer, totalSize,
                        out _, out _, out uint recvId)) continue;

                if (recvId == SimConnectProtocol.RecvIdOpen)
                {
                    return true;
                }
                if (recvId == SimConnectProtocol.RecvIdException)
                {
                    if (SimConnectProtocol.TryReadException(_rxBuffer, totalSize,
                            out uint code, out uint badSendId, out _))
                    {
                        _log($"[MsfsSimConnect] Open rejected: exception={code} sendId={badSendId}");
                    }
                    return false;
                }
            }
            _log("[MsfsSimConnect] handshake timeout.");
            return false;
        }

        // -----------------------------------------------------------------
        //  Registration (defaults + probed customs) + subscription
        // -----------------------------------------------------------------

        // Rebuild the streaming data definition, then subscribe at SIM_FRAME.
        // Defaults are trusted (added unprobed); customs are validated one at a
        // time and only survivors join the definition.
        private void Configure(NamedPipeClientStream pipe, bool clearFirst)
        {
            // A configure someone else asked for — a fresh session (no clear yet)
            // or a changed var set — restarts the retry schedule. One we triggered
            // ourselves continues it.
            if (!clearFirst || _customsDirty || _writablesDirty) _failedVarRetries = 0;
            _customsDirty = false;
            _writablesDirty = false;

            List<MsfsCustomVar> customs;
            lock (_stateLock) { customs = _pendingCustoms; }

            var survivors = new List<MsfsCustomVar>();
            var failed = new List<MsfsFailedVar>();

            // Reset the streaming definition before rebuilding it. Skipped on a
            // fresh session where the define doesn't exist yet (avoids a stray
            // "unknown define" exception).
            if (clearFirst)
            {
                WriteAndFlush(pipe, SimConnectProtocol.WriteClearDataDefinition(
                    _txBuffer, Protocol, DefineId, NextSendId()));
            }

            // Defaults: datumId = index 0..35, in table order.
            for (int i = 0; i < MsfsSimVarTable.Entries.Length; i++)
            {
                MsfsSimVarTable.Entry e = MsfsSimVarTable.Entries[i];
                AddToDefinition(pipe, DefineId, e.Name, e.Units, (uint)i);
            }
            WriteFlush(pipe);

            // Customs: probe each in isolation, append survivors after the
            // defaults (datumId continues from DefaultCount).
            int added = 0;
            if (customs != null)
            {
                foreach (var c in customs)
                {
                    if (added >= MaxCustomVars)
                    {
                        _log($"[MsfsSimConnect] custom var cap ({MaxCustomVars}) reached; '{c.Name}' and any after it were skipped.");
                        break;
                    }

                    if (ProbeCustom(pipe, c, out uint code))
                    {
                        AddToDefinition(pipe, DefineId, c.Name, c.Units,
                            (uint)(DefaultCount + survivors.Count));
                        WriteFlush(pipe);
                        survivors.Add(c);
                        added++;
                    }
                    else
                    {
                        failed.Add(new MsfsFailedVar(c.Alias, c.Name, code));
                        _log($"[MsfsSimConnect] custom var rejected: '{c.Name}' (alias '{c.Alias}') exception={code}.");
                    }
                }
            }

            int total = DefaultCount + survivors.Count;
            if (_sampleBuffer.Length < total) _sampleBuffer = new double[total];

            lock (_stateLock)
            {
                _survivingCustoms = survivors;
                _failed = failed;
            }
            _customSampleLogged = false;

            Subscribe(pipe);
            _log($"[MsfsSimConnect] connected, streaming SIM_FRAME: {DefaultCount} defaults + {survivors.Count} custom ({failed.Count} rejected).");

            ConfigureWritables(pipe, clearFirst);

            // Stamp last so the retry delay measures from a finished configure,
            // not from the start of one that spent a second probing.
            _lastConfigureUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// True when the last configure rejected at least one read or write target
        /// and the next retry slot is due. Bounded by the schedule length, so an
        /// aircraft that will never have those vars settles after the last attempt.
        /// </summary>
        private bool ShouldRetryFailedVars(out int pending)
        {
            // Called every read-loop iteration, so the two cheap worker-owned
            // checks gate the locks rather than the other way round.
            pending = 0;
            if (_failedVarRetries >= FailedVarRetryDelaysMs.Length) return false;
            if ((DateTime.UtcNow - _lastConfigureUtc).TotalMilliseconds
                < FailedVarRetryDelaysMs[_failedVarRetries]) return false;

            lock (_stateLock) { pending += _failed.Count; }
            lock (_writeLock) { pending += _writeFailed.Count; }
            return pending > 0;
        }

        // Plan 24: (re)build the A:/L: write defines — one define per writable var
        // in the WriteDefineBase band, each holding a single FLOAT64. Each define
        // is Cleared before re-adding: AddToDataDefinition APPENDS, and a rebuild
        // reassigns defineIds, so skipping the clear would leave a stale datum and
        // the single-FLOAT64 write would target the wrong one (plan 24 §4.2).
        // B: entries are handled by the Input Event resolver (Phase 2), not here.
        // Bumps WriteGeneration so the plugin forces a full re-push.
        private void ConfigureWritables(NamedPipeClientStream pipe, bool clearFirst)
        {
            List<MsfsCustomVar> writables;
            lock (_stateLock) { writables = _pendingWritables; }

            var defineByAlias = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint next = WriteDefineBase;
            int added = 0;

            if (writables != null)
            {
                foreach (var w in writables)
                {
                    if (added >= MaxCustomVars)
                    {
                        _log($"[MsfsSimConnect] writable cap ({MaxCustomVars}) reached; '{w.Name}' and any after it were skipped.");
                        break;
                    }
                    if (IsInputEventName(w.Name)) continue;   // B: → Input Event resolver
                    if (IsKeyEventName(w.Name)) continue;     // K: → key-event mapping below
                    if (defineByAlias.ContainsKey(w.Alias)) continue; // dup alias, first wins

                    uint defineId = next++;
                    // A reconfigure may reuse this defineId for a different var —
                    // clear it before adding so no stale datum lingers.
                    WriteAndFlush(pipe, SimConnectProtocol.WriteClearDataDefinition(
                        _txBuffer, Protocol, defineId, NextSendId()));
                    AddToDefinition(pipe, defineId, w.Name, w.Units, datumId: 0);
                    WriteFlush(pipe);
                    defineByAlias[w.Alias] = defineId;
                    added++;
                }
            }

            _writeDefineByAlias = defineByAlias;
            _droppedLogged.Clear();
            lock (_writeLock)
            {
                _sendIdToAlias.Clear();
                _sendIdOrder.Clear();
                _writeFailed.Clear();
            }

            // Plan 24: map K: key/sim events (stable transport). Assign each a
            // client-event id and register the name — fire-and-forget; a bad name
            // yields an async exception but never closes the pipe.
            var eventIdByAlias = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (writables != null)
            {
                uint nextEvent = KeyEventIdBase;
                foreach (var w in writables)
                {
                    if (!IsKeyEventName(w.Name)) continue;
                    if (eventIdByAlias.ContainsKey(w.Alias)) continue;
                    uint eventId = nextEvent++;
                    WriteAndFlush(pipe, SimConnectProtocol.WriteMapClientEventToSimEvent(
                        _txBuffer, Protocol, eventId, w.Name.Substring(2), NextSendId()));
                    eventIdByAlias[w.Alias] = eventId;
                }
            }
            _eventIdByAlias = eventIdByAlias;

            // Plan 24 Phase 2: resolve B: input-event hashes for this aircraft.
            _hashByAlias = ResolveInputEvents(pipe, writables);

            // Record B: targets that never resolved on this aircraft (pending /
            // absent) as write failures (code 0) so the editor can flag them.
            if (writables != null)
            {
                lock (_writeLock)
                {
                    foreach (var w in writables)
                    {
                        if (IsInputEventName(w.Name) && !_hashByAlias.ContainsKey(w.Alias))
                            _writeFailed[w.Alias] = 0;
                    }
                }
            }

            unchecked { Volatile.Write(ref _writeGeneration, _writeGeneration + 1); }
            if (added > 0 || _hashByAlias.Count > 0 || _eventIdByAlias.Count > 0)
                _log($"[MsfsSimConnect] writables registered: {added} var define(s), {_eventIdByAlias.Count} key event(s), {_hashByAlias.Count} input event(s).");
        }

        // Plan 24 Phase 2: enumerate the current aircraft's input events and build
        // alias -> hash for the declared B: targets. FAILS OPEN (plan §4.5): a
        // timed-out / empty / garbled enumerate returns whatever resolved so far
        // (possibly nothing) and lets Configure finish normally — unresolved B:
        // aliases stay pending (dropped-and-logged) until a later re-register.
        // The drain is bounded on BOTH a wall-clock deadline and a page cap, so no
        // reply shape can spin or block. UNVALIDATED wire format.
        private Dictionary<string, ulong> ResolveInputEvents(
            NamedPipeClientStream pipe, List<MsfsCustomVar> writables)
        {
            var hashByAlias = new Dictionary<string, ulong>(StringComparer.Ordinal);

            // Pending B: targets. Match case-INSENSITIVELY against BOTH the
            // B:-stripped name and the full "B:NAME" form — MSFS input-event names
            // are conventionally all-caps and the enumeration form is unvalidated,
            // so accept either. Normalized key (upper) -> alias.
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (writables != null)
            {
                foreach (var w in writables)
                {
                    if (!IsInputEventName(w.Name)) continue;
                    string full = w.Name.Trim();
                    string stripped = full.Substring(2); // drop "B:"
                    if (stripped.Length == 0) continue;
                    if (!wanted.ContainsKey(stripped)) wanted[stripped] = w.Alias;
                    if (!wanted.ContainsKey(full)) wanted[full] = w.Alias;
                }
            }
            if (wanted.Count == 0) return hashByAlias;

            // Circuit-breaker: a prior run proved the enumerate never replies (and
            // may be dropping the pipe). Skip it — B: targets stay pending, reads
            // and A:/L: writes are unaffected. Restart SimHub to re-arm.
            if (_enumerateDisabled)
            {
                int pendingCount = new HashSet<string>(wanted.Values, StringComparer.Ordinal).Count;
                _log($"[MsfsSimConnect] input-event enumeration disabled this session; {pendingCount} B: target(s) stay pending.");
                return hashByAlias;
            }

            _enumerateAttempts++;
            WriteAndFlush(pipe, SimConnectProtocol.WriteEnumerateInputEvents(
                _txBuffer, Protocol, InputEventRequestId, NextSendId()));

            var page = new List<SimConnectProtocol.InputEventRecord>();
            var allNames = new List<string>();      // diagnostic: every enumerated name
            uint totalExpected = uint.MaxValue;      // until first page tells us dwOutOf
            int received = 0;
            int pages = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnumerateTimeoutMs);

            while (DateTime.UtcNow < deadline && pages < MaxEnumeratePages)
            {
                if (!TryReadPacketBeforeDeadline(pipe, deadline, out int totalSize))
                    break; // nothing more before the deadline — give up (fail open)
                if (!SimConnectProtocol.TryReadHeader(_rxBuffer, totalSize,
                        out _, out _, out uint recvId))
                    continue;
                if (recvId != SimConnectProtocol.RecvIdEnumerateInputEvents)
                    continue; // ignore anything else arriving mid-enumerate

                page.Clear();
                if (!SimConnectProtocol.TryReadEnumerateInputEvents(_rxBuffer, totalSize,
                        out uint rid, out _, out _, out uint outOf, page))
                    break;
                if (rid != InputEventRequestId) continue;

                pages++;
                totalExpected = outOf;
                foreach (var rec in page)
                {
                    received++;
                    if (allNames.Count < 400) allNames.Add(rec.Name);
                    if (wanted.TryGetValue(rec.Name?.Trim() ?? "", out string alias))
                        hashByAlias[alias] = rec.Hash;
                }

                if (totalExpected != uint.MaxValue && received >= (int)totalExpected)
                    break; // all pages accumulated
            }

            // One-shot diagnostic (UNVALIDATED wire format): dump what the enumerate
            // actually returned so a "confirmed name but unresolved" can be pinned
            // to a parser-offset bug (garbage names) vs. a name/form mismatch (sane
            // names, different string). Distinct aliases resolved counts too.
            if (received > 0) _enumerateEverReplied = true;
            int distinctResolved = new HashSet<string>(hashByAlias.Keys, StringComparer.Ordinal).Count;
            _log($"[MsfsSimConnect] EnumerateInputEvents: attempt={_enumerateAttempts} pages={pages} received={received} outOf={(totalExpected == uint.MaxValue ? -1 : (int)totalExpected)} resolved={distinctResolved}.");

            // Trip the circuit-breaker: never a single reply across N attempts →
            // the enumerate is almost certainly the wrong function id for this MSFS
            // build and is destabilising the pipe. Stop sending it.
            if (!_enumerateEverReplied && _enumerateAttempts >= EnumerateMaxAttemptsNoReply)
            {
                _enumerateDisabled = true;
                _log($"[MsfsSimConnect] EnumerateInputEvents produced no reply in {_enumerateAttempts} attempts — DISABLING input-event resolution for this session to protect the connection. B: targets stay pending; reads and A:/L: writes are unaffected. (UNVALIDATED protocol id — see plan 24 §4.5.)");
            }
            if (received > 0)
            {
                int dump = Math.Min(allNames.Count, 80);
                var sb = new System.Text.StringBuilder("[MsfsSimConnect] input event names:");
                for (int i = 0; i < dump; i++) sb.Append(' ').Append('\'').Append(allNames[i]).Append('\'');
                if (allNames.Count > dump) sb.Append($" …(+{received - dump} more)");
                _log(sb.ToString());
            }

            int unresolved = new HashSet<string>(wanted.Values, StringComparer.Ordinal).Count - distinctResolved;
            if (unresolved > 0)
                _log($"[MsfsSimConnect] {unresolved} B: input event(s) unresolved on this aircraft (pending).");
            return hashByAlias;
        }

        // Validate one custom var in a scratch definition. Returns true if the
        // var is usable. Drain rule: an exception matching the add or request
        // sendId => invalid; a PERIOD_ONCE data reply or timeout => valid.
        private bool ProbeCustom(NamedPipeClientStream pipe, MsfsCustomVar c, out uint exceptionCode)
        {
            exceptionCode = 0;

            WriteAndFlush(pipe, SimConnectProtocol.WriteClearDataDefinition(
                _txBuffer, Protocol, ProbeDefineId, NextSendId()));

            uint addSend = NextSendId();
            WriteAndFlush(pipe, SimConnectProtocol.WriteAddToDataDefinition(
                _txBuffer, Protocol, ProbeDefineId, c.Name, c.Units,
                SimConnectProtocol.DataTypeFloat64, epsilon: 0.0f,
                datumId: SimConnectProtocol.Unused, sendId: addSend));

            uint reqSend = NextSendId();
            WriteAndFlush(pipe, SimConnectProtocol.WriteRequestDataOnSimObject(
                _txBuffer, Protocol,
                requestId: ProbeRequestId, defineId: ProbeDefineId,
                objectId: SimConnectProtocol.ObjectIdUser,
                period: SimConnectProtocol.PeriodOnce,
                flags: 0, origin: 0, interval: 0, limit: 0, sendId: reqSend));

            bool valid = true;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ProbeTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!TryReadPacketBeforeDeadline(pipe, deadline, out int totalSize))
                    break; // no more data before deadline -> no exception -> valid
                if (!SimConnectProtocol.TryReadHeader(_rxBuffer, totalSize,
                        out _, out _, out uint recvId))
                    continue;

                if (recvId == SimConnectProtocol.RecvIdException)
                {
                    if (SimConnectProtocol.TryReadException(_rxBuffer, totalSize,
                            out uint code, out uint sendId, out _)
                        && (sendId == addSend || sendId == reqSend))
                    {
                        exceptionCode = code;
                        valid = false;
                        break;
                    }
                    // Unrelated exception (e.g. the earlier clear on an unknown
                    // define) — ignore and keep draining.
                }
                else if (recvId == SimConnectProtocol.RecvIdSimObjectData)
                {
                    if (SimConnectProtocol.TryReadSimObjectData(_rxBuffer, totalSize,
                            out uint rid, out _, out _, out _, out _, out _)
                        && rid == ProbeRequestId)
                    {
                        valid = true;
                        break;
                    }
                }
                // Ignore other messages (QUIT is handled by the read failing).
            }

            // Recycle the scratch definition for the next probe.
            WriteAndFlush(pipe, SimConnectProtocol.WriteClearDataDefinition(
                _txBuffer, Protocol, ProbeDefineId, NextSendId()));
            return valid;
        }

        private void AddToDefinition(NamedPipeClientStream pipe, uint defineId,
            string name, string units, uint datumId)
        {
            int n = SimConnectProtocol.WriteAddToDataDefinition(
                _txBuffer, Protocol, defineId, name, units,
                SimConnectProtocol.DataTypeFloat64, epsilon: 0.0f,
                datumId: datumId, sendId: NextSendId());
            pipe.Write(_txBuffer, 0, n);
        }

        private void Subscribe(NamedPipeClientStream pipe)
        {
            int n = SimConnectProtocol.WriteRequestDataOnSimObject(
                _txBuffer, Protocol,
                requestId: RequestId, defineId: DefineId,
                objectId: SimConnectProtocol.ObjectIdUser,
                period: SimConnectProtocol.PeriodSimFrame,
                flags: 0, origin: 0, interval: 0, limit: 0, sendId: NextSendId());
            pipe.Write(_txBuffer, 0, n);
            pipe.Flush();
        }

        // Stop the SIM_FRAME stream before clearing/rebuilding the definition.
        private void StopStream(NamedPipeClientStream pipe)
        {
            WriteAndFlush(pipe, SimConnectProtocol.WriteRequestDataOnSimObject(
                _txBuffer, Protocol,
                requestId: RequestId, defineId: DefineId,
                objectId: SimConnectProtocol.ObjectIdUser,
                period: SimConnectProtocol.PeriodNever,
                flags: 0, origin: 0, interval: 0, limit: 0, sendId: NextSendId()));
        }

        // -----------------------------------------------------------------
        //  Main read loop
        // -----------------------------------------------------------------

        private LoopResult ReadLoop(NamedPipeClientStream pipe, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Custom vars OR the write set changed — stop the stream and
                // re-register. The two dirty flags are independent inputs to the
                // same reconfigure gate (plan 24 §4.2).
                if (_customsDirty || _writablesDirty)
                {
                    StopStream(pipe);
                    return LoopResult.Reconfigure;
                }

                // Vars rejected last time may exist by now — the aircraft was
                // probably still loading when we probed. Re-register on the
                // widening retry schedule.
                if (ShouldRetryFailedVars(out int pendingVars))
                {
                    _failedVarRetries++;
                    _log($"[MsfsSimConnect] re-probing {pendingVars} rejected var(s) " +
                         $"(attempt {_failedVarRetries}/{FailedVarRetryDelaysMs.Length}).");
                    StopStream(pipe);
                    return LoopResult.Reconfigure;
                }

                // Flush any pending writes. Latency = one loop iteration; a write
                // is not sent until the next inbound packet unblocks ReadOnePacket
                // (acceptable — writes only matter in-flight while data flows).
                if (_writesDirty) DrainWrites(pipe);

                if (!ReadOnePacket(pipe, out int totalSize)) return LoopResult.Disconnected;
                if (!SimConnectProtocol.TryReadHeader(_rxBuffer, totalSize,
                        out _, out _, out uint recvId)) continue;

                if (recvId == SimConnectProtocol.RecvIdSimObjectData)
                {
                    HandleSimObjectData(totalSize);
                }
                else if (recvId == SimConnectProtocol.RecvIdQuit)
                {
                    _log("[MsfsSimConnect] MSFS quit, reconnecting.");
                    return LoopResult.Disconnected;
                }
                else if (recvId == SimConnectProtocol.RecvIdException)
                {
                    if (SimConnectProtocol.TryReadException(_rxBuffer, totalSize,
                            out uint code, out uint sendId, out _))
                    {
                        string alias = LookupSendAlias(sendId);
                        if (alias != null)
                        {
                            lock (_writeLock) { _writeFailed[alias] = code; }
                            _log($"[MsfsSimConnect] write failed: alias '{alias}' exception={code} sendId={sendId}");
                        }
                        else
                            _log($"[MsfsSimConnect] exception={code} sendId={sendId}");
                    }
                }
            }
            return LoopResult.Disconnected;
        }

        private void HandleSimObjectData(int totalSize)
        {
            if (!SimConnectProtocol.TryReadSimObjectData(_rxBuffer, totalSize,
                    out uint requestId, out _, out _, out _, out uint defineCount,
                    out int payloadOffset))
                return;
            if (requestId != RequestId) return;

            List<MsfsCustomVar> customs;
            lock (_stateLock) { customs = _survivingCustoms; }
            int expected = DefaultCount + customs.Count;

            // A default going missing is the only fatal case (should not happen).
            // A short/long count vs the survivor set means the definition and our
            // view diverged — skip the frame; re-registration will resync.
            if (defineCount != (uint)expected) return;
            if ((int)defineCount < DefaultCount) return;

            int bytesNeeded = (int)defineCount * 8;
            if (payloadOffset + bytesNeeded > totalSize) return;

            for (int i = 0; i < (int)defineCount; i++)
            {
                _sampleBuffer[i] = SimConnectProtocol.ReadFloat64(_rxBuffer, payloadOffset + i * 8);
            }

            IReadOnlyDictionary<string, double> customValues;
            if (customs.Count == 0)
            {
                customValues = EmptyCustoms;
            }
            else
            {
                var map = new Dictionary<string, double>(customs.Count, StringComparer.Ordinal);
                for (int i = 0; i < customs.Count; i++)
                {
                    map[customs[i].Alias] = _sampleBuffer[DefaultCount + i];
                }
                customValues = map;

                // One-shot diagnostic: log the raw custom values the transport
                // actually reads, so a "reads 0" report can be pinned to the
                // transport vs. downstream. Reset each (re)registration.
                if (!_customSampleLogged)
                {
                    _customSampleLogged = true;
                    var sb = new System.Text.StringBuilder("[MsfsSimConnect] first custom sample:");
                    for (int i = 0; i < customs.Count; i++)
                    {
                        sb.Append($" {customs[i].Alias}({customs[i].Name})={_sampleBuffer[DefaultCount + i]:0.###}");
                    }
                    _log(sb.ToString());
                }
            }

            lock (_stateLock) { _lastSampleUtc = DateTime.UtcNow; }

            try { _onSample(_sampleBuffer, customValues); }
            catch (Exception ex) { _log("[MsfsSimConnect] callback threw: " + ex.Message); }
        }

        private static readonly IReadOnlyDictionary<string, double> EmptyCustoms =
            new Dictionary<string, double>(0);

        // -----------------------------------------------------------------
        //  Plan 24: write drain + exception attribution
        // -----------------------------------------------------------------

        // Snapshot + clear the coalesced pending writes, then send one write per
        // entry via its routed transport. An alias with no route (define not
        // built, or a B: hash unresolved) is dropped and logged once. Worker
        // thread only. No re-diff — change-detection already ran plugin-side.
        private void DrainWrites(NamedPipeClientStream pipe)
        {
            KeyValuePair<string, double>[] pending;
            lock (_writeLock)
            {
                _writesDirty = false;
                if (_pendingWrites.Count == 0) return;
                pending = new KeyValuePair<string, double>[_pendingWrites.Count];
                int i = 0;
                foreach (var kvp in _pendingWrites) pending[i++] = kvp;
                _pendingWrites.Clear();
            }

            foreach (var kvp in pending)
            {
                if (_writeDefineByAlias.TryGetValue(kvp.Key, out uint defineId))
                {
                    uint sendId = NextSendId();
                    RecordSendAlias(sendId, kvp.Key);
                    WriteAndFlush(pipe, SimConnectProtocol.WriteSetDataOnSimObjectFloat64(
                        _txBuffer, Protocol, defineId,
                        SimConnectProtocol.ObjectIdUser, kvp.Value, sendId));
                }
                else if (_eventIdByAlias.TryGetValue(kvp.Key, out uint eventId))
                {
                    // K: key/sim event — DWORD value (two's-complement for signed
                    // axis SET events, e.g. -16383..16383).
                    uint data = unchecked((uint)(int)Math.Round(kvp.Value));
                    uint sendId = NextSendId();
                    RecordSendAlias(sendId, kvp.Key);
                    WriteAndFlush(pipe, SimConnectProtocol.WriteTransmitClientEvent(
                        _txBuffer, Protocol, SimConnectProtocol.ObjectIdUser, eventId, data,
                        SimConnectProtocol.GroupPriorityHighest,
                        SimConnectProtocol.EventFlagGroupIdIsPriority, sendId));
                }
                else if (_hashByAlias.TryGetValue(kvp.Key, out ulong hash))
                {
                    uint sendId = NextSendId();
                    RecordSendAlias(sendId, kvp.Key);
                    WriteAndFlush(pipe, SimConnectProtocol.WriteSetInputEventFloat64(
                        _txBuffer, Protocol, hash, kvp.Value, sendId));
                }
                else if (_droppedLogged.Add(kvp.Key))
                {
                    _log($"[MsfsSimConnect] write dropped: alias '{kvp.Key}' has no resolved target (yet).");
                }
            }
        }

        private void RecordSendAlias(uint sendId, string alias)
        {
            lock (_writeLock)
            {
                if (_sendIdToAlias.ContainsKey(sendId)) return;
                _sendIdToAlias[sendId] = alias;
                _sendIdOrder.Enqueue(sendId);
                while (_sendIdOrder.Count > SendIdMapCap)
                {
                    uint old = _sendIdOrder.Dequeue();
                    _sendIdToAlias.Remove(old);
                }
            }
        }

        private string LookupSendAlias(uint sendId)
        {
            lock (_writeLock)
            {
                return _sendIdToAlias.TryGetValue(sendId, out string alias) ? alias : null;
            }
        }

        // -----------------------------------------------------------------
        //  Pipe I/O
        // -----------------------------------------------------------------

        private void WriteAndFlush(NamedPipeClientStream pipe, int n)
        {
            pipe.Write(_txBuffer, 0, n);
            pipe.Flush();
        }

        private void WriteFlush(NamedPipeClientStream pipe)
        {
            pipe.Flush();
        }

        private bool ReadOnePacket(NamedPipeClientStream pipe, out int totalSize)
        {
            totalSize = 0;
            if (!ReadFully(pipe, _rxBuffer, 0, 4)) return false;
            int size = (int)((uint)_rxBuffer[0]
                | ((uint)_rxBuffer[1] <<  8)
                | ((uint)_rxBuffer[2] << 16)
                | ((uint)_rxBuffer[3] << 24));
            if (size < SimConnectProtocol.InboundHeaderSize || size > MaxInboundPacketBytes)
            {
                return false;
            }
            if (size > _rxBuffer.Length)
            {
                // Grow to fit a large packet (e.g. a RECV_ENUMERATE_INPUT_EVENTS
                // list). Preserve the 4 size bytes already read. Capped above.
                var bigger = new byte[size];
                Buffer.BlockCopy(_rxBuffer, 0, bigger, 0, 4);
                _rxBuffer = bigger;
            }
            if (!ReadFully(pipe, _rxBuffer, 4, size - 4)) return false;
            totalSize = size;
            return true;
        }

        // .NET pipe streams don't support read timeouts, and a blocking read
        // would hang a good-var probe forever while the sim is paused (no
        // PERIOD_ONCE reply). So we poll with PeekNamedPipe and only read once a
        // whole packet is buffered; "nothing before the deadline" => silent =>
        // valid (per the drain rule). Returns false on deadline or pipe error.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(
            SafePipeHandle hNamedPipe, byte[] lpBuffer, uint nBufferSize,
            IntPtr lpBytesRead, out uint lpTotalBytesAvail, IntPtr lpBytesLeftThisMessage);

        private readonly byte[] _peek4 = new byte[4];

        private bool TryReadPacketBeforeDeadline(NamedPipeClientStream pipe, DateTime deadline, out int totalSize)
        {
            totalSize = 0;
            SafePipeHandle handle = pipe.SafePipeHandle;
            while (DateTime.UtcNow < deadline)
            {
                if (!PeekNamedPipe(handle, _peek4, 4, IntPtr.Zero, out uint avail, IntPtr.Zero))
                    return false; // pipe broken/closed
                if (avail >= 4)
                {
                    int size = (int)((uint)_peek4[0]
                        | ((uint)_peek4[1] <<  8)
                        | ((uint)_peek4[2] << 16)
                        | ((uint)_peek4[3] << 24));
                    if (size < SimConnectProtocol.InboundHeaderSize || size > MaxInboundPacketBytes)
                        return false; // garbage frame
                    if (avail >= (uint)size)
                        return ReadOnePacket(pipe, out totalSize); // whole packet buffered (ReadOnePacket grows _rxBuffer if needed)
                }
                Thread.Sleep(2);
            }
            return false;
        }

        private static bool ReadFully(Stream stream, byte[] buf, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buf, offset + got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private uint NextSendId()
        {
            unchecked { return ++_sendIdSeq; }
        }

        private static void DelayOrCancel(int ms, CancellationToken ct)
        {
            try { Thread.Sleep(ms); } catch { }
            if (ct.IsCancellationRequested) return;
        }
    }
}
