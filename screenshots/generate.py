"""Renders store screenshots and a banner, styled by config.json.

1. Put screenshots in raw/<locale>/<name>.png (or .jpg, .jpeg, .webp), one
   <name> per entry in "captions" in config.json.
2. Run: python generate.py

Writes framed/<locale>/<name>.png (1080x1920) for every caption and
framed/banner_<locale>.png (1920x1080) for every locale, rendered with
headless Chrome or Edge. Templates are read from custom/ first, then
templates/, so a project can restyle a layout without touching this file.

StatefulBar change: "frame_size" ([width, height] in CSS px) and
"device_scale" in config.json override the frame size and the device
scale factor; the written PNG is frame_size x device_scale pixels.
"""

import base64
import html
import json
import mimetypes
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
RAW = ROOT / "raw"
OUT = ROOT / "framed"
CONFIG = ROOT / "config.json"
TEMPLATE_DIRS = [ROOT / "custom", ROOT / "templates"]

FRAME_SIZE = (1080, 1920)
BANNER_SIZE = (1920, 1080)
EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp"}
FULL_HEIGHT = [0, 100000]
RESERVED_KEYS = {"layout", "crop"}

CHROME_CANDIDATES = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "google-chrome",
    "chromium",
]


def find_chrome():
    for candidate in CHROME_CANDIDATES:
        path = shutil.which(candidate) or (candidate if Path(candidate).exists() else None)
        if path:
            return path
    raise SystemExit("Chrome or Edge not found")


def render(chrome, page_html, png_path, size, scale=1):
    png_path.parent.mkdir(parents=True, exist_ok=True)
    png_path.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "page.html"
        page.write_text(page_html, encoding="utf-8")
        result = subprocess.run(
            [
                chrome,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                f"--force-device-scale-factor={scale}",
                "--allow-file-access-from-files",
                # Gives fonts and the chroma key script time to finish.
                "--virtual-time-budget=3000",
                f"--window-size={size[0]},{size[1]}",
                f"--screenshot={png_path}",
                page.as_uri(),
            ],
            capture_output=True,
            text=True,
            errors="replace",
        )
    if not png_path.exists():
        raise SystemExit(f"Chrome did not write {png_path}:\n{result.stderr}")
    print(f"wrote {png_path.relative_to(ROOT)}")


def data_uri(path):
    """Inlines an image so canvas code can read its pixels."""
    mime = mimetypes.guess_type(path.name)[0] or "image/png"
    return f"data:{mime};base64,{base64.b64encode(path.read_bytes()).decode()}"


def template(name):
    for folder in TEMPLATE_DIRS:
        path = folder / name
        if path.exists():
            return path.read_text(encoding="utf-8")
    raise SystemExit(f"Template {name} not found in custom/ or templates/")


def fill(text, values):
    """Replaces every {{key}} in one pass, so inserted values are never
    scanned for placeholders themselves."""

    def value(match):
        key = match.group(1)
        if key not in values:
            print(f"  warning: no value for {{{{{key}}}}}")
            return match.group(0)
        return str(values[key])

    return re.sub(r"\{\{(\w+)\}\}", value, text)


def find_shot(locale, name):
    for path in sorted((RAW / locale).glob(f"{name}.*")):
        if path.suffix.lower() in EXTENSIONS:
            return path
    return None


def theme_css(config):
    """@font-face rules for "fonts" plus "theme" as CSS variables."""
    rules = [
        f'@font-face {{ font-family: "{font["family"]}"; '
        f'font-weight: {font.get("weight", 400)}; '
        f'font-style: {font.get("style", "normal")}; '
        f'src: url("{(ROOT / font["file"]).resolve().as_uri()}"); }}'
        for font in config.get("fonts", [])
    ]
    props = " ".join(f"--{key}: {value};" for key, value in config.get("theme", {}).items())
    rules.append(f":root {{ {props} }}")
    return "\n".join(rules)


def localized(value, locale):
    """A var is either one string or a {locale: string} map."""
    if isinstance(value, dict):
        value = value.get(locale, "")
    return html.escape(str(value))


def base_values(config, locale):
    source_width = config.get("source_width", 1080)
    values = {
        key: localized(value, locale)
        for key, value in config.get("vars", {}).items()
    }
    values.update(
        {key: data_uri(ROOT / path) for key, path in config.get("images", {}).items()}
    )
    values.update(
        theme=theme_css(config),
        chroma_key=template("chroma_key.js"),
        source_width=source_width,
        crop=f'{config.get("crop_top", 0) / source_width * 100:.4f}',
    )
    return values


def needed_shots(config, locale):
    names = [name for name, entry in config["captions"].items() if locale in entry]
    names += config.get("banner", {}).get("shots", [])
    return list(dict.fromkeys(names))


def frames(chrome, config, locale, base):
    locales = set(config["locales"])
    for name, entry in config["captions"].items():
        shot = find_shot(locale, name)
        caption = entry.get(locale)
        if shot is None or caption is None:
            continue
        title, subtitle = caption
        crop_top, crop_bottom = entry.get("crop", FULL_HEIGHT)
        values = dict(base)
        values.update(
            {
                key: localized(value, locale)
                for key, value in entry.items()
                if key not in RESERVED_KEYS | locales
            }
        )
        values.update(
            shot=data_uri(shot),
            title=html.escape(title),
            subtitle=html.escape(subtitle),
            crop_top=crop_top,
            crop_bottom=crop_bottom,
        )
        layout = entry.get("layout", "phone")
        page = fill(template(f"{layout}.html"), values)
        render(
            chrome,
            page,
            OUT / locale / f"{name}.png",
            tuple(config.get("frame_size", FRAME_SIZE)),
            config.get("device_scale", 1),
        )


def banner(chrome, config, locale, base):
    spec = config.get("banner")
    if not spec:
        return
    values = dict(base)
    for index, name in enumerate(spec["shots"], 1):
        shot = find_shot(locale, name)
        if shot is None:
            print(f"skip banner_{locale}: missing raw/{locale}/{name}")
            return
        entry = config["captions"].get(name, {})
        widget = entry.get("layout") == "widget"
        top, bottom = spec.get("widget_crop", entry.get("crop", FULL_HEIGHT))
        values[f"shot_{index}"] = data_uri(shot)
        values[f"kind_{index}"] = "widget" if widget else "app"
        values[f"crop_{index}"] = f"{top},{bottom}"
    page = fill(template(spec.get("template", "banner.html")), values)
    render(chrome, page, OUT / f"banner_{locale}.png", BANNER_SIZE)


def main():
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    missing = [
        f"raw/{locale}/{name}"
        for locale in config["locales"]
        for name in needed_shots(config, locale)
        if find_shot(locale, name) is None
    ]
    if missing:
        print("Missing screenshots (skipped):")
        print("\n".join(f"  {path}" for path in missing))

    chrome = find_chrome()
    for locale in config["locales"]:
        base = base_values(config, locale)
        frames(chrome, config, locale, base)
        banner(chrome, config, locale, base)


if __name__ == "__main__":
    main()
