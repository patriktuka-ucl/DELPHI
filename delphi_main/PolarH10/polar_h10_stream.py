import asyncio
import sys
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

DEVICE_NAME_PREFIX = "Polar H10"
HEART_RATE_MEASUREMENT_UUID = "00002a37-0000-1000-8000-00805f9b34fb"

# Polar's proprietary PMD (Polar Measurement Data) service — not part of the
# standard BLE Heart Rate profile, needed for raw accelerometer (and ECG)
# access. UUIDs and frame format confirmed against Polar's own PMD spec and
# the bleakheart reference implementation, not guessed.
PMD_CONTROL_UUID = "fb005c81-02e7-f387-1cad-8acd2d8df0c8"
PMD_DATA_UUID = "fb005c82-02e7-f387-1cad-8acd2d8df0c8"

# Start-ACC-stream command: [start_op=0x02, type=ACC(0x02),
#   (SAMPLE_RATE, len=1, 200Hz LE16), (RESOLUTION, len=1, 16-bit LE16),
#   (RANGE, len=1, 2G LE16)] — 200Hz/16-bit/2G is bleakheart's tested
# default; H10 also supports 4G/8G range and 25/50/100Hz if 2G ever clips.
ACC_START_COMMAND = bytearray([0x02, 0x02, 0x00, 0x01, 0xC8, 0x00, 0x01, 0x01, 0x10, 0x00, 0x02, 0x01, 0x02, 0x00])

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


def on_acc_notification(osc_client: SimpleUDPClient, _, data: bytearray) -> None:
    for x_mg, y_mg, z_mg in parse_acc_frame(data):
        osc_client.send_message(OSC_ACC_X_ADDRESS, float(x_mg))
        osc_client.send_message(OSC_ACC_Y_ADDRESS, float(y_mg))
        osc_client.send_message(OSC_ACC_Z_ADDRESS, float(z_mg))


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


async def main() -> None:
    osc_client = SimpleUDPClient(OSC_HOST, OSC_PORT)
    print(f"Polar H10 bridge — target '{DEVICE_NAME_PREFIX}*', OSC -> {OSC_HOST}:{OSC_PORT}")

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
        try:
            await client.connect()
            await _subscribe(client, HEART_RATE_MEASUREMENT_UUID,
                             lambda s, d: on_hr_notification(osc_client, s, d), "HR")

            # Accelerometer is best-effort — HR/RR are the priority.
            try:
                await client.write_gatt_char(PMD_CONTROL_UUID, ACC_START_COMMAND, response=True)
                await _subscribe(client, PMD_DATA_UUID,
                                 lambda s, d: on_acc_notification(osc_client, s, d), "ACC")
                print("Accelerometer stream started (200Hz, 16-bit, 2G).")
            except Exception as e:
                print(f"Accelerometer unavailable (HR/RR still fine): {type(e).__name__}: {e}")

            print(f"\nStreaming to Unity at {OSC_HOST}:{OSC_PORT} "
                  f"(connected in {time.monotonic() - t0:.1f}s) — Ctrl+C to stop.\n")
            while client.is_connected:
                await asyncio.sleep(1.0)
            print("Link dropped — reconnecting...")

        except Exception as e:
            print(f"Connect/subscribe failed ({type(e).__name__}: {e}) — retrying.")
            await asyncio.sleep(RECONNECT_DELAY_S)
        finally:
            try:
                await client.disconnect()
            except Exception:
                pass


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
