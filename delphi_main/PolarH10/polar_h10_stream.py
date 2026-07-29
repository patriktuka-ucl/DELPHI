import argparse
import asyncio
import sys
import threading
import time

from bleak import BleakClient, BleakScanner
from pythonosc.udp_client import SimpleUDPClient

# When Unity launches this bridge it pipes our stdout/stderr into the Editor
# console. Python BLOCK-buffers a pipe by default, so nothing would appear until
# ~4KB accumulated — making the bridge look dead while it's actually retrying.
# Force line buffering so every status line shows up live in the console.
try:
    sys.stdout.reconfigure(line_buffering=True)
    sys.stderr.reconfigure(line_buffering=True)
except Exception:
    pass

# Only CONNECTION LIFECYCLE prints by default (scanning/connecting/connected/
# lost/retrying) — NOT per-sample sensor data. At 25-200Hz accelerometer rates,
# printing every sample (or even a 1/sec summary) means Unity's console piping
# (PolarH10OscConnection.StartPythonBridge -> Debug.Log per stdout line, on a
# background thread) becomes the dominant editor performance cost — measured at
# 267KB of GC allocation in a single frame. Pass --verbose to restore the old
# per-beat/per-second data prints for standalone debugging.
VERBOSE = False

DEVICE_NAME_PREFIX = "Polar H10"

# Hard-set strap BLE address. NOTE: passing this address to BleakClient does NOT
# by itself skip discovery on Windows — see the SCAN-FREE CONNECT section below
# for the measurement and what actually skips it.
# Set to "" to fall back to discovery by scan. Override with --address.
DEVICE_ADDRESS = "24:AC:AC:1D:A3:9C"  # Polar H10 1DA39C31

HEART_RATE_MEASUREMENT_UUID = "00002a37-0000-1000-8000-00805f9b34fb"

# Polar's proprietary PMD (Polar Measurement Data) service — not part of the
# standard BLE Heart Rate profile, needed for raw accelerometer (and ECG)
# access. UUIDs and frame format confirmed against Polar's own PMD spec and
# the bleakheart reference implementation, not guessed.
PMD_CONTROL_UUID = "fb005c81-02e7-f387-1cad-8acd2d8df0c8"
PMD_DATA_UUID = "fb005c82-02e7-f387-1cad-8acd2d8df0c8"

# The Polar H10's PMD accelerometer only accepts THESE sample rates — it is
# firmware-fixed, not arbitrary. A request for e.g. 30Hz is rejected outright,
# so we clamp down to the nearest supported rate at or below what's asked
# (30 -> 25). Unity passes the requested rate via --acc-rate.
SUPPORTED_ACC_RATES_HZ = (25, 50, 100, 200)
DEFAULT_ACC_RATE_HZ = 25


def nearest_supported_acc_rate(requested_hz: int) -> int:
    at_or_below = [r for r in SUPPORTED_ACC_RATES_HZ if r <= requested_hz]
    return max(at_or_below) if at_or_below else min(SUPPORTED_ACC_RATES_HZ)


def build_acc_start_command(rate_hz: int, resolution_bits: int = 16,
                            range_g: int = 2) -> bytearray:
    """Start-ACC-stream PMD control frame: [start_op=0x02, type=ACC(0x02),
    (SAMPLE_RATE, len=1, LE16), (RESOLUTION, len=1, LE16), (RANGE, len=1, LE16)].
    Frame layout confirmed against Polar's PMD spec / bleakheart, not guessed."""
    return bytearray([
        0x02, 0x02,
        0x00, 0x01, rate_hz & 0xFF, (rate_hz >> 8) & 0xFF,                  # SAMPLE_RATE
        0x01, 0x01, resolution_bits & 0xFF, (resolution_bits >> 8) & 0xFF,  # RESOLUTION
        0x02, 0x01, range_g & 0xFF, (range_g >> 8) & 0xFF,                  # RANGE
    ])

# Must match PolarH10OscConnection's listenPort/addresses in Unity.
OSC_HOST = "127.0.0.1"
OSC_PORT = 9500
OSC_HR_ADDRESS = "/PolarH10/HR"
OSC_RR_ADDRESS = "/PolarH10/RR"
OSC_ACC_X_ADDRESS = "/PolarH10/AccX"
OSC_ACC_Y_ADDRESS = "/PolarH10/AccY"
OSC_ACC_Z_ADDRESS = "/PolarH10/AccZ"


def parse_hr_measurement(data: bytearray) -> tuple[int, list[float]]:
    flags = data[0]
    hr_is_16bit = flags & 0x01
    energy_expended_present = flags & 0x08
    rr_present = flags & 0x10

    offset = 1
    if hr_is_16bit:
        hr_bpm = int.from_bytes(data[offset : offset + 2], "little")
        offset += 2
    else:
        hr_bpm = data[offset]
        offset += 1

    if energy_expended_present:
        offset += 2

    rr_intervals_ms = []
    if rr_present:
        while offset + 1 < len(data):
            rr_raw = int.from_bytes(data[offset : offset + 2], "little")
            rr_intervals_ms.append(rr_raw / 1024.0 * 1000.0)
            offset += 2

    return hr_bpm, rr_intervals_ms


def on_hr_notification(osc_client: SimpleUDPClient, _, data: bytearray) -> None:
    hr_bpm, rr_intervals_ms = parse_hr_measurement(data)
    if VERBOSE:
        rr_str = ", ".join(f"{rr:.1f}ms" for rr in rr_intervals_ms) if rr_intervals_ms else "-"
        print(f"HR: {hr_bpm:3d} bpm | RR: {rr_str}")

    osc_client.send_message(OSC_HR_ADDRESS, float(hr_bpm))
    for rr_ms in rr_intervals_ms:
        osc_client.send_message(OSC_RR_ADDRESS, float(rr_ms))


def parse_acc_frame(data: bytearray) -> list[tuple[int, int, int]]:
    # PMD frame layout: byte 0 = measurement type, bytes 1-8 = 64-bit
    # timestamp (unused here), byte 9 = frame type. 0x01 is the plain
    # (uncompressed) frame Polar sends at these settings; other frame types
    # (e.g. delta-compressed) aren't handled here.
    frame_type = data[9]
    if frame_type != 0x01:
        return []

    samples = []
    for offset in range(10, len(data) - 5, 6):
        x_mg = int.from_bytes(data[offset : offset + 2], "little", signed=True)
        y_mg = int.from_bytes(data[offset + 2 : offset + 4], "little", signed=True)
        z_mg = int.from_bytes(data[offset + 4 : offset + 6], "little", signed=True)
        samples.append((x_mg, y_mg, z_mg))
    return samples


# ACC arrives far too fast to print per sample (25-200/s). VERBOSE mode prints a
# ONE-LINE summary once a second instead (latest x/y/z + measured samples/s) —
# off by default, since even that once/sec line means Unity's stdout->Debug.Log
# piping runs on a background thread continuously; see the VERBOSE comment above.
_acc_report = {"count": 0, "last": (0, 0, 0), "t": 0.0}

# Set on every real ACC frame — arm_accelerometer() below waits on this to know
# the stream ACTUALLY started (not just that the start command was ATT-acked).
_acc_frame_event: asyncio.Event | None = None


def on_acc_notification(osc_client: SimpleUDPClient, _, data: bytearray) -> None:
    samples = parse_acc_frame(data)
    if samples and _acc_frame_event is not None and not _acc_frame_event.is_set():
        _acc_frame_event.set()

    for x_mg, y_mg, z_mg in samples:
        osc_client.send_message(OSC_ACC_X_ADDRESS, float(x_mg))
        osc_client.send_message(OSC_ACC_Y_ADDRESS, float(y_mg))
        osc_client.send_message(OSC_ACC_Z_ADDRESS, float(z_mg))

    if not samples or not VERBOSE:
        return
    _acc_report["count"] += len(samples)
    _acc_report["last"] = samples[-1]

    now = time.monotonic()
    if _acc_report["t"] == 0.0:
        _acc_report["t"] = now
        return
    elapsed = now - _acc_report["t"]
    if elapsed >= 1.0:
        x_mg, y_mg, z_mg = _acc_report["last"]
        print(f"ACC: x={x_mg:6d} y={y_mg:6d} z={z_mg:6d} mG "
              f"| {_acc_report['count'] / elapsed:5.1f} samples/s")
        _acc_report["count"] = 0
        _acc_report["t"] = now


# ─────────────────────────────────────────────────────────────────────────────
# Arming the accelerometer is FAR flakier than HR on this Windows/WinRT stack.
# Root-caused by hand: writing the PMD "start ACC" command races the CCCD
# subscription actually propagating to the strap over the air. A fixed settle
# delay does NOT reliably fix it (measured: 2.0s succeeded once, then failed on
# an identical retry) — the only thing that reliably worked eventually was
# patient retrying, the same lesson that fixed HR's notify-enable flakiness.
# Since a full reconnect is expensive (~6-8s) and would needlessly tear down an
# otherwise-healthy HR/RR stream, this instead keeps retrying JUST the arm
# sequence in the background, on the live connection, until real ACC frames are
# CONFIRMED (not just "the write didn't throw") or the link drops.
# ─────────────────────────────────────────────────────────────────────────────
ACC_ARM_VERIFY_TIMEOUT_S = 3.0   # wait this long per attempt for a real frame
ACC_ARM_RETRY_DELAY_S = 3.0      # pause between arm attempts


def start_accelerometer(client, osc_client: SimpleUDPClient, acc_command: bytearray,
                        acc_rate_hz: int) -> "asyncio.Task":
    """Subscribes to PMD_DATA then repeatedly (re-)writes the start command
    until a real frame is confirmed via _acc_frame_event, retrying quietly in
    the background — HR/RR keep streaming unaffected the whole time. Returns
    the background Task; cancel it when the connection is torn down."""
    global _acc_frame_event
    _acc_frame_event = asyncio.Event()

    async def _loop():
        try:
            await _subscribe(client, PMD_DATA_UUID,
                             lambda s, d: on_acc_notification(osc_client, s, d), "ACC")
        except Exception as e:
            print(f"Accelerometer subscribe failed permanently (HR/RR still fine): {e}")
            return

        attempt = 0
        while client.is_connected:
            attempt += 1
            _acc_frame_event.clear()
            try:
                await client.write_gatt_char(PMD_CONTROL_UUID, acc_command, response=True)
            except Exception as e:
                if not client.is_connected:
                    return
                if attempt == 1:
                    print(f"  ACC: start-command write failed, retrying quietly "
                          f"in the background (HR/RR unaffected)...")
                await asyncio.sleep(ACC_ARM_RETRY_DELAY_S)
                continue

            try:
                await asyncio.wait_for(_acc_frame_event.wait(), timeout=ACC_ARM_VERIFY_TIMEOUT_S)
                print(f"Accelerometer stream confirmed ({acc_rate_hz}Hz, 16-bit, 2G)"
                      + (f" after {attempt} attempts." if attempt > 1 else "."))
                return
            except asyncio.TimeoutError:
                if attempt == 1:
                    print(f"  ACC: armed but no data yet — retrying quietly in the "
                          f"background every {ACC_ARM_RETRY_DELAY_S:.0f}s "
                          f"(HR/RR unaffected)...")

    return asyncio.create_task(_loop())


# On Windows the WinRT stack intermittently fails start_notify with "Operation
# aborted" (E_ABORT), and connecting by a bare address sometimes yields an empty
# GATT table (CharacteristicNotFound) — both TRANSIENT, worse when the Bluetooth
# adapter is contended (VR Lighthouse base stations share the radio) and on older
# Intel BT drivers. So this bridge never gives up: it re-SCANS and connects by
# the freshly-discovered device object each attempt (which gives WinRT a reliable
# service table), retries through the aborts, and reconnects if the link drops.
CONNECT_TIMEOUT_S = 15.0
NOTIFY_RETRIES = 12
NOTIFY_RETRY_DELAY_S = 0.3
RECONNECT_DELAY_S = 0.5
SCAN_TIMEOUT_S = 6.0          # one-time discovery scan (by-address main)
RETRY_DELAY_S = 2.0           # wait between discovery attempts while asleep
RESCAN_AFTER_FAILURES = 5     # rediscover by scan after this many address fails


# ─────────────────────────────────────────────────────────────────────────────
# SCAN-FREE CONNECT — the fix for "sometimes 2s, sometimes 45s and still nothing"
#
# MEASURED, not guessed: on the WinRT backend BleakClient("24:AC:...").connect()
# does NOT connect by address. bleak/backends/winrt/client.py::connect() starts
# with `if self._device_info is None: await BleakScanner.find_device_by_address(
# address, timeout=timeout)` — i.e. an address string still costs a full
# discovery scan, and if the strap isn't advertising in that window it burns the
# ENTIRE timeout and raises BleakDeviceNotFoundError. Timed on this rig with the
# strap not advertising: failure at exactly 15.03s. With RECONNECT_DELAY_S that
# is a 15.5s cycle — so "45 seconds and still not connected" is precisely three
# failed scans, and "2 seconds" is the case where the strap happened to be
# advertising. Same code was instant on the Mac because CoreBluetooth queues a
# connection request against a known peripheral instead of requiring an advert.
#
# The strap stops being discoverable whenever Windows' own stack is holding the
# bond (it is paired here — two BTHLE\DEV_24ACAC1DA39C nodes exist, one per
# radio) or while it is still tearing down the link from the previous run.
#
# Fix: preset `_device_info` so bleak's scan branch is skipped and WinRT goes
# straight to BluetoothLEDevice.from_bluetooth_address_async(). For a bonded
# device that resolves from the OS device DB with no advert needed, and the
# connection then queues in the Bluetooth stack exactly like CoreBluetooth does
# — it completes the moment the strap is reachable, instead of us gambling on
# hitting an advertising window. Bounded by FAST_CONNECT_TIMEOUT_S so a genuinely
# absent strap still falls back to a real scan rather than hanging forever.
# ─────────────────────────────────────────────────────────────────────────────
FAST_CONNECT_TIMEOUT_S = 25.0          # bounded wait on the queued scan-free connect
FAST_FAILURES_BEFORE_SCAN = 2          # then verify by scan that it's really there


def _address_to_int(address: str) -> int:
    return int(address.replace(":", "").replace("-", ""), 16)


def make_scan_free_client(address: str):
    """BleakClient for `address` with bleak's mandatory discovery scan bypassed.

    Returns (client, is_scan_free). Touching `_backend._device_info` is reaching
    into bleak internals, so it is version-guarded: if the attribute isn't there
    (bleak API moved), we hand back a plain client and the caller just pays for
    the scan as before rather than crashing. requirements.txt pins bleak==0.22.3,
    which is what this was verified against."""
    client = BleakClient(address, timeout=CONNECT_TIMEOUT_S)
    backend = getattr(client, "_backend", None)
    if backend is None or not hasattr(backend, "_device_info"):
        return client, False
    try:
        backend._device_info = _address_to_int(address)
        return client, True
    except Exception:
        return BleakClient(address, timeout=CONNECT_TIMEOUT_S), False


# ─────────────────────────────────────────────────────────────────────────────
# GRACEFUL SHUTDOWN — the other half of the slow-reconnect problem.
#
# Unity used to stop this bridge with Process.Kill(), which gives us no chance to
# call client.disconnect(). The strap then keeps the dead link open until its own
# supervision timeout expires (seconds), during which it neither advertises nor
# accepts a new central — so the NEXT Play press lands straight in the failing
# scan path above. Unity now closes our stdin (or writes "quit") instead, and we
# tear the BLE link down properly before exiting.
#
# Only armed with --stdin-shutdown: when run standalone from a terminal, stdin
# may already be at EOF, which would otherwise make us exit immediately.
# ─────────────────────────────────────────────────────────────────────────────
_shutdown_event: "asyncio.Event | None" = None


def arm_stdin_shutdown(loop: asyncio.AbstractEventLoop) -> None:
    global _shutdown_event
    _shutdown_event = asyncio.Event()

    def _reader():
        try:
            for line in sys.stdin:            # returns on EOF = parent closed stdin
                if line.strip().lower() in ("quit", "stop", "exit"):
                    break
        except Exception:
            pass
        loop.call_soon_threadsafe(_shutdown_event.set)

    threading.Thread(target=_reader, daemon=True, name="stdin-shutdown").start()


def shutdown_requested() -> bool:
    return _shutdown_event is not None and _shutdown_event.is_set()


class ShutdownRequested(Exception):
    """Raised to unwind out of a pending connect/sleep the moment Unity asks us
    to stop. Without this the bridge would keep a stop waiting for its in-flight
    connect to time out (up to FAST_CONNECT_TIMEOUT_S) — far longer than Unity's
    exit budget, so it would get killed anyway and we'd lose the clean BLE
    disconnect this whole path exists to guarantee."""


async def await_or_shutdown(coro, timeout: float):
    """Await `coro` with a timeout, abandoning it if a shutdown is requested."""
    task = asyncio.ensure_future(coro)
    waiters = [task]
    if _shutdown_event is not None:
        waiters.append(asyncio.ensure_future(_shutdown_event.wait()))

    done, pending = await asyncio.wait(waiters, timeout=timeout,
                                       return_when=asyncio.FIRST_COMPLETED)
    for p in pending:
        p.cancel()

    if task in done:
        return task.result()          # re-raises whatever the connect raised
    if not done:
        raise asyncio.TimeoutError
    raise ShutdownRequested


async def sleep_or_shutdown(delay: float) -> None:
    """asyncio.sleep that returns early (as ShutdownRequested) on stop."""
    if _shutdown_event is None:
        await asyncio.sleep(delay)
        return
    try:
        await asyncio.wait_for(_shutdown_event.wait(), timeout=delay)
    except asyncio.TimeoutError:
        return
    raise ShutdownRequested


async def _subscribe(client, uuid, callback, label):
    """Enable notifications, retrying the WinRT transient 'Operation aborted'
    IN PLACE on the live connection. Scan/connect/discovery already succeeded,
    so as long as the link is still up we just re-issue start_notify (cheap)
    instead of paying for a full reconnect. Raises only if the link actually
    drops (then the caller reconnects)."""
    for attempt in range(1, NOTIFY_RETRIES + 1):
        try:
            await client.start_notify(uuid, callback)
            print(f"  {label} notifications on"
                  + (f" (after {attempt} tries)." if attempt > 1 else "."))
            return
        except Exception as e:
            if not client.is_connected:
                raise  # link dropped — reconnect at the outer loop
            if attempt == 1:
                print(f"  {label}: retrying through transient aborts...")
            await asyncio.sleep(NOTIFY_RETRY_DELAY_S)
    raise RuntimeError(f"{label} notify failed {NOTIFY_RETRIES}x on a live link")


# ─────────────────────────────────────────────────────────────────────────────
# PRESERVED FALLBACK — the original scan-every-attempt version. Kept intact (NOT
# deleted) so we can revert instantly: to go back to it, change `main()` to
# `main_scan_based()` in the __main__ block at the very bottom of this file.
# ─────────────────────────────────────────────────────────────────────────────
async def main_scan_based(acc_rate_hz: int = DEFAULT_ACC_RATE_HZ) -> None:
    osc_client = SimpleUDPClient(OSC_HOST, OSC_PORT)
    acc_command = build_acc_start_command(acc_rate_hz)
    print(f"Polar H10 bridge (scan-based) — target '{DEVICE_NAME_PREFIX}*', OSC -> {OSC_HOST}:{OSC_PORT}")

    while True:
        # Scan every attempt: connecting by the discovered device object (not a
        # bare address) is what makes WinRT return a full GATT table reliably.
        print("Scanning for the strap...")
        device = await BleakScanner.find_device_by_filter(
            lambda d, adv: d.name is not None and d.name.startswith(DEVICE_NAME_PREFIX),
            timeout=8.0,
        )
        if device is None:
            print("  Not advertising — is it worn, electrodes moist, and awake? Retrying in 3s.")
            await asyncio.sleep(3.0)
            continue

        print(f"  Found {device.name} [{device.address}]. Connecting...")
        t0 = time.monotonic()
        client = BleakClient(device, timeout=CONNECT_TIMEOUT_S)
        acc_task = None
        try:
            await client.connect()
            await _subscribe(client, HEART_RATE_MEASUREMENT_UUID,
                             lambda s, d: on_hr_notification(osc_client, s, d), "HR")

            # Accelerometer arms itself in the background and keeps retrying
            # until confirmed — see start_accelerometer's docstring for why.
            acc_task = start_accelerometer(client, osc_client, acc_command, acc_rate_hz)

            print(f"\nStreaming to Unity at {OSC_HOST}:{OSC_PORT} "
                  f"(connected in {time.monotonic() - t0:.1f}s) — Ctrl+C to stop.\n")
            while client.is_connected:
                await asyncio.sleep(1.0)
            print("Link dropped — reconnecting...")

        except Exception as e:
            print(f"Connect/subscribe failed ({type(e).__name__}: {e}) — retrying.")
            await asyncio.sleep(RECONNECT_DELAY_S)
        finally:
            if acc_task is not None:
                acc_task.cancel()
            try:
                await client.disconnect()
            except Exception:
                pass


# ─────────────────────────────────────────────────────────────────────────────
# ACTIVE — scan-free connect. Connects straight through the OS bond with no
# discovery scan at all (see the SCAN-FREE CONNECT block above for why an address
# string alone did NOT achieve that, and the 15.03s measurement behind it), so
# startup no longer depends on catching an advertising window. If the scan-free
# path times out repeatedly the strap may genuinely be off/out of range, so we
# fall back to a real scan — and then connect using the discovered BLEDevice
# OBJECT, which also carries its own device info and skips bleak's internal scan.
# ─────────────────────────────────────────────────────────────────────────────
async def main(acc_rate_hz: int = DEFAULT_ACC_RATE_HZ, device_address: str = "",
               allow_scan_free: bool = True) -> None:
    osc_client = SimpleUDPClient(OSC_HOST, OSC_PORT)
    acc_command = build_acc_start_command(acc_rate_hz)
    print(f"Polar H10 bridge — ACC {acc_rate_hz}Hz, OSC -> {OSC_HOST}:{OSC_PORT}")

    address = device_address or DEVICE_ADDRESS or None
    target = None            # a discovered BLEDevice, when we had to fall back to scanning
    fast_failures = 0
    while True:
        if shutdown_requested():
            return

        # Discovery only happens when we have no address at all, or the scan-free
        # path has failed enough times to suspect the strap really isn't there.
        if address is None and target is None:
            print("Discovering the strap (scan)...")
            target = await BleakScanner.find_device_by_filter(
                lambda d, adv: d.name is not None and d.name.startswith(DEVICE_NAME_PREFIX),
                timeout=SCAN_TIMEOUT_S,
            )
            if target is None:
                print(f"  Not advertising — worn, electrodes moist, awake? Retrying in {RETRY_DELAY_S:.0f}s.")
                try:
                    await sleep_or_shutdown(RETRY_DELAY_S)
                except ShutdownRequested:
                    return
                continue
            address = target.address
            print(f"  Found {target.name} [{address}].")

        if target is not None:
            client, scan_free = BleakClient(target, timeout=CONNECT_TIMEOUT_S), True
            print(f"Connecting to {address} (discovered device)...")
        elif allow_scan_free:
            client, scan_free = make_scan_free_client(address)
            print(f"Connecting to {address} ({'scan-free' if scan_free else 'via scan — bleak internals moved'})...")
        else:
            client, scan_free = BleakClient(address, timeout=CONNECT_TIMEOUT_S), False
            print(f"Connecting to {address} (scan-based)...")

        t0 = time.monotonic()
        acc_task = None
        try:
            # The scan-free connect QUEUES in the Windows stack and completes as
            # soon as the strap is reachable, so it must be bounded — otherwise a
            # strap that is simply switched off would wait forever.
            await await_or_shutdown(client.connect(), timeout=FAST_CONNECT_TIMEOUT_S)
            await _subscribe(client, HEART_RATE_MEASUREMENT_UUID,
                             lambda s, d: on_hr_notification(osc_client, s, d), "HR")

            # Accelerometer arms itself in the background and keeps retrying
            # until confirmed — see start_accelerometer's docstring for why.
            acc_task = start_accelerometer(client, osc_client, acc_command, acc_rate_hz)

            fast_failures = 0
            target = None    # bond is good; go back to the scan-free path on reconnect
            print(f"\nStreaming to Unity at {OSC_HOST}:{OSC_PORT} "
                  f"(connected in {time.monotonic() - t0:.1f}s) — Ctrl+C to stop.\n")
            while client.is_connected:
                if shutdown_requested():
                    print("Shutdown requested — disconnecting from the strap cleanly.")
                    return
                await asyncio.sleep(0.25)
            print("Link dropped — reconnecting...")

        except ShutdownRequested:
            print("Shutdown requested — disconnecting from the strap cleanly.")
            return
        except asyncio.TimeoutError:
            fast_failures += 1
            print(f"No connection after {FAST_CONNECT_TIMEOUT_S:.0f}s — is the strap worn "
                  f"and are the electrodes moist? Retrying.")
            if fast_failures >= FAST_FAILURES_BEFORE_SCAN:
                print("  Falling back to a discovery scan to confirm it's really there.")
                address, target, fast_failures = None, None, 0
        except Exception as e:
            fast_failures += 1
            print(f"Connect/subscribe failed ({type(e).__name__}: {e}) — retrying.")
            # BleakDeviceNotFoundError here means the OS has no bond for this
            # address (fresh strap / unpaired), and an empty GATT table shows up
            # as CharacteristicNotFound — both are cured by rediscovering.
            if fast_failures >= FAST_FAILURES_BEFORE_SCAN:
                address, target, fast_failures = None, None, 0
            try:
                await sleep_or_shutdown(RECONNECT_DELAY_S)
            except ShutdownRequested:
                return
        finally:
            if acc_task is not None:
                acc_task.cancel()
            # Always tear the link down properly: a half-open connection is what
            # makes the NEXT connect slow.
            try:
                await client.disconnect()
            except Exception:
                pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Polar H10 -> OSC bridge for DELPHI.")
    parser.add_argument("--acc-rate", type=int, default=DEFAULT_ACC_RATE_HZ,
                        help="Requested accelerometer rate in Hz. Clamped to the "
                             "nearest H10-supported rate (25/50/100/200). Unity "
                             "passes DelphiManager's IMU rate here.")
    parser.add_argument("--address", type=str, default="",
                        help="Strap BLE address; skips the discovery scan.")
    parser.add_argument("--scan-based", action="store_true",
                        help="Use the preserved scan-every-attempt implementation.")
    parser.add_argument("--no-scan-free", action="store_true",
                        help="Disable the scan-free connect and let bleak do its "
                             "internal discovery scan (the slow path). Diagnostics only.")
    parser.add_argument("--stdin-shutdown", action="store_true",
                        help="Exit gracefully when stdin closes or receives 'quit'. "
                             "Unity passes this so stopping Play disconnects the "
                             "strap cleanly instead of killing the link mid-connection.")
    parser.add_argument("--verbose", action="store_true",
                        help="Print per-beat HR/RR and a once/sec ACC summary. Off "
                             "by default — even the once/sec ACC line means Unity's "
                             "console keeps piping data continuously, which measurably "
                             "hurts editor framerate. Use only for standalone debugging.")
    args = parser.parse_args()

    VERBOSE = args.verbose
    rate = nearest_supported_acc_rate(args.acc_rate)
    if rate != args.acc_rate:
        print(f"Note: the H10 only supports {SUPPORTED_ACC_RATES_HZ} Hz — "
              f"requested {args.acc_rate}Hz, using {rate}Hz.")

    async def _run():
        if args.stdin_shutdown:
            arm_stdin_shutdown(asyncio.get_running_loop())
        # REVERT to the old behaviour any time with --scan-based.
        if args.scan_based:
            await main_scan_based(rate)
        else:
            await main(rate, args.address, allow_scan_free=not args.no_scan_free)

    try:
        asyncio.run(_run())
    except KeyboardInterrupt:
        print("\nStopped.")
