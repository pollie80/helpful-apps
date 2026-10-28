"""DS4 battery tray icon.

Shows the DualShock 4 battery percentage as a number in the Windows
system tray, polling the controller over HID (Bluetooth or USB).
Run with pythonw.exe to avoid a console window.
"""

import json
import threading
import time
from pathlib import Path

import hid
import pystray
from PIL import Image, ImageDraw, ImageFont

VID = 0x054C
PIDS = (0x09CC, 0x05C4, 0x0BA0)  # DS4 v2, DS4 v1, Sony wireless adapter

CONFIG_PATH = Path(__file__).with_name("ds4-battery-config.json")


def load_controller_names():
    """Friendly names per controller serial (Bluetooth MAC) from the config
    file next to this script. Unknown serials show the raw ID instead, and a
    missing or unreadable file just means no names."""
    try:
        with open(CONFIG_PATH, encoding="utf-8") as f:
            return json.load(f).get("controller_names", {})
    except (OSError, ValueError, AttributeError):
        return {}


CONTROLLER_NAMES = load_controller_names()
POLL_SECONDS = 30
FONT_PATH = r"C:\Windows\Fonts\segoeuib.ttf"  # Segoe UI Bold
ICON_SIZE = 64


def read_battery():
    """Return (percent, charging, serial) or None if no controller responds."""
    try:
        devices = hid.enumerate(VID)
    except OSError:
        return None
    for info in devices:
        if info["product_id"] not in PIDS:
            continue
        try:
            dev = hid.device()
            dev.open_path(info["path"])
        except OSError:
            continue
        try:
            # Reading feature report 0x05 switches a Bluetooth DS4 into
            # extended mode so it sends 0x11 reports, which carry battery
            # data. Harmless over USB.
            try:
                dev.get_feature_report(0x05, 41)
            except OSError:
                pass
            data = dev.read(78, timeout_ms=2000)
            if not data:
                continue
            if data[0] == 0x11:  # Bluetooth extended report
                b = data[32]
            elif data[0] == 0x01 and len(data) >= 34:  # USB report
                b = data[30]
            else:
                continue
            charging = bool(b & 0x10)
            level = b & 0x0F
            if charging:
                pct = 100 if level > 10 else min(level, 10) * 10
            else:
                pct = min((level + 1) * 10, 100)
            serial = (info.get("serial_number") or "").upper()
            return pct, charging, serial
        except OSError:
            continue
        finally:
            dev.close()
    return None


def draw_battery_symbol(draw, x, y, w, h, pct, charging, color):
    """Small vertical battery: outline, proportional fill, bolt when charging."""
    tip_w, tip_h = 6, 4
    draw.rectangle(
        [x + (w - tip_w) / 2, y, x + (w + tip_w) / 2, y + tip_h], fill=color
    )
    body_top = y + tip_h + 1
    body_bottom = y + h
    draw.rounded_rectangle(
        [x, body_top, x + w, body_bottom], radius=3, outline=color, width=2
    )
    if pct:
        inner_top = body_top + 4
        inner_bottom = body_bottom - 4
        fill_h = (inner_bottom - inner_top) * min(pct, 100) / 100
        if fill_h >= 1:
            draw.rectangle(
                [x + 4, inner_bottom - fill_h, x + w - 4, inner_bottom], fill=color
            )
    if charging:
        bx, by = x + 2, body_top + 2
        bw, bh = w - 4, body_bottom - body_top - 4
        bolt = [
            (bx + 0.65 * bw, by),
            (bx + 0.25 * bw, by + 0.58 * bh),
            (bx + 0.50 * bw, by + 0.58 * bh),
            (bx + 0.35 * bw, by + bh),
            (bx + 0.80 * bw, by + 0.42 * bh),
            (bx + 0.55 * bw, by + 0.42 * bh),
        ]
        draw.polygon(bolt, fill=(255, 215, 60, 255), outline=(30, 30, 30, 255))


def make_controller_icon(color):
    """Gamepad silhouette shown while no controller is connected."""
    s = 4  # draw 4x and downscale for smooth edges
    big = Image.new("RGBA", (ICON_SIZE * s, ICON_SIZE * s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(big)

    def box(*v):
        return [c * s for c in v]

    draw.rounded_rectangle(box(6, 18, 58, 42), radius=10 * s, fill=color)
    draw.ellipse(box(3, 26, 24, 58), fill=color)   # left grip
    draw.ellipse(box(40, 26, 61, 58), fill=color)  # right grip
    cut = (0, 0, 0, 0)  # punch the d-pad and buttons out of the body
    draw.rectangle(box(16, 24, 20, 36), fill=cut)
    draw.rectangle(box(12, 28, 24, 32), fill=cut)
    for cx, cy in ((46, 24), (46, 36), (40, 30), (52, 30)):
        draw.ellipse(box(cx - 2.5, cy - 2.5, cx + 2.5, cy + 2.5), fill=cut)
    return big.resize((ICON_SIZE, ICON_SIZE), Image.LANCZOS)


def make_icon_image(pct, charging):
    if pct is None:
        return make_controller_icon((140, 140, 140, 255))
    img = Image.new("RGBA", (ICON_SIZE, ICON_SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    text = str(pct)
    if charging:
        color = (80, 220, 120, 255)   # green while charging
    elif pct <= 15:
        color = (235, 70, 70, 255)    # red
    elif pct <= 30:
        color = (240, 180, 50, 255)   # amber
    else:
        color = (255, 255, 255, 255)  # white
    sym_w, sym_h, gap = 14, 36, 2
    # Shrink the font until number + symbol fit inside the icon.
    font_size = 36 if len(text) >= 3 else 52
    while True:
        try:
            font = ImageFont.truetype(FONT_PATH, font_size)
        except OSError:
            font = ImageFont.load_default()
            break
        left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
        if (right - left) + gap + sym_w <= ICON_SIZE or font_size <= 18:
            break
        font_size -= 2
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    text_w, text_h = right - left, bottom - top
    x0 = (ICON_SIZE - (text_w + gap + sym_w)) / 2
    draw.text(
        (x0 - left, (ICON_SIZE - text_h) / 2 - top), text, font=font, fill=color
    )
    draw_battery_symbol(
        draw,
        x0 + text_w + gap,
        (ICON_SIZE - sym_h) / 2,
        sym_w,
        sym_h,
        pct,
        charging,
        color,
    )
    return img


def update(icon):
    result = read_battery()
    if result is None:
        icon.icon = make_icon_image(None, False)
        icon.title = "PS4 controller: not connected"
    else:
        pct, charging, serial = result
        icon.icon = make_icon_image(pct, charging)
        state = "charging" if charging else "on battery"
        title = f"PS4 controller: {pct}% ({state})"
        if serial:
            name = CONTROLLER_NAMES.get(serial, f"ID: {serial}")
            title += f"\n{name}"
        icon.title = title


def poll_loop(icon):
    while True:
        try:
            update(icon)
        except Exception:
            pass  # never let a bad read kill the tray icon
        time.sleep(POLL_SECONDS)


def on_refresh(icon, item):
    threading.Thread(target=update, args=(icon,), daemon=True).start()


def setup(icon):
    icon.visible = True
    threading.Thread(target=poll_loop, args=(icon,), daemon=True).start()


def main():
    icon = pystray.Icon(
        "ds4battery",
        make_icon_image(None, False),
        "PS4 controller: reading...",
        menu=pystray.Menu(
            pystray.MenuItem("Refresh now", on_refresh),
            pystray.MenuItem("Exit", lambda icon, item: icon.stop()),
        ),
    )
    icon.run(setup)


if __name__ == "__main__":
    main()
