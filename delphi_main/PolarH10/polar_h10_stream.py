import asyncio

from bleak import BleakClient, BleakScanner
from pythonosc.udp_client import SimpleUDPClient

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


async def main() -> None:
    osc_client = SimpleUDPClient(OSC_HOST, OSC_PORT)

    print(f"Scanning for a device named '{DEVICE_NAME_PREFIX}*'...")
    device = await BleakScanner.find_device_by_filter(
        lambda d, adv: d.name is not None and d.name.startswith(DEVICE_NAME_PREFIX)
    )
    if device is None:
        print("No Polar H10 found. Check that the strap is worn (electrodes moist) and awake.")
        return

    print(f"Found {device.name} ({device.address}). Connecting...")
    async with BleakClient(device) as client:
        print("Connected. Subscribing to Heart Rate Measurement notifications...")
        await client.start_notify(
            HEART_RATE_MEASUREMENT_UUID,
            lambda sender, data: on_hr_notification(osc_client, sender, data),
        )

        try:
            await client.write_gatt_char(PMD_CONTROL_UUID, ACC_START_COMMAND, response=True)
            await client.start_notify(
                PMD_DATA_UUID,
                lambda sender, data: on_acc_notification(osc_client, sender, data),
            )
            print("Accelerometer stream started (200Hz, 16-bit, 2G).")
        except Exception as e:
            print(f"Could not start accelerometer stream (HR still works): {e}")

        print(f"Streaming to Unity at {OSC_HOST}:{OSC_PORT} — Ctrl+C to stop.\n")
        try:
            while True:
                await asyncio.sleep(1)
        except asyncio.CancelledError:
            pass


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
