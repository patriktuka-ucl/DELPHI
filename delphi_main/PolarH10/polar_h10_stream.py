import asyncio

from bleak import BleakClient, BleakScanner
from pythonosc.udp_client import SimpleUDPClient

DEVICE_NAME_PREFIX = "Polar H10"
HEART_RATE_MEASUREMENT_UUID = "00002a37-0000-1000-8000-00805f9b34fb"

# Must match PolarH10OscConnection's listenPort/hrAddress/rrAddress in Unity.
OSC_HOST = "127.0.0.1"
OSC_PORT = 9500
OSC_HR_ADDRESS = "/PolarH10/HR"
OSC_RR_ADDRESS = "/PolarH10/RR"


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
